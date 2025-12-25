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

        // Empty data is valid - return empty binary constant marker
        if (data.Length == 0)
        {
            return await GetOrCreateConstantAsync(0xFFFFFFFB, SEED_TYPE_INTEGER, "binary_empty", ct);
        }

        // ============================================
        // PHASE 1: BULK create all 256 byte constants (ONE DB round-trip)
        // ============================================
        var allBytes = Enumerable.Range(0, 256).Select(b => (uint)b).ToArray();
        var byteLookup = await BulkGetOrCreateConstantsAsync(allBytes, SEED_TYPE_INTEGER, "byte", ct);

        // ============================================
        // PHASE 2: Build chunk compositions (using lookup, minimal DB ops)
        // ============================================
        var chunkAtomIds = new List<long>();
        int chunkCount = (data.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;

        for (int c = 0; c < chunkCount; c++)
        {
            int start = c * CHUNK_SIZE;
            int length = Math.Min(CHUNK_SIZE, data.Length - start);

            var chunkBytes = new byte[length];
            Array.Copy(data, start, chunkBytes, 0, length);

            // RLE compress
            var (refs, mults) = CompressChunk(chunkBytes);

            // Map byte values to atom IDs using dictionary (O(1) per byte, NO DB)
            var byteAtomIds = new long[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                byteAtomIds[i] = byteLookup[refs[i]];
            }

            var chunkId = await CreateCompositionAsync(byteAtomIds, mults, "binary_chunk", ct);
            chunkAtomIds.Add(chunkId);
        }

        // ============================================
        // PHASE 3: Create binary composition
        // ============================================
        var binaryId = await CreateCompositionAsync(
            chunkAtomIds.ToArray(),
            Enumerable.Repeat(1, chunkAtomIds.Count).ToArray(),
            "binary",
            ct
        );

        // Store length as metadata
        var lengthAtom = await GetOrCreateIntegerConstantAsync(data.Length, "binary_meta", ct);
        var metaId = await CreateCompositionAsync(
            new[] { binaryId, lengthAtom },
            new[] { 1, 1 },
            "binary_meta",
            ct
        );

        Logger?.LogInformation("Ingested binary as atom {AtomId} ({ChunkCount} chunks)", metaId, chunkCount);
        return metaId;
    }

    public async Task<byte[]> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        // Handle empty binary constant
        var emptyCheck = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == compositionId && a.AtomType == "binary_empty" && a.IsConstant, ct);
        if (emptyCheck != null)
            return Array.Empty<byte>();

        var meta = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == compositionId && a.AtomType == "binary_meta", ct);

        if (meta?.Refs == null || meta.Refs.Length != 2)
            throw new InvalidOperationException($"Invalid binary meta atom {compositionId}");

        var binaryAtomId = meta.Refs[0];
        var lengthAtom = await Context.Atoms.FindAsync(new object[] { meta.Refs[1] }, ct);
        var expectedLength = (int)(lengthAtom?.SeedValue ?? 0);

        var binaryAtom = await Context.Atoms
            .FirstOrDefaultAsync(a => a.Id == binaryAtomId && a.AtomType == "binary", ct);

        if (binaryAtom?.Refs == null)
            throw new InvalidOperationException("Invalid binary atom");

        var result = new List<byte>();

        foreach (var chunkId in binaryAtom.Refs)
        {
            var chunkAtom = await Context.Atoms
                .FirstOrDefaultAsync(a => a.Id == chunkId && a.AtomType == "binary_chunk", ct);

            if (chunkAtom?.Refs == null) continue;

            var byteConstants = await Context.Atoms
                .Where(a => chunkAtom.Refs.Contains(a.Id) && a.IsConstant)
                .ToDictionaryAsync(a => a.Id, ct);

            for (int i = 0; i < chunkAtom.Refs.Length; i++)
            {
                var byteAtom = byteConstants[chunkAtom.Refs[i]];
                var mult = chunkAtom.Multiplicities?[i] ?? 1;
                var byteValue = (byte)(byteAtom.SeedValue ?? 0);

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
}
