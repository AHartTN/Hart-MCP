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
        var weightConstantId = await GetOrCreateConstantAsync(weightBits, SEED_TYPE_FLOAT_BITS, ct);

        // Create the edge composition: [input, output, weight]
        // Input/output are compositions, weight is a constant
        var children = new (long id, bool isConstant)[] {
            (inputAtomId, false),   // input composition
            (outputAtomId, false),  // output composition
            (weightConstantId, true) // weight constant
        };
        var multiplicities = new[] { 1, 1, 1 };

        return await CreateWeightEdgeCompositionAsync(children, multiplicities, typeRef, ct);
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
        var weightConstantId = await GetOrCreateConstantAsync(weight, SEED_TYPE_INTEGER, ct);

        // Input/output are compositions, weight is a constant
        var children = new (long id, bool isConstant)[] {
            (inputAtomId, false),
            (outputAtomId, false),
            (weightConstantId, true)
        };
        var multiplicities = new[] { 1, 1, 1 };

        return await CreateWeightEdgeCompositionAsync(children, multiplicities, typeRef, ct);
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
        var weightLookup = await BulkGetOrCreateConstantsAsync(uniqueWeightBits, SEED_TYPE_FLOAT_BITS, ct);

        // Create edges in batches
        for (int i = 0; i < edgeCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var weightBits = BitConverter.SingleToUInt32Bits(weights[i]);
            var weightConstantId = weightLookup[weightBits];

            var children = new (long id, bool isConstant)[] {
                (inputAtomIds[i], false),
                (outputAtomIds[i], false),
                (weightConstantId, true)
            };
            var multiplicities = new[] { 1, 1, 1 };

            result[i] = await CreateWeightEdgeCompositionAsync(children, multiplicities, typeRef, ct);
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
        var weightLookup = await BulkGetOrCreateConstantsAsync(uniqueWeightBits, SEED_TYPE_FLOAT_BITS, ct);

        // Create edge compositions
        foreach (var (from, to, weight) in edges)
        {
            ct.ThrowIfCancellationRequested();

            var weightBits = BitConverter.SingleToUInt32Bits(weight);
            var weightConstantId = weightLookup[weightBits];

            var children = new (long id, bool isConstant)[] {
                (tokenAtomIds[from], false),
                (tokenAtomIds[to], false),
                (weightConstantId, true)
            };
            var multiplicities = new[] { 1, 1, 1 };

            var edgeId = await CreateWeightEdgeCompositionAsync(children, multiplicities, typeRef, ct);
            edgeAtomIds.Add(edgeId);
        }

        return edgeAtomIds;
    }

    /// <summary>
    /// Get the weight value from a weight edge composition.
    /// </summary>
    public async Task<float?> GetWeightValueAsync(long edgeCompositionId, CancellationToken ct = default)
    {
        var edgeRelations = await Context.Relations
            .Where(r => r.CompositionId == edgeCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (edgeRelations.Count < 3)
            return null;

        // Weight is the 3rd child (position 2) and should be a constant
        var weightRelation = edgeRelations[2];
        if (!weightRelation.ChildConstantId.HasValue)
            return null;

        var weightConstant = await Context.Constants.FindAsync(new object[] { weightRelation.ChildConstantId.Value }, ct);

        if (weightConstant == null)
            return null;

        if (weightConstant.SeedType == SEED_TYPE_FLOAT_BITS)
        {
            uint bits = (uint)weightConstant.SeedValue;
            return BitConverter.UInt32BitsToSingle(bits);
        }
        else if (weightConstant.SeedType == SEED_TYPE_INTEGER)
        {
            return (float)weightConstant.SeedValue;
        }

        return null;
    }

    /// <summary>
    /// Get input and output composition IDs from a weight edge.
    /// </summary>
    public async Task<(long inputCompositionId, long outputCompositionId)?> GetEdgeEndpointsAsync(
        long edgeCompositionId,
        CancellationToken ct = default)
    {
        var edgeRelations = await Context.Relations
            .Where(r => r.CompositionId == edgeCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (edgeRelations.Count < 2)
            return null;

        // Input and output should be compositions (not constants)
        var inputId = edgeRelations[0].ChildCompositionId;
        var outputId = edgeRelations[1].ChildCompositionId;

        if (!inputId.HasValue || !outputId.HasValue)
            return null;

        return (inputId.Value, outputId.Value);
    }

    /// <summary>
    /// Create edge composition with LINESTRING geometry.
    /// </summary>
    private async Task<long> CreateWeightEdgeCompositionAsync(
        (long id, bool isConstant)[] children,
        int[] multiplicities,
        long? typeId,
        CancellationToken ct)
    {
        var childIds = children.Select(c => c.id).ToArray();
        var contentHash = HartNative.ComputeCompositionHash(childIds, multiplicities);

        // Check for existing (content-addressed deduplication)
        var existing = await Context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Load endpoint compositions to build geometry (first two should be compositions)
        var inputComposition = await Context.Compositions.FindAsync(new object[] { children[0].id }, ct);
        var outputComposition = await Context.Compositions.FindAsync(new object[] { children[1].id }, ct);

        if (inputComposition?.Geom == null || outputComposition?.Geom == null)
            throw new InvalidOperationException("Input or output composition geometry not found");

        // Create LINESTRING from input to output
        var inputCoord = ExtractCoordinate(inputComposition.Geom);
        var outputCoord = ExtractCoordinate(outputComposition.Geom);
        var lineString = GeometryFactory.CreateLineString(new[] { inputCoord, outputCoord });

        // Hilbert from midpoint
        var midpoint = lineString.Centroid.Coordinate;
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = midpoint.X,
            Y = midpoint.Y,
            Z = double.IsNaN(midpoint.Z) ? 0 : midpoint.Z,
            M = double.IsNaN(midpoint.M) ? 0 : midpoint.M
        });

        var edge = new Entities.Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = lineString,
            ContentHash = contentHash,
            TypeId = typeId
        };

        Context.Compositions.Add(edge);
        await Context.SaveChangesAsync(ct);

        // Create Relation entries for the edge
        for (int i = 0; i < children.Length; i++)
        {
            var (childId, isConstant) = children[i];
            var relation = new Entities.Relation
            {
                CompositionId = edge.Id,
                Position = i,
                Multiplicity = multiplicities[i]
            };

            if (isConstant)
                relation.ChildConstantId = childId;
            else
                relation.ChildCompositionId = childId;

            Context.Relations.Add(relation);
        }
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
