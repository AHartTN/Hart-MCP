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
        // PHASE 2: BULK create ALL component constants (ONE DB round-trip)
        // ============================================
        var bitsLookup = await BulkGetOrCreateConstantsAsync(
            uniqueBits.ToArray(), 
            SEED_TYPE_FLOAT_BITS, 
            "embedding_component", 
            ct);

        // ============================================
        // PHASE 3: Build embedding composition using lookup
        // ============================================
        var componentAtomIds = new long[embedding.Length];
        for (int i = 0; i < embedding.Length; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(embedding[i]);
            componentAtomIds[i] = bitsLookup[bits]; // O(1), NO DB
        }

        // Create embedding composition
        var embeddingId = await CreateCompositionAsync(
            componentAtomIds,
            Enumerable.Repeat(1, componentAtomIds.Length).ToArray(),
            "embedding",
            ct
        );

        Logger?.LogInformation("Ingested embedding as atom {AtomId}", embeddingId);
        return embeddingId;
    }

    public async Task<float[]> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        var embedding = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == compositionId && a.AtomType == "embedding", ct);

        if (embedding?.Refs == null)
            throw new InvalidOperationException($"Invalid embedding atom {compositionId}");

        var components = await Context.Atoms
            .Where(a => embedding.Refs.Contains(a.Id) && a.IsConstant)
            .ToDictionaryAsync(a => a.Id, ct);

        var result = new float[embedding.Refs.Length];

        for (int i = 0; i < embedding.Refs.Length; i++)
        {
            var component = components[embedding.Refs[i]];
            uint bits = (uint)(component.SeedValue ?? 0);
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
        var componentAtomIds = new long[embedding.Length];

        for (int i = 0; i < embedding.Length; i++)
        {
            // Convert double to IEEE754 bits (64-bit)
            ulong bits = BitConverter.DoubleToUInt64Bits(embedding[i]);
            
            // Store as 64-bit integer constant
            componentAtomIds[i] = await GetOrCreateIntegerConstantAsync((long)bits, "embedding_component_f64", ct);
        }

        var embeddingId = await CreateCompositionAsync(
            componentAtomIds,
            Enumerable.Repeat(1, componentAtomIds.Length).ToArray(),
            "embedding_f64",
            ct
        );

        Logger?.LogInformation("Ingested double embedding as atom {AtomId}", embeddingId);
        return embeddingId;
    }

    /// <summary>
    /// Reconstruct double-precision embedding
    /// </summary>
    public async Task<double[]> ReconstructDoubleAsync(long compositionId, CancellationToken ct = default)
    {
        var embedding = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == compositionId && a.AtomType == "embedding_f64", ct);

        if (embedding?.Refs == null)
            throw new InvalidOperationException($"Invalid embedding atom {compositionId}");

        var components = await Context.Atoms
            .Where(a => embedding.Refs.Contains(a.Id) && a.IsConstant)
            .ToDictionaryAsync(a => a.Id, ct);

        var result = new double[embedding.Refs.Length];

        for (int i = 0; i < embedding.Refs.Length; i++)
        {
            var component = components[embedding.Refs[i]];
            ulong bits = (ulong)(component.SeedValue ?? 0);
            result[i] = BitConverter.UInt64BitsToDouble(bits);
        }

        return result;
    }
}
