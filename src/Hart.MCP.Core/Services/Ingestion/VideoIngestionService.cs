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
        // PHASE 2: BULK create ALL pixel constants (ONE DB round-trip)
        // ============================================
        var pixelLookup = await BulkGetOrCreateConstantsAsync(
            uniquePixels.ToArray(), 
            SEED_TYPE_INTEGER, 
            "pixel", 
            ct);

        // ============================================
        // PHASE 3: Build frame compositions (using lookup, minimal DB ops)
        // ============================================
        var frameAtomIds = new long[video.Frames.Count];

        for (int f = 0; f < video.Frames.Count; f++)
        {
            frameAtomIds[f] = await IngestFrameAsync(video.Frames[f], video.Width, video.Height, pixelLookup, ct);
            
            if (f > 0 && f % 100 == 0)
            {
                Logger?.LogDebug("Ingested frame {Current}/{Total}", f, video.Frames.Count);
            }
        }

        // ============================================
        // PHASE 4: Create video track composition
        // ============================================
        var trackId = await CreateCompositionAsync(
            frameAtomIds,
            Enumerable.Repeat(1, frameAtomIds.Length).ToArray(),
            "video_track",
            ct
        );

        // Store metadata
        var widthAtom = await GetOrCreateConstantAsync((uint)video.Width, SEED_TYPE_INTEGER, "video_meta", ct);
        var heightAtom = await GetOrCreateConstantAsync((uint)video.Height, SEED_TYPE_INTEGER, "video_meta", ct);
        var fpsAtom = await GetOrCreateConstantAsync((uint)(video.FrameRate * 1000), SEED_TYPE_INTEGER, "video_meta", ct);
        var frameCountAtom = await GetOrCreateConstantAsync((uint)video.Frames.Count, SEED_TYPE_INTEGER, "video_meta", ct);

        var metaRefs = new List<long> { trackId, widthAtom, heightAtom, fpsAtom, frameCountAtom };
        var metaMults = new List<int> { 1, 1, 1, 1, 1 };

        // Link audio if present
        if (video.AudioAtomId.HasValue)
        {
            metaRefs.Add(video.AudioAtomId.Value);
            metaMults.Add(1);
        }

        var videoId = await CreateCompositionAsync(
            metaRefs.ToArray(),
            metaMults.ToArray(),
            "video",
            ct
        );

        Logger?.LogInformation("Ingested video as atom {AtomId}", videoId);
        return videoId;
    }

    private async Task<long> IngestFrameAsync(
        uint[] pixels, 
        int width, 
        int height, 
        Dictionary<uint, long> pixelLookup,
        CancellationToken ct)
    {
        var rowAtomIds = new long[height];

        for (int y = 0; y < height; y++)
        {
            var rowPixels = new uint[width];
            Array.Copy(pixels, y * width, rowPixels, 0, width);

            var (refs, mults) = CompressRow(rowPixels);
            
            // Map pixel values to atom IDs using dictionary (O(1) per pixel, NO DB)
            var pixelAtomIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                pixelAtomIds[i] = pixelLookup[refs[i]];
            }

            rowAtomIds[y] = await CreateCompositionAsync(pixelAtomIds, mults, "video_row", ct);
        }

        return await CreateCompositionAsync(
            rowAtomIds,
            Enumerable.Repeat(1, rowAtomIds.Length).ToArray(),
            "video_frame",
            ct
        );
    }

    public async Task<VideoData> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        var video = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == compositionId && a.AtomType == "video", ct);

        if (video?.Refs == null || video.Refs.Length < 5)
            throw new InvalidOperationException($"Invalid video atom {compositionId}");

        var trackAtomId = video.Refs[0];
        var metaAtoms = await Context.Atoms
            .Where(a => video.Refs.Skip(1).Take(4).Contains(a.Id))
            .ToListAsync(ct);

        var width = (int)(metaAtoms.First(a => a.Id == video.Refs[1]).SeedValue ?? 0);
        var height = (int)(metaAtoms.First(a => a.Id == video.Refs[2]).SeedValue ?? 0);
        var frameRate = (metaAtoms.First(a => a.Id == video.Refs[3]).SeedValue ?? 30000) / 1000.0;
        var frameCount = (int)(metaAtoms.First(a => a.Id == video.Refs[4]).SeedValue ?? 0);

        long? audioAtomId = video.Refs.Length > 5 ? video.Refs[5] : null;

        var trackAtom = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == trackAtomId && a.AtomType == "video_track", ct);

        if (trackAtom?.Refs == null)
            throw new InvalidOperationException("Invalid video track atom");

        var frames = new List<uint[]>();

        foreach (var frameId in trackAtom.Refs)
        {
            var frame = await ReconstructFrameAsync(frameId, width, height, ct);
            frames.Add(frame);
        }

        return new VideoData(width, height, frameRate, frames, audioAtomId);
    }

    private async Task<uint[]> ReconstructFrameAsync(long frameId, int width, int height, CancellationToken ct)
    {
        var frameAtom = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == frameId && a.AtomType == "video_frame", ct);

        if (frameAtom?.Refs == null)
            return new uint[width * height];

        var pixels = new uint[width * height];
        var rowAtoms = await Context.Atoms
            .Where(a => frameAtom.Refs.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        for (int y = 0; y < frameAtom.Refs.Length && y < height; y++)
        {
            var rowAtom = rowAtoms[frameAtom.Refs[y]];
            if (rowAtom.Refs == null) continue;

            var pixelConstants = await Context.Atoms
                .Where(a => rowAtom.Refs.Contains(a.Id) && a.IsConstant)
                .ToDictionaryAsync(a => a.Id, ct);

            int x = 0;
            for (int i = 0; i < rowAtom.Refs.Length && x < width; i++)
            {
                var pixelAtom = pixelConstants[rowAtom.Refs[i]];
                var mult = rowAtom.Multiplicities?[i] ?? 1;
                var pixelValue = (uint)(pixelAtom.SeedValue ?? 0);

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
