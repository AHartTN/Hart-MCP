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
        // PHASE 2: BULK create ALL pixel constants
        // ============================================
        var pixelLookup = await BulkGetOrCreateConstantsAsync(
            uniquePixels.ToArray(),
            SEED_TYPE_INTEGER,
            ct);

        // ============================================
        // PHASE 3: Build row compositions (using lookup, minimal DB ops)
        // ============================================
        var rowCompositionIds = new long[image.Height];

        for (int y = 0; y < image.Height; y++)
        {
            var rowPixels = new uint[image.Width];
            Array.Copy(image.Pixels, y * image.Width, rowPixels, 0, image.Width);

            // RLE compress the row
            var (refs, mults) = CompressRow(rowPixels);

            // Map pixel values to constant IDs using dictionary (O(1) per pixel, NO DB)
            var pixelConstantIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                pixelConstantIds[i] = pixelLookup[refs[i]];
            }

            // Create row composition from constants
            rowCompositionIds[y] = await CreateCompositionFromConstantsAsync(pixelConstantIds, mults, null, ct);
        }

        // ============================================
        // PHASE 4: Create image composition from rows
        // ============================================
        var rowChildren = rowCompositionIds.Select(id => (id, isConstant: false)).ToArray();
        var imageId = await CreateCompositionAsync(
            rowChildren,
            Enumerable.Repeat(1, rowCompositionIds.Length).ToArray(),
            null,
            ct
        );

        // Store dimensions as metadata composition: [image, width, height]
        var widthConstant = await GetOrCreateConstantAsync((uint)image.Width, SEED_TYPE_INTEGER, ct);
        var heightConstant = await GetOrCreateConstantAsync((uint)image.Height, SEED_TYPE_INTEGER, ct);
        var metaChildren = new (long id, bool isConstant)[] {
            (imageId, false),
            (widthConstant, true),
            (heightConstant, true)
        };
        var metaId = await CreateCompositionAsync(
            metaChildren,
            new[] { 1, 1, 1 },
            null,
            ct
        );

        Logger?.LogInformation("Ingested image as composition {CompositionId}", metaId);
        return metaId;
    }

    public async Task<ImageData> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        // Get meta composition relations via Relation table
        var metaRelations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (metaRelations.Count != 3)
            throw new InvalidOperationException($"Invalid image meta composition {compositionId}");

        var imageCompositionId = metaRelations[0].ChildCompositionId!.Value;
        var widthConstantId = metaRelations[1].ChildConstantId!.Value;
        var heightConstantId = metaRelations[2].ChildConstantId!.Value;

        var dims = await Context.Constants
            .Where(c => c.Id == widthConstantId || c.Id == heightConstantId)
            .ToListAsync(ct);

        var width = (int)dims.First(d => d.Id == widthConstantId).SeedValue;
        var height = (int)dims.First(d => d.Id == heightConstantId).SeedValue;

        // Get row relations for image composition
        var rowRelations = await Context.Relations
            .Where(r => r.CompositionId == imageCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (rowRelations.Count == 0)
            throw new InvalidOperationException("Invalid image composition");

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
/// Image data container (ARGB format)
/// </summary>
public record ImageData(int Width, int Height, uint[] Pixels);
