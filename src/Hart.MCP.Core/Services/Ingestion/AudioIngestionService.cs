using Hart.MCP.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Audio ingestion service.
/// 
/// REPRESENTATION:
/// - Each sample is a constant atom (16-bit signed PCM → uint32 with sign encoding)
/// - Frames (chunks of samples) are compositions
/// - Audio track is a composition of frame compositions
/// - Temporal structure preserved: sequential samples maintain Hilbert locality
/// 
/// LOSSLESS: Original audio exactly reconstructable from atoms.
/// </summary>
public class AudioIngestionService : IngestionServiceBase, IIngestionService<AudioData>
{
    private const int SAMPLES_PER_FRAME = 1024; // ~23ms at 44.1kHz

    public AudioIngestionService(HartDbContext context, ILogger<AudioIngestionService>? logger = null)
        : base(context, logger) { }

    public async Task<long> IngestAsync(AudioData audio, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.SampleRate <= 0)
            throw new ArgumentException("Sample rate must be positive");
        if (audio.Samples == null || audio.Samples.Length == 0)
            throw new ArgumentException("Samples cannot be empty");

        Logger?.LogInformation("Ingesting audio: {SampleCount} samples, {SampleRate}Hz, {Channels} channels",
            audio.Samples.Length, audio.SampleRate, audio.Channels);

        // ============================================
        // PHASE 1: Extract ALL unique samples (memory only)
        // ============================================
        var uniqueSamples = new HashSet<short>(audio.Samples);
        var uniqueEncoded = uniqueSamples.Select(s => (uint)(s + 32768)).ToArray();
        Logger?.LogDebug("Found {UniqueCount} unique samples", uniqueEncoded.Length);

        // ============================================
        // PHASE 2: BULK create ALL sample constants
        // ============================================
        var sampleLookup = await BulkGetOrCreateConstantsAsync(
            uniqueEncoded,
            SEED_TYPE_INTEGER,
            ct);

        // ============================================
        // PHASE 3: Build frame compositions (using lookup, minimal DB ops)
        // ============================================
        var frameCompositionIds = new List<long>();
        int totalSamples = audio.Samples.Length;
        int frameCount = (totalSamples + SAMPLES_PER_FRAME - 1) / SAMPLES_PER_FRAME;

        for (int f = 0; f < frameCount; f++)
        {
            int start = f * SAMPLES_PER_FRAME;
            int length = Math.Min(SAMPLES_PER_FRAME, totalSamples - start);

            var frameSamples = new short[length];
            Array.Copy(audio.Samples, start, frameSamples, 0, length);

            // RLE compress the frame
            var (refs, mults) = CompressFrame(frameSamples);

            // Map sample values to constant IDs using dictionary (O(1) per sample, NO DB)
            var sampleConstantIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                uint encoded = (uint)(refs[i] + 32768);
                sampleConstantIds[i] = sampleLookup[encoded];
            }

            // Create frame composition from constants
            var frameId = await CreateCompositionFromConstantsAsync(sampleConstantIds, mults, null, ct);
            frameCompositionIds.Add(frameId);
        }

        // ============================================
        // PHASE 4: Create audio track composition (compositions of compositions)
        // ============================================
        var frameChildren = frameCompositionIds.Select(id => (id, isConstant: false)).ToArray();
        var trackId = await CreateCompositionAsync(
            frameChildren,
            Enumerable.Repeat(1, frameCompositionIds.Count).ToArray(),
            null,
            ct
        );

        // Store metadata as composition: [track, sampleRate, channels, bitsPerSample]
        var sampleRateConstant = await GetOrCreateConstantAsync((uint)audio.SampleRate, SEED_TYPE_INTEGER, ct);
        var channelsConstant = await GetOrCreateConstantAsync((uint)audio.Channels, SEED_TYPE_INTEGER, ct);
        var bitsConstant = await GetOrCreateConstantAsync((uint)audio.BitsPerSample, SEED_TYPE_INTEGER, ct);

        var metaChildren = new (long id, bool isConstant)[] {
            (trackId, false),
            (sampleRateConstant, true),
            (channelsConstant, true),
            (bitsConstant, true)
        };
        var metaId = await CreateCompositionAsync(
            metaChildren,
            new[] { 1, 1, 1, 1 },
            null,
            ct
        );

        Logger?.LogInformation("Ingested audio as composition {CompositionId} ({FrameCount} frames)", metaId, frameCount);
        return metaId;
    }

    public async Task<AudioData> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        // Get meta composition relations via Relation table
        var metaRelations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (metaRelations.Count != 4)
            throw new InvalidOperationException($"Invalid audio meta composition {compositionId}");

        var trackCompositionId = metaRelations[0].ChildCompositionId!.Value;
        var metaConstantIds = metaRelations.Skip(1).Select(r => r.ChildConstantId!.Value).ToList();
        var metaConstants = await Context.Constants
            .Where(c => metaConstantIds.Contains(c.Id))
            .ToListAsync(ct);

        var sampleRate = (int)metaConstants.First(c => c.Id == metaRelations[1].ChildConstantId!.Value).SeedValue;
        var channels = (int)metaConstants.First(c => c.Id == metaRelations[2].ChildConstantId!.Value).SeedValue;
        var bitsPerSample = (int)metaConstants.First(c => c.Id == metaRelations[3].ChildConstantId!.Value).SeedValue;

        // Get frame relations for track composition
        var frameRelations = await Context.Relations
            .Where(r => r.CompositionId == trackCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (frameRelations.Count == 0)
            throw new InvalidOperationException("Invalid audio track composition");

        var allSamples = new List<short>();

        foreach (var frameRelation in frameRelations)
        {
            // Get sample relations for frame
            var frameCompositionId = frameRelation.ChildCompositionId ?? throw new InvalidOperationException("Frame relation missing child composition");
            var sampleRelations = await Context.Relations
                .Where(r => r.CompositionId == frameCompositionId)
                .OrderBy(r => r.Position)
                .ToListAsync(ct);

            if (sampleRelations.Count == 0) continue;

            var sampleConstantIds = sampleRelations
                .Where(r => r.ChildConstantId.HasValue)
                .Select(r => r.ChildConstantId!.Value)
                .Distinct()
                .ToList();
            var sampleConstants = await Context.Constants
                .Where(c => sampleConstantIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);

            foreach (var sampleRelation in sampleRelations)
            {
                var sampleConstant = sampleConstants[sampleRelation.ChildConstantId!.Value];
                var mult = sampleRelation.Multiplicity;

                // Decode back to signed 16-bit
                uint encoded = (uint)sampleConstant.SeedValue;
                short sample = (short)(encoded - 32768);

                for (int m = 0; m < mult; m++)
                {
                    allSamples.Add(sample);
                }
            }
        }

        return new AudioData(sampleRate, channels, bitsPerSample, allSamples.ToArray());
    }

    private static (short[] Refs, int[] Multiplicities) CompressFrame(short[] samples)
    {
        var refs = new List<short>();
        var mults = new List<int>();

        int i = 0;
        while (i < samples.Length)
        {
            short current = samples[i];
            int count = 1;
            while (i + count < samples.Length && samples[i + count] == current)
                count++;

            refs.Add(current);
            mults.Add(count);
            i += count;
        }

        return (refs.ToArray(), mults.ToArray());
    }

    /// <summary>
    /// Bulk get or create constants for an array of seed values.
    /// Returns a dictionary mapping seed values to constant IDs.
    /// </summary>
    private async Task<Dictionary<uint, long>> BulkGetOrCreateConstantsAsync(
        uint[] seedValues,
        int seedType,
        CancellationToken ct)
    {
        var result = new Dictionary<uint, long>();

        foreach (var seedValue in seedValues)
        {
            ct.ThrowIfCancellationRequested();
            var constantId = await GetOrCreateConstantAsync(seedValue, seedType, ct);
            result[seedValue] = constantId;
        }

        return result;
    }
}

/// <summary>
/// Audio data container (PCM format)
/// </summary>
public record AudioData(int SampleRate, int Channels, int BitsPerSample, short[] Samples);
