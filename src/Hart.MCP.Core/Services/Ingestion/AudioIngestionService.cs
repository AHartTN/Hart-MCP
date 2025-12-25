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
        // PHASE 2: BULK create ALL sample constants (ONE DB round-trip)
        // ============================================
        var sampleLookup = await BulkGetOrCreateConstantsAsync(
            uniqueEncoded,
            SEED_TYPE_INTEGER,
            null,
            ct);

        // ============================================
        // PHASE 3: Build frame compositions (using lookup, minimal DB ops)
        // ============================================
        var frameAtomIds = new List<long>();
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

            // Map sample values to atom IDs using dictionary (O(1) per sample, NO DB)
            var sampleAtomIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                uint encoded = (uint)(refs[i] + 32768);
                sampleAtomIds[i] = sampleLookup[encoded];
            }

            // Create frame composition
            var frameId = await CreateCompositionAsync(sampleAtomIds, mults, null, ct);
            frameAtomIds.Add(frameId);
        }

        // ============================================
        // PHASE 4: Create audio track composition
        // ============================================
        var trackId = await CreateCompositionAsync(
            frameAtomIds.ToArray(),
            Enumerable.Repeat(1, frameAtomIds.Count).ToArray(),
            null,
            ct
        );

        // Store metadata as composition: [track, sampleRate, channels, bitsPerSample]
        var sampleRateAtom = await GetOrCreateConstantAsync((uint)audio.SampleRate, SEED_TYPE_INTEGER, null, ct);
        var channelsAtom = await GetOrCreateConstantAsync((uint)audio.Channels, SEED_TYPE_INTEGER, null, ct);
        var bitsAtom = await GetOrCreateConstantAsync((uint)audio.BitsPerSample, SEED_TYPE_INTEGER, null, ct);

        var metaId = await CreateCompositionAsync(
            new[] { trackId, sampleRateAtom, channelsAtom, bitsAtom },
            new[] { 1, 1, 1, 1 },
            null,
            ct
        );

        Logger?.LogInformation("Ingested audio as atom {AtomId} ({FrameCount} frames)", metaId, frameCount);
        return metaId;
    }

    public async Task<AudioData> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        var meta = await Context.Atoms.FindAsync(new object[] { compositionId }, ct);

        if (meta?.Refs == null || meta.Refs.Length != 4)
            throw new InvalidOperationException($"Invalid audio meta atom {compositionId}");

        var trackAtomId = meta.Refs[0];
        var metaAtoms = await Context.Atoms
            .Where(a => meta.Refs.Skip(1).Contains(a.Id))
            .ToListAsync(ct);

        var sampleRate = (int)(metaAtoms.First(a => a.Id == meta.Refs[1]).SeedValue ?? 44100);
        var channels = (int)(metaAtoms.First(a => a.Id == meta.Refs[2]).SeedValue ?? 1);
        var bitsPerSample = (int)(metaAtoms.First(a => a.Id == meta.Refs[3]).SeedValue ?? 16);

        var trackAtom = await Context.Atoms.FindAsync(new object[] { trackAtomId }, ct);

        if (trackAtom?.Refs == null)
            throw new InvalidOperationException("Invalid audio track atom");

        var allSamples = new List<short>();

        foreach (var frameId in trackAtom.Refs)
        {
            var frameAtom = await Context.Atoms.FindAsync(new object[] { frameId }, ct);

            if (frameAtom?.Refs == null) continue;

            var sampleConstants = await Context.Atoms
                .Where(a => frameAtom.Refs.Contains(a.Id) && a.IsConstant)
                .ToDictionaryAsync(a => a.Id, ct);

            for (int i = 0; i < frameAtom.Refs.Length; i++)
            {
                var sampleAtom = sampleConstants[frameAtom.Refs[i]];
                var mult = frameAtom.Multiplicities?[i] ?? 1;

                // Decode back to signed 16-bit
                uint encoded = (uint)(sampleAtom.SeedValue ?? 32768);
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
}

/// <summary>
/// Audio data container (PCM format)
/// </summary>
public record AudioData(int SampleRate, int Channels, int BitsPerSample, short[] Samples);
