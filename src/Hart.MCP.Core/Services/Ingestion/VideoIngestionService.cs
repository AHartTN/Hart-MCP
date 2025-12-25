using Hart.MCP.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Video ingestion service.
/// 
/// REPRESENTATION:
/// - Each frame is an image composition (via ImageIngestionService pattern)
/// - Video is a composition of frame compositions with temporal ordering
/// - Audio track (if present) is a separate composition linked to video
/// 
/// TEMPORAL STRUCTURE:
/// - Frame order preserved via refs ordering
/// - Frame rate stored in metadata
/// - Enables frame-level queries and temporal navigation
/// 
/// LOSSLESS: Original frames exactly reconstructable (audio separate).
/// </summary>
public class VideoIngestionService : IngestionServiceBase, IIngestionService<VideoData>
{
    public VideoIngestionService(HartDbContext context, ILogger<VideoIngestionService>? logger = null)
        : base(context, logger) { }

    public async Task<long> IngestAsync(VideoData video, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (video.Width <= 0 || video.Height <= 0)
            throw new ArgumentException("Video dimensions must be positive");
        if (video.Frames == null || video.Frames.Count == 0)
            throw new ArgumentException("Video must have at least one frame");

        Logger?.LogInformation("Ingesting video: {Width}x{Height}, {FrameCount} frames, {FrameRate}fps",
            video.Width, video.Height, video.Frames.Count, video.FrameRate);

        // ============================================
        // PHASE 1: Extract ALL unique pixels from ALL frames (memory only)
        // ============================================
        var uniquePixels = new HashSet<uint>();
        foreach (var frame in video.Frames)
        {
            foreach (var pixel in frame)
            {
                uniquePixels.Add(pixel);
            }
        }
        Logger?.LogDebug("Found {UniqueCount} unique pixels across {FrameCount} frames", 
            uniquePixels.Count, video.Frames.Count);

        // ============================================
        // PHASE 2: BULK create ALL pixel constants
        // ============================================
        var pixelLookup = await BulkGetOrCreateConstantsAsync(
            uniquePixels.ToArray(),
            SEED_TYPE_INTEGER,
            ct);

        // ============================================
        // PHASE 3: Build frame compositions (using lookup, minimal DB ops)
        // ============================================
        var frameCompositionIds = new long[video.Frames.Count];

        for (int f = 0; f < video.Frames.Count; f++)
        {
            frameCompositionIds[f] = await IngestFrameAsync(video.Frames[f], video.Width, video.Height, pixelLookup, ct);
            
            if (f > 0 && f % 100 == 0)
            {
                Logger?.LogDebug("Ingested frame {Current}/{Total}", f, video.Frames.Count);
            }
        }

        // ============================================
        // PHASE 4: Create video track composition
        // ============================================
        var frameChildren = frameCompositionIds.Select(id => (id, isConstant: false)).ToArray();
        var trackId = await CreateCompositionAsync(
            frameChildren,
            Enumerable.Repeat(1, frameCompositionIds.Length).ToArray(),
            null,
            ct
        );

        // Store metadata as composition: [track, width, height, fps, frameCount, ?audio]
        var widthConstant = await GetOrCreateConstantAsync((uint)video.Width, SEED_TYPE_INTEGER, ct);
        var heightConstant = await GetOrCreateConstantAsync((uint)video.Height, SEED_TYPE_INTEGER, ct);
        var fpsConstant = await GetOrCreateConstantAsync((uint)(video.FrameRate * 1000), SEED_TYPE_INTEGER, ct);
        var frameCountConstant = await GetOrCreateConstantAsync((uint)video.Frames.Count, SEED_TYPE_INTEGER, ct);

        var metaChildren = new List<(long id, bool isConstant)> {
            (trackId, false),
            (widthConstant, true),
            (heightConstant, true),
            (fpsConstant, true),
            (frameCountConstant, true)
        };
        var metaMults = new List<int> { 1, 1, 1, 1, 1 };

        // Link audio if present (audio is a composition)
        if (video.AudioAtomId.HasValue)
        {
            metaChildren.Add((video.AudioAtomId.Value, false));
            metaMults.Add(1);
        }

        var videoId = await CreateCompositionAsync(
            metaChildren.ToArray(),
            metaMults.ToArray(),
            null,
            ct
        );

        Logger?.LogInformation("Ingested video as composition {CompositionId}", videoId);
        return videoId;
    }

    private async Task<long> IngestFrameAsync(
        uint[] pixels, 
        int width, 
        int height, 
        Dictionary<uint, long> pixelLookup,
        CancellationToken ct)
    {
        var rowCompositionIds = new long[height];

        for (int y = 0; y < height; y++)
        {
            var rowPixels = new uint[width];
            Array.Copy(pixels, y * width, rowPixels, 0, width);

            var (refs, mults) = CompressRow(rowPixels);
            
            // Map pixel values to constant IDs using dictionary (O(1) per pixel, NO DB)
            var pixelConstantIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                pixelConstantIds[i] = pixelLookup[refs[i]];
            }

            rowCompositionIds[y] = await CreateCompositionFromConstantsAsync(pixelConstantIds, mults, null, ct);
        }

        var rowChildren = rowCompositionIds.Select(id => (id, isConstant: false)).ToArray();
        return await CreateCompositionAsync(
            rowChildren,
            Enumerable.Repeat(1, rowCompositionIds.Length).ToArray(),
            null,
            ct
        );
    }

    public async Task<VideoData> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        // Get video composition relations via Relation table
        var videoRelations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (videoRelations.Count < 5)
            throw new InvalidOperationException($"Invalid video composition {compositionId}");

        var trackCompositionId = videoRelations[0].ChildCompositionId!.Value;
        var metaConstantIds = videoRelations.Skip(1).Take(4).Select(r => r.ChildConstantId!.Value).ToList();
        var metaConstants = await Context.Constants
            .Where(c => metaConstantIds.Contains(c.Id))
            .ToListAsync(ct);

        var width = (int)metaConstants.First(c => c.Id == videoRelations[1].ChildConstantId!.Value).SeedValue;
        var height = (int)metaConstants.First(c => c.Id == videoRelations[2].ChildConstantId!.Value).SeedValue;
        var frameRate = metaConstants.First(c => c.Id == videoRelations[3].ChildConstantId!.Value).SeedValue / 1000.0;
        var frameCount = (int)metaConstants.First(c => c.Id == videoRelations[4].ChildConstantId!.Value).SeedValue;

        // Audio is a composition (not constant)
        long? audioCompositionId = videoRelations.Count > 5 ? videoRelations[5].ChildCompositionId : null;

        // Get frame relations for track composition
        var frameRelations = await Context.Relations
            .Where(r => r.CompositionId == trackCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (frameRelations.Count == 0)
            throw new InvalidOperationException("Invalid video track composition");

        var frames = new List<uint[]>();

        foreach (var frameRelation in frameRelations)
        {
            var frameCompositionId = frameRelation.ChildCompositionId ?? throw new InvalidOperationException("Frame relation missing child composition");
            var frame = await ReconstructFrameAsync(frameCompositionId, width, height, ct);
            frames.Add(frame);
        }

        return new VideoData(width, height, frameRate, frames, audioCompositionId);
    }

    private async Task<uint[]> ReconstructFrameAsync(long frameCompositionId, int width, int height, CancellationToken ct)
    {
        // Get row relations for frame composition
        var rowRelations = await Context.Relations
            .Where(r => r.CompositionId == frameCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (rowRelations.Count == 0)
            return new uint[width * height];

        var pixels = new uint[width * height];

        for (int y = 0; y < rowRelations.Count && y < height; y++)
        {
            // Get pixel relations for row
            var rowCompositionId = rowRelations[y].ChildCompositionId ?? throw new InvalidOperationException("Row relation missing child composition");
            var pixelRelations = await Context.Relations
                .Where(r => r.CompositionId == rowCompositionId)
                .OrderBy(r => r.Position)
                .ToListAsync(ct);

            if (pixelRelations.Count == 0) continue;

            var pixelConstantIds = pixelRelations
                .Where(r => r.ChildConstantId.HasValue)
                .Select(r => r.ChildConstantId!.Value)
                .Distinct()
                .ToList();
            var pixelConstants = await Context.Constants
                .Where(c => pixelConstantIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);

            int x = 0;
            foreach (var pixelRelation in pixelRelations)
            {
                if (x >= width) break;

                var pixelConstant = pixelConstants[pixelRelation.ChildConstantId!.Value];
                var mult = pixelRelation.Multiplicity;
                var pixelValue = (uint)pixelConstant.SeedValue;

                for (int m = 0; m < mult && x < width; m++)
                {
                    pixels[y * width + x] = pixelValue;
                    x++;
                }
            }
        }

        return pixels;
    }

    private static (uint[] Refs, int[] Multiplicities) CompressRow(uint[] row)
    {
        var refs = new List<uint>();
        var mults = new List<int>();

        int i = 0;
        while (i < row.Length)
        {
            uint current = row[i];
            int count = 1;
            while (i + count < row.Length && row[i + count] == current)
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
/// Video data container
/// </summary>
public record VideoData(
    int Width,
    int Height,
    double FrameRate,
    IReadOnlyList<uint[]> Frames,
    long? AudioAtomId = null
);
