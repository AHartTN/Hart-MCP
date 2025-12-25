using Hart.MCP.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Embedding/vector ingestion service.
/// 
/// REPRESENTATION:
/// - Each float component is a constant atom (IEEE754 bits as uint64 → split to uint32)
/// - Vector is a composition of component atoms (ordered, no RLE - floats rarely repeat)
/// - Preserves full IEEE754 precision (no quantization loss)
/// 
/// SPATIAL PROPERTY:
/// - Similar embeddings → similar hypersphere positions → Hilbert locality
/// - Enables semantic similarity search via spatial queries
/// 
/// LOSSLESS: Original float[] exactly reconstructable.
/// </summary>
public class EmbeddingIngestionService : IngestionServiceBase, IIngestionService<float[]>
{
    public EmbeddingIngestionService(HartDbContext context, ILogger<EmbeddingIngestionService>? logger = null)
        : base(context, logger) { }

    public async Task<long> IngestAsync(float[] embedding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
            throw new ArgumentException("Embedding cannot be empty", nameof(embedding));

        Logger?.LogInformation("Ingesting embedding: {Dimensions} dimensions", embedding.Length);

        // ============================================
        // PHASE 1: Extract ALL unique float bits (memory only)
        // ============================================
        var uniqueBits = new HashSet<uint>();
        foreach (var f in embedding)
        {
            uniqueBits.Add(BitConverter.SingleToUInt32Bits(f));
        }
        Logger?.LogDebug("Found {UniqueCount} unique float values", uniqueBits.Count);

        // ============================================
        // PHASE 2: BULK create ALL component constants
        // ============================================
        var bitsLookup = await BulkGetOrCreateConstantsAsync(
            uniqueBits.ToArray(),
            SEED_TYPE_FLOAT_BITS,
            ct);

        // ============================================
        // PHASE 3: Build embedding composition using lookup
        // ============================================
        var componentConstantIds = new long[embedding.Length];
        for (int i = 0; i < embedding.Length; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(embedding[i]);
            componentConstantIds[i] = bitsLookup[bits]; // O(1), NO DB
        }

        // Create embedding composition from constants
        var embeddingId = await CreateCompositionFromConstantsAsync(
            componentConstantIds,
            Enumerable.Repeat(1, componentConstantIds.Length).ToArray(),
            null, // typeId
            ct
        );

        Logger?.LogInformation("Ingested embedding as composition {CompositionId}", embeddingId);
        return embeddingId;
    }

    public async Task<float[]> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        var embedding = await Context.Compositions
            .FirstOrDefaultAsync(c => c.Id == compositionId, ct);

        if (embedding == null)
            throw new InvalidOperationException($"Invalid embedding composition {compositionId}");

        // Get component relations via Relation table
        var componentRelations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (componentRelations.Count == 0)
            throw new InvalidOperationException($"Invalid embedding composition {compositionId} - no components");

        var constantIds = componentRelations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();
        var constants = await Context.Constants
            .Where(c => constantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var result = new float[componentRelations.Count];

        for (int i = 0; i < componentRelations.Count; i++)
        {
            var relation = componentRelations[i];
            if (!relation.ChildConstantId.HasValue)
                throw new InvalidOperationException($"Expected constant child at position {i}");

            var constant = constants[relation.ChildConstantId.Value];
            uint bits = (uint)constant.SeedValue;
            result[i] = BitConverter.UInt32BitsToSingle(bits);
        }

        return result;
    }

    /// <summary>
    /// Ingest double-precision embedding (64-bit floats)
    /// </summary>
    public async Task<long> IngestDoubleAsync(double[] embedding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
            throw new ArgumentException("Embedding cannot be empty", nameof(embedding));

        Logger?.LogInformation("Ingesting double embedding: {Dimensions} dimensions", embedding.Length);

        // For double precision, values are typically unique so we still process individually
        // (64-bit precision means deduplication is rare)
        var componentConstantIds = new long[embedding.Length];

        for (int i = 0; i < embedding.Length; i++)
        {
            // Convert double to IEEE754 bits (64-bit)
            ulong bits = BitConverter.DoubleToUInt64Bits(embedding[i]);

            // Store as 64-bit integer constant
            componentConstantIds[i] = await GetOrCreateConstantAsync((long)bits, SEED_TYPE_INTEGER, ct);
        }

        var embeddingId = await CreateCompositionFromConstantsAsync(
            componentConstantIds,
            Enumerable.Repeat(1, componentConstantIds.Length).ToArray(),
            null, // typeId
            ct
        );

        Logger?.LogInformation("Ingested double embedding as composition {CompositionId}", embeddingId);
        return embeddingId;
    }

    /// <summary>
    /// Reconstruct double-precision embedding
    /// </summary>
    public async Task<double[]> ReconstructDoubleAsync(long compositionId, CancellationToken ct = default)
    {
        var embedding = await Context.Compositions
            .FirstOrDefaultAsync(c => c.Id == compositionId, ct);

        if (embedding == null)
            throw new InvalidOperationException($"Invalid embedding composition {compositionId}");

        // Get component relations via Relation table
        var componentRelations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (componentRelations.Count == 0)
            throw new InvalidOperationException($"Invalid embedding composition {compositionId} - no components");

        var constantIds = componentRelations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();
        var constants = await Context.Constants
            .Where(c => constantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var result = new double[componentRelations.Count];

        for (int i = 0; i < componentRelations.Count; i++)
        {
            var relation = componentRelations[i];
            if (!relation.ChildConstantId.HasValue)
                throw new InvalidOperationException($"Expected constant child at position {i}");

            var constant = constants[relation.ChildConstantId.Value];
            ulong bits = (ulong)constant.SeedValue;
            result[i] = BitConverter.UInt64BitsToDouble(bits);
        }

        return result;
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
