using Hart.MCP.Core.Data;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Weight edge service - creates weight edges as [A, B, weight_value] compositions.
///
/// ARCHITECTURE:
/// - Weight edge = composition with refs = [input_atom, output_atom, weight_value_atom]
/// - input_atom: Already atomized token (from text ingestion)
/// - output_atom: Already atomized token (from text ingestion)
/// - weight_value_atom: Constant atom representing the numeric weight value
///
/// The weight_value contributes to path priority during traversal.
/// Positive weights strengthen paths, negative weights weaken them.
///
/// GEOMETRY:
/// - Edge is LINESTRING from input position to output position
/// - Weight value influences the "trajectory" through the hypersphere
/// </summary>
public class WeightEdgeService : IngestionServiceBase
{
    public WeightEdgeService(HartDbContext context, ILogger<WeightEdgeService>? logger = null)
        : base(context, logger) { }

    /// <summary>
    /// Create a weight edge connecting two atoms with a weight value.
    /// Returns the edge composition atom ID.
    /// </summary>
    public async Task<long> CreateWeightEdgeAsync(
        long inputAtomId,
        long outputAtomId,
        float weight,
        long? typeRef = null,
        CancellationToken ct = default)
    {
        // Get or create the weight value constant
        uint weightBits = BitConverter.SingleToUInt32Bits(weight);
        var weightAtomId = await GetOrCreateConstantAsync(weightBits, SEED_TYPE_FLOAT_BITS, null, ct);

        // Create the edge composition: [input, output, weight]
        var refs = new[] { inputAtomId, outputAtomId, weightAtomId };
        var multiplicities = new[] { 1, 1, 1 };

        return await CreateWeightEdgeCompositionAsync(refs, multiplicities, typeRef, ct);
    }

    /// <summary>
    /// Create a weight edge with integer weight (for quantized models).
    /// </summary>
    public async Task<long> CreateWeightEdgeAsync(
        long inputAtomId,
        long outputAtomId,
        int weight,
        long? typeRef = null,
        CancellationToken ct = default)
    {
        // Create integer weight constant
        var weightAtomId = await GetOrCreateIntegerConstantAsync(weight, null, ct);

        var refs = new[] { inputAtomId, outputAtomId, weightAtomId };
        var multiplicities = new[] { 1, 1, 1 };

        return await CreateWeightEdgeCompositionAsync(refs, multiplicities, typeRef, ct);
    }

    /// <summary>
    /// Bulk create weight edges for a layer of neural network weights.
    /// More efficient than creating edges one by one.
    /// </summary>
    public async Task<long[]> BulkCreateWeightEdgesAsync(
        long[] inputAtomIds,
        long[] outputAtomIds,
        float[] weights,
        long? typeRef = null,
        CancellationToken ct = default)
    {
        if (inputAtomIds.Length != outputAtomIds.Length || inputAtomIds.Length != weights.Length)
            throw new ArgumentException("All arrays must have same length");

        var edgeCount = inputAtomIds.Length;
        var result = new long[edgeCount];

        // Extract unique weight values and bulk create their constants
        var uniqueWeightBits = weights.Select(w => BitConverter.SingleToUInt32Bits(w)).Distinct().ToArray();
        var weightLookup = await BulkGetOrCreateConstantsAsync(uniqueWeightBits, SEED_TYPE_FLOAT_BITS, null, ct);

        // Create edges in batches
        for (int i = 0; i < edgeCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var weightBits = BitConverter.SingleToUInt32Bits(weights[i]);
            var weightAtomId = weightLookup[weightBits];

            var refs = new[] { inputAtomIds[i], outputAtomIds[i], weightAtomId };
            var multiplicities = new[] { 1, 1, 1 };

            result[i] = await CreateWeightEdgeCompositionAsync(refs, multiplicities, typeRef, ct);
        }

        return result;
    }

    /// <summary>
    /// Create weight edges for an attention head matrix.
    /// Creates edges only for weights above threshold to reduce storage.
    /// </summary>
    public async Task<List<long>> CreateAttentionEdgesAsync(
        float[,] attentionMatrix,
        long[] tokenAtomIds,
        float threshold = 0.01f,
        long? typeRef = null,
        CancellationToken ct = default)
    {
        var rows = attentionMatrix.GetLength(0);
        var cols = attentionMatrix.GetLength(1);

        if (tokenAtomIds.Length < Math.Max(rows, cols))
            throw new ArgumentException("Not enough token atoms for attention matrix dimensions");

        var edgeAtomIds = new List<long>();

        // Collect all significant weights first
        var edges = new List<(int from, int to, float weight)>();
        for (int from = 0; from < rows; from++)
        {
            for (int to = 0; to < cols; to++)
            {
                var weight = attentionMatrix[from, to];
                if (Math.Abs(weight) >= threshold)
                {
                    edges.Add((from, to, weight));
                }
            }
        }

        Logger?.LogDebug("Creating {Count} attention edges (threshold {Threshold})", edges.Count, threshold);

        // Bulk create weight constants
        var uniqueWeightBits = edges.Select(e => BitConverter.SingleToUInt32Bits(e.weight)).Distinct().ToArray();
        var weightLookup = await BulkGetOrCreateConstantsAsync(uniqueWeightBits, SEED_TYPE_FLOAT_BITS, null, ct);

        // Create edge compositions
        foreach (var (from, to, weight) in edges)
        {
            ct.ThrowIfCancellationRequested();

            var weightBits = BitConverter.SingleToUInt32Bits(weight);
            var weightAtomId = weightLookup[weightBits];

            var refs = new[] { tokenAtomIds[from], tokenAtomIds[to], weightAtomId };
            var multiplicities = new[] { 1, 1, 1 };

            var edgeId = await CreateWeightEdgeCompositionAsync(refs, multiplicities, typeRef, ct);
            edgeAtomIds.Add(edgeId);
        }

        return edgeAtomIds;
    }

    /// <summary>
    /// Get the weight value from a weight edge atom.
    /// </summary>
    public async Task<float?> GetWeightValueAsync(long edgeAtomId, CancellationToken ct = default)
    {
        var edge = await Context.Atoms.FindAsync(new object[] { edgeAtomId }, ct);
        if (edge?.Refs == null || edge.Refs.Length < 3)
            return null;

        var weightAtomId = edge.Refs[2];
        var weightAtom = await Context.Atoms.FindAsync(new object[] { weightAtomId }, ct);

        if (weightAtom?.SeedValue == null)
            return null;

        if (weightAtom.SeedType == SEED_TYPE_FLOAT_BITS)
        {
            uint bits = (uint)weightAtom.SeedValue.Value;
            return BitConverter.UInt32BitsToSingle(bits);
        }
        else if (weightAtom.SeedType == SEED_TYPE_INTEGER)
        {
            return (float)weightAtom.SeedValue.Value;
        }

        return null;
    }

    /// <summary>
    /// Get input and output atom IDs from a weight edge.
    /// </summary>
    public async Task<(long inputAtomId, long outputAtomId)?> GetEdgeEndpointsAsync(
        long edgeAtomId,
        CancellationToken ct = default)
    {
        var edge = await Context.Atoms.FindAsync(new object[] { edgeAtomId }, ct);
        if (edge?.Refs == null || edge.Refs.Length < 2)
            return null;

        return (edge.Refs[0], edge.Refs[1]);
    }

    /// <summary>
    /// Create edge composition with LINESTRING geometry.
    /// </summary>
    private async Task<long> CreateWeightEdgeCompositionAsync(
        long[] refs,
        int[] multiplicities,
        long? typeRef,
        CancellationToken ct)
    {
        var contentHash = NativeLibrary.ComputeCompositionHash(refs, multiplicities);

        // Check for existing (content-addressed deduplication)
        var existing = await Context.Atoms
            .Where(a => a.ContentHash == contentHash && !a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Load endpoint atoms to build geometry
        var inputAtom = await Context.Atoms.FindAsync(new object[] { refs[0] }, ct);
        var outputAtom = await Context.Atoms.FindAsync(new object[] { refs[1] }, ct);

        if (inputAtom?.Geom == null || outputAtom?.Geom == null)
            throw new InvalidOperationException("Input or output atom geometry not found");

        // Create LINESTRING from input to output
        var inputCoord = ExtractCoordinate(inputAtom.Geom);
        var outputCoord = ExtractCoordinate(outputAtom.Geom);
        var lineString = GeometryFactory.CreateLineString(new[] { inputCoord, outputCoord });

        // Hilbert from midpoint
        var midpoint = lineString.Centroid.Coordinate;
        var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
        {
            X = midpoint.X,
            Y = midpoint.Y,
            Z = double.IsNaN(midpoint.Z) ? 0 : midpoint.Z,
            M = double.IsNaN(midpoint.M) ? 0 : midpoint.M
        });

        var edge = new Entities.Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = lineString,
            IsConstant = false,
            Refs = refs,
            Multiplicities = multiplicities,
            ContentHash = contentHash,
            TypeRef = typeRef
        };

        Context.Atoms.Add(edge);
        await Context.SaveChangesAsync(ct);

        return edge.Id;
    }

    private static CoordinateZM ExtractCoordinate(Geometry geom)
    {
        var coord = geom is Point p ? p.Coordinate : geom.Centroid.Coordinate;
        return new CoordinateZM(
            coord.X,
            coord.Y,
            double.IsNaN(coord.Z) ? 0 : coord.Z,
            double.IsNaN(coord.M) ? 0 : coord.M
        );
    }
}
