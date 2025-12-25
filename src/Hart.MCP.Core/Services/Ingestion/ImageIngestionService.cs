using Hart.MCP.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Image ingestion service.
/// 
/// REPRESENTATION:
/// - Each pixel is a constant atom (ARGB packed as uint32)
/// - Rows are compositions of pixel atoms
/// - Image is a composition of row compositions
/// - Preserves spatial structure: nearby pixels → nearby Hilbert indices
/// 
/// LOSSLESS: Original image exactly reconstructable from atoms.
/// </summary>
public class ImageIngestionService : IngestionServiceBase, IIngestionService<ImageData>
{
    public ImageIngestionService(HartDbContext context, ILogger<ImageIngestionService>? logger = null)
        : base(context, logger) { }

    public async Task<long> IngestAsync(ImageData image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException("Image dimensions must be positive");
        if (image.Pixels == null || image.Pixels.Length != image.Width * image.Height)
            throw new ArgumentException("Pixels array size must match dimensions");

        Logger?.LogInformation("Ingesting image {Width}x{Height}", image.Width, image.Height);

        // ============================================
        // PHASE 1: Extract ALL unique pixels (memory only)
        // ============================================
        var uniquePixels = new HashSet<uint>(image.Pixels);
        Logger?.LogDebug("Found {UniqueCount} unique pixels", uniquePixels.Count);

        // ============================================
        // PHASE 2: BULK create ALL pixel constants (ONE DB round-trip)
        // ============================================
        var pixelLookup = await BulkGetOrCreateConstantsAsync(
            uniquePixels.ToArray(), 
            SEED_TYPE_INTEGER, 
            "pixel", 
            ct);

        // ============================================
        // PHASE 3: Build row compositions (using lookup, minimal DB ops)
        // ============================================
        var rowAtomIds = new long[image.Height];

        for (int y = 0; y < image.Height; y++)
        {
            var rowPixels = new uint[image.Width];
            Array.Copy(image.Pixels, y * image.Width, rowPixels, 0, image.Width);

            // RLE compress the row
            var (refs, mults) = CompressRow(rowPixels);

            // Map pixel values to atom IDs using dictionary (O(1) per pixel, NO DB)
            var pixelAtomIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                pixelAtomIds[i] = pixelLookup[refs[i]];
            }

            // Create row composition
            rowAtomIds[y] = await CreateCompositionAsync(pixelAtomIds, mults, "image_row", ct);
        }

        // ============================================
        // PHASE 4: Create image composition from rows
        // ============================================
        var imageId = await CreateCompositionAsync(
            rowAtomIds,
            Enumerable.Repeat(1, rowAtomIds.Length).ToArray(),
            "image",
            ct
        );

        // Store dimensions as metadata composition
        var widthAtom = await GetOrCreateConstantAsync((uint)image.Width, SEED_TYPE_INTEGER, "dimension", ct);
        var heightAtom = await GetOrCreateConstantAsync((uint)image.Height, SEED_TYPE_INTEGER, "dimension", ct);
        var metaId = await CreateCompositionAsync(
            new[] { imageId, widthAtom, heightAtom },
            new[] { 1, 1, 1 },
            "image_meta",
            ct
        );

        Logger?.LogInformation("Ingested image as atom {AtomId}", metaId);
        return metaId;
    }

    public async Task<ImageData> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        var meta = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == compositionId && a.AtomType == "image_meta", ct);

        if (meta?.Refs == null || meta.Refs.Length != 3)
            throw new InvalidOperationException($"Invalid image meta atom {compositionId}");

        var imageAtomId = meta.Refs[0];
        var widthAtomId = meta.Refs[1];
        var heightAtomId = meta.Refs[2];

        var dims = await Context.Atoms
            .Where(a => a.Id == widthAtomId || a.Id == heightAtomId)
            .ToListAsync(ct);

        var width = (int)(dims.First(d => d.Id == widthAtomId).SeedValue ?? 0);
        var height = (int)(dims.First(d => d.Id == heightAtomId).SeedValue ?? 0);

        var imageAtom = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == imageAtomId && a.AtomType == "image", ct);

        if (imageAtom?.Refs == null)
            throw new InvalidOperationException("Invalid image atom");

        var pixels = new uint[width * height];
        var rowAtoms = await Context.Atoms
            .Where(a => imageAtom.Refs.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        for (int y = 0; y < imageAtom.Refs.Length && y < height; y++)
        {
            var rowAtom = rowAtoms[imageAtom.Refs[y]];
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

        return new ImageData(width, height, pixels);
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
/// Image data container (ARGB format)
/// </summary>
public record ImageData(int Width, int Height, uint[] Pixels);
