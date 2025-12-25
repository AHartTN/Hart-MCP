using Hart.MCP.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Binary/bytes ingestion service.
/// 
/// REPRESENTATION:
/// - Each byte is a constant atom (0-255 as uint32)
/// - Only 256 possible byte constants exist (maximum deduplication)
/// - Chunks are compositions of byte atoms with RLE compression
/// - File is a composition of chunks
/// 
/// LOSSLESS: Original bytes exactly reconstructable.
/// </summary>
public class BinaryIngestionService : IngestionServiceBase, IIngestionService<byte[]>
{
    private const int CHUNK_SIZE = 4096;

    public BinaryIngestionService(HartDbContext context, ILogger<BinaryIngestionService>? logger = null)
        : base(context, logger) { }

    public async Task<long> IngestAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        Logger?.LogInformation("Ingesting binary data: {Length} bytes", data.Length);

        // Empty data is valid - return composition with length constant only
        if (data.Length == 0)
        {
            var emptyLengthConstant = await GetOrCreateConstantAsync(0, SEED_TYPE_INTEGER, ct);
            return await CreateCompositionFromConstantsAsync(new[] { emptyLengthConstant }, new[] { 1 }, null, ct);
        }

        // ============================================
        // PHASE 1: BULK create all 256 byte constants
        // ============================================
        var allBytes = Enumerable.Range(0, 256).Select(b => (uint)b).ToArray();
        var byteLookup = await BulkGetOrCreateConstantsAsync(allBytes, SEED_TYPE_INTEGER, ct);

        // ============================================
        // PHASE 2: Build chunk compositions (using lookup, minimal DB ops)
        // ============================================
        var chunkCompositionIds = new List<long>();
        int chunkCount = (data.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;

        for (int c = 0; c < chunkCount; c++)
        {
            int start = c * CHUNK_SIZE;
            int length = Math.Min(CHUNK_SIZE, data.Length - start);

            var chunkBytes = new byte[length];
            Array.Copy(data, start, chunkBytes, 0, length);

            // RLE compress
            var (refs, mults) = CompressChunk(chunkBytes);

            // Map byte values to constant IDs using dictionary (O(1) per byte, NO DB)
            var byteConstantIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                byteConstantIds[i] = byteLookup[refs[i]];
            }

            var chunkId = await CreateCompositionFromConstantsAsync(byteConstantIds, mults, null, ct);
            chunkCompositionIds.Add(chunkId);
        }

        // ============================================
        // PHASE 3: Create binary composition
        // ============================================
        var chunkChildren = chunkCompositionIds.Select(id => (id, isConstant: false)).ToArray();
        var binaryId = await CreateCompositionAsync(
            chunkChildren,
            Enumerable.Repeat(1, chunkCompositionIds.Count).ToArray(),
            null,
            ct
        );

        // Store length as metadata composition: [binary, length]
        var lengthConstant = await GetOrCreateConstantAsync(data.Length, SEED_TYPE_INTEGER, ct);
        var metaChildren = new (long id, bool isConstant)[] {
            (binaryId, false),
            (lengthConstant, true)
        };
        var metaId = await CreateCompositionAsync(
            metaChildren,
            new[] { 1, 1 },
            null,
            ct
        );

        Logger?.LogInformation("Ingested binary as composition {CompositionId} ({ChunkCount} chunks)", metaId, chunkCount);
        return metaId;
    }

    public async Task<byte[]> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        // Get meta composition relations via Relation table
        var metaRelations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (metaRelations.Count < 1)
            throw new InvalidOperationException($"Invalid binary meta composition {compositionId}");

        // Check for empty binary (only has length constant)
        if (metaRelations.Count == 1)
        {
            var relation = metaRelations[0];
            if (relation.ChildConstantId.HasValue)
            {
                var lengthCheck = await Context.Constants.FindAsync(new object[] { relation.ChildConstantId.Value }, ct);
                if (lengthCheck?.SeedValue == 0)
                    return Array.Empty<byte>();
            }
        }

        if (metaRelations.Count != 2)
            throw new InvalidOperationException($"Invalid binary meta composition {compositionId}");

        var binaryCompositionId = metaRelations[0].ChildCompositionId!.Value;
        var lengthConstant = await Context.Constants.FindAsync(new object[] { metaRelations[1].ChildConstantId!.Value }, ct);
        var expectedLength = (int)(lengthConstant?.SeedValue ?? 0);

        // Get chunk relations for binary composition
        var chunkRelations = await Context.Relations
            .Where(r => r.CompositionId == binaryCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (chunkRelations.Count == 0)
            throw new InvalidOperationException("Invalid binary composition");

        var result = new List<byte>();

        foreach (var chunkRelation in chunkRelations)
        {
            // Get byte relations for chunk
            var chunkCompositionId = chunkRelation.ChildCompositionId ?? throw new InvalidOperationException("Chunk relation missing child composition");
            var byteRelations = await Context.Relations
                .Where(r => r.CompositionId == chunkCompositionId)
                .OrderBy(r => r.Position)
                .ToListAsync(ct);

            if (byteRelations.Count == 0) continue;

            var byteConstantIds = byteRelations
                .Where(r => r.ChildConstantId.HasValue)
                .Select(r => r.ChildConstantId!.Value)
                .Distinct()
                .ToList();
            var byteConstants = await Context.Constants
                .Where(c => byteConstantIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);

            foreach (var byteRelation in byteRelations)
            {
                var byteConstant = byteConstants[byteRelation.ChildConstantId!.Value];
                var mult = byteRelation.Multiplicity;
                var byteValue = (byte)byteConstant.SeedValue;

                for (int m = 0; m < mult; m++)
                {
                    result.Add(byteValue);
                }
            }
        }

        return result.ToArray();
    }

    private static (byte[] Refs, int[] Multiplicities) CompressChunk(byte[] chunk)
    {
        var refs = new List<byte>();
        var mults = new List<int>();

        int i = 0;
        while (i < chunk.Length)
        {
            byte current = chunk[i];
            int count = 1;
            while (i + count < chunk.Length && chunk[i + count] == current)
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
