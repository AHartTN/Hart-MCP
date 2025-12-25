using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services;

/// <summary>
/// AI Query Service - provides attention, inference, transformation, and generation
/// operations on the spatial knowledge substrate.
/// 
/// Key insight: Storage IS the model. Query IS inference. PostGIS = semantic operations.
/// All content is atoms with AtomType discrimination and JSONB metadata.
/// </summary>
public class AIQueryService
{
    private readonly HartDbContext _context;
    private readonly ILogger<AIQueryService>? _logger;
    private readonly GeometryFactory _geometryFactory;

    public AIQueryService(
        HartDbContext context,
        ILogger<AIQueryService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    #region Attention Queries - Find what the system "attends to"

    /// <summary>
    /// Compute attention weights between a query and a set of key atoms.
    /// Uses spatial distance as inverse attention weight.
    /// </summary>
    public async Task<List<AttentionResult>> ComputeAttentionAsync(
        long queryAtomId,
        IEnumerable<long> keyAtomIds,
        CancellationToken cancellationToken = default)
    {
        var queryAtom = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == queryAtomId, cancellationToken);

        if (queryAtom == null)
            throw new InvalidOperationException($"Query atom {queryAtomId} not found");

        var keyIds = keyAtomIds.ToList();
        var keyAtoms = await _context.Atoms
            .AsNoTracking()
            .Where(a => keyIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        var results = new List<AttentionResult>();
        double totalWeight = 0;

        foreach (var key in keyAtoms)
        {
            double distance = queryAtom.Geom.Distance(key.Geom);
            double rawWeight = 1.0 / (1.0 + distance); // Inverse distance
            totalWeight += rawWeight;
            results.Add(new AttentionResult
            {
                KeyAtomId = key.Id,
                RawWeight = rawWeight,
                Distance = distance
            });
        }

        // Normalize (softmax-style)
        foreach (var result in results)
        {
            result.NormalizedWeight = totalWeight > 0 ? result.RawWeight / totalWeight : 0;
        }

        return results.OrderByDescending(r => r.NormalizedWeight).ToList();
    }

    /// <summary>
    /// Multi-head attention: compute attention from multiple query perspectives
    /// Each "head" uses a different dimensional projection
    /// </summary>
    public async Task<MultiHeadAttentionResult> ComputeMultiHeadAttentionAsync(
        long queryAtomId,
        IEnumerable<long> keyAtomIds,
        int numHeads = 4,
        CancellationToken cancellationToken = default)
    {
        var queryAtom = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == queryAtomId, cancellationToken);

        if (queryAtom == null)
            throw new InvalidOperationException($"Query atom {queryAtomId} not found");

        var keyIds = keyAtomIds.ToList();
        var keyAtoms = await _context.Atoms
            .AsNoTracking()
            .Where(a => keyIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        var heads = new List<List<AttentionResult>>();
        var queryCoord = queryAtom.Geom.Coordinate;

        for (int h = 0; h < numHeads; h++)
        {
            var headResults = new List<AttentionResult>();
            double totalWeight = 0;

            // Each head projects to different 2D subspace of the 4D space
            foreach (var key in keyAtoms)
            {
                var keyCoord = key.Geom.Coordinate;
                double distance = ComputeHeadDistance(queryCoord, keyCoord, h, numHeads);
                double rawWeight = 1.0 / (1.0 + distance);
                totalWeight += rawWeight;

                headResults.Add(new AttentionResult
                {
                    KeyAtomId = key.Id,
                    RawWeight = rawWeight,
                    Distance = distance
                });
            }

            // Normalize per head
            foreach (var result in headResults)
            {
                result.NormalizedWeight = totalWeight > 0 ? result.RawWeight / totalWeight : 0;
            }

            heads.Add(headResults.OrderByDescending(r => r.NormalizedWeight).ToList());
        }

        // Aggregate across heads
        var aggregated = new Dictionary<long, double>();
        foreach (var head in heads)
        {
            foreach (var result in head)
            {
                if (!aggregated.ContainsKey(result.KeyAtomId))
                    aggregated[result.KeyAtomId] = 0;
                aggregated[result.KeyAtomId] += result.NormalizedWeight;
            }
        }

        // Normalize aggregated
        var totalAgg = aggregated.Values.Sum();
        var finalResults = aggregated.Select(kvp => new AttentionResult
        {
            KeyAtomId = kvp.Key,
            NormalizedWeight = totalAgg > 0 ? kvp.Value / totalAgg : 0,
            RawWeight = kvp.Value,
            Distance = 0 // Aggregated, no single distance
        }).OrderByDescending(r => r.NormalizedWeight).ToList();

        return new MultiHeadAttentionResult
        {
            Heads = heads,
            Aggregated = finalResults,
            NumHeads = numHeads
        };
    }

    private static double ComputeHeadDistance(Coordinate q, Coordinate k, int head, int numHeads)
    {
        // Each head looks at a different pair of dimensions
        // Head 0: X,Y | Head 1: Y,Z | Head 2: Z,M | Head 3: M,X
        double d1, d2;
        switch (head % 4)
        {
            case 0:
                d1 = q.X - k.X;
                d2 = q.Y - k.Y;
                break;
            case 1:
                d1 = q.Y - k.Y;
                d2 = q.Z - k.Z;
                break;
            case 2:
                d1 = q.Z - k.Z;
                d2 = q.M - k.M;
                break;
            default:
                d1 = q.M - k.M;
                d2 = q.X - k.X;
                break;
        }
        return Math.Sqrt(d1 * d1 + d2 * d2);
    }

    #endregion

    #region Inference Queries - Derive knowledge from spatial relationships

    /// <summary>
    /// Infer related concepts by spatial proximity.
    /// "What is semantically near this content?"
    /// </summary>
    public async Task<List<InferenceResult>> InferRelatedConceptsAsync(
        long atomId,
        double radius = 0.1,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var atom = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == atomId, cancellationToken);

        if (atom == null)
            throw new InvalidOperationException($"Atom {atomId} not found");

        _logger?.LogDebug("Inferring concepts within radius {Radius} of atom {AtomId}", radius, atomId);

        // Use PostGIS ST_DWithin for efficient spatial query
        var nearby = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.Id != atomId && a.Geom.IsWithinDistance(atom.Geom, radius))
            .OrderBy(a => a.Geom.Distance(atom.Geom))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return nearby.Select(n => new InferenceResult
        {
            AtomId = n.Id,
            Confidence = 1.0 / (1.0 + n.Geom.Distance(atom.Geom)),
            RelationType = DetermineRelationType(atom, n),
            AtomType = n.AtomType,
            IsConstant = n.IsConstant
        }).ToList();
    }

    /// <summary>
    /// Infer missing connections using Hilbert gap analysis.
    /// "What should exist in this region but doesn't?"
    /// </summary>
    public async Task<List<GapInferenceResult>> InferFromGapsAsync(
        long hilbertHigh,
        long hilbertLow,
        long range = 1000,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Analyzing gaps near Hilbert index [{High}:{Low}]", hilbertHigh, hilbertLow);

        // Find atoms in range
        var neighbors = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.HilbertHigh >= hilbertHigh - 1 && a.HilbertHigh <= hilbertHigh + 1)
            .OrderBy(a => a.HilbertHigh)
            .ThenBy(a => a.HilbertLow)
            .Take(100)
            .ToListAsync(cancellationToken);

        var results = new List<GapInferenceResult>();

        // Find gaps in the Hilbert sequence
        for (int i = 0; i < neighbors.Count - 1; i++)
        {
            var current = neighbors[i];
            var next = neighbors[i + 1];

            // Calculate gap size
            long gapSize = 0;
            if (current.HilbertHigh == next.HilbertHigh)
            {
                gapSize = next.HilbertLow - current.HilbertLow;
            }
            else
            {
                gapSize = long.MaxValue; // Large gap
            }

            // If gap is significant, infer potential content
            if (gapSize > range)
            {
                // Calculate midpoint coordinates
                var midpoint = new NativeLibrary.PointZM
                {
                    X = (current.Geom.Coordinate.X + next.Geom.Coordinate.X) / 2,
                    Y = (current.Geom.Coordinate.Y + next.Geom.Coordinate.Y) / 2,
                    Z = (current.Geom.Coordinate.Z + next.Geom.Coordinate.Z) / 2,
                    M = (current.Geom.Coordinate.M + next.Geom.Coordinate.M) / 2
                };

                results.Add(new GapInferenceResult
                {
                    GapStartAtomId = current.Id,
                    GapEndAtomId = next.Id,
                    GapSize = gapSize,
                    PredictedX = midpoint.X,
                    PredictedY = midpoint.Y,
                    PredictedZ = midpoint.Z,
                    PredictedM = midpoint.M,
                    Confidence = Math.Min(1.0, (double)range / gapSize)
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Chain inference: follow composition refs to derive implications.
    /// "If A references B which references C, what does this imply about A and C?"
    /// </summary>
    public async Task<InferenceChain> InferChainAsync(
        long rootAtomId,
        int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<long>();
        var chain = new InferenceChain { RootAtomId = rootAtomId };

        await TraverseChainAsync(rootAtomId, 0, maxDepth, visited, chain, cancellationToken);

        return chain;
    }

    private async Task TraverseChainAsync(
        long atomId,
        int depth,
        int maxDepth,
        HashSet<long> visited,
        InferenceChain chain,
        CancellationToken cancellationToken)
    {
        if (depth >= maxDepth || visited.Contains(atomId))
            return;

        visited.Add(atomId);

        var atom = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == atomId, cancellationToken);

        if (atom == null)
            return;

        chain.Nodes.Add(new ChainNode
        {
            AtomId = atomId,
            Depth = depth,
            AtomType = atom.AtomType,
            IsConstant = atom.IsConstant
        });

        if (atom.Refs != null)
        {
            foreach (var refId in atom.Refs)
            {
                chain.Edges.Add(new ChainEdge
                {
                    FromAtomId = atomId,
                    ToAtomId = refId,
                    Depth = depth
                });

                await TraverseChainAsync(refId, depth + 1, maxDepth, visited, chain, cancellationToken);
            }
        }
    }

    private static string DetermineRelationType(Atom source, Atom target)
    {
        if (source.IsConstant && target.IsConstant)
            return "semantic_neighbor";
        if (!source.IsConstant && target.IsConstant)
            return "composition_to_constant";
        if (source.IsConstant && !target.IsConstant)
            return "constant_to_composition";
        return "composition_similarity";
    }

    #endregion

    #region Transformation Queries - Map between representations

    /// <summary>
    /// Transform content from one representation to another.
    /// E.g., text → embedding trajectory, or embedding → nearest text
    /// </summary>
    public async Task<TransformationResult> TransformAsync(
        long sourceAtomId,
        string targetRepresentation,
        CancellationToken cancellationToken = default)
    {
        var source = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == sourceAtomId, cancellationToken);

        if (source == null)
            throw new InvalidOperationException($"Source atom {sourceAtomId} not found");

        _logger?.LogDebug("Transforming atom {AtomId} to {Target}", sourceAtomId, targetRepresentation);

        return targetRepresentation.ToLowerInvariant() switch
        {
            "embedding" => await TransformToEmbeddingAsync(source, cancellationToken),
            "text" => await TransformToTextAsync(source, cancellationToken),
            "hilbert" => TransformToHilbert(source),
            "coordinates" => TransformToCoordinates(source),
            _ => throw new ArgumentException($"Unknown target representation: {targetRepresentation}")
        };
    }

    private async Task<TransformationResult> TransformToEmbeddingAsync(Atom source, CancellationToken cancellationToken)
    {
        // If composition, get all child coordinates as embedding dimensions
        if (!source.IsConstant && source.Refs != null)
        {
            var children = await _context.Atoms
                .AsNoTracking()
                .Where(a => source.Refs.Contains(a.Id))
                .ToListAsync(cancellationToken);

            var embedding = children.Select(c => (float)c.Geom.Coordinate.X).ToArray();
            return new TransformationResult
            {
                SourceAtomId = source.Id,
                TargetRepresentation = "embedding",
                Data = embedding,
                Dimensions = embedding.Length
            };
        }

        // Single atom → 4D vector
        var coord = source.Geom.Coordinate;
        return new TransformationResult
        {
            SourceAtomId = source.Id,
            TargetRepresentation = "embedding",
            Data = new float[] { (float)coord.X, (float)coord.Y, (float)coord.Z, (float)coord.M },
            Dimensions = 4
        };
    }

    private async Task<TransformationResult> TransformToTextAsync(Atom source, CancellationToken cancellationToken)
    {
        if (source.IsConstant && source.SeedType == 0 && source.SeedValue.HasValue)
        {
            // Unicode constant → character
            var text = char.ConvertFromUtf32((int)source.SeedValue.Value);
            return new TransformationResult
            {
                SourceAtomId = source.Id,
                TargetRepresentation = "text",
                Data = text,
                Dimensions = text.Length
            };
        }

        if (!source.IsConstant && source.Refs != null)
        {
            // Composition → reconstruct text
            var children = await _context.Atoms
                .AsNoTracking()
                .Where(a => source.Refs.Contains(a.Id) && a.IsConstant && a.SeedType == 0)
                .ToDictionaryAsync(a => a.Id, cancellationToken);

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < source.Refs.Length; i++)
            {
                if (children.TryGetValue(source.Refs[i], out var child) && child.SeedValue.HasValue)
                {
                    var ch = char.ConvertFromUtf32((int)child.SeedValue.Value);
                    var mult = source.Multiplicities?[i] ?? 1;
                    for (int m = 0; m < mult; m++)
                        text.Append(ch);
                }
            }

            return new TransformationResult
            {
                SourceAtomId = source.Id,
                TargetRepresentation = "text",
                Data = text.ToString(),
                Dimensions = text.Length
            };
        }

        throw new InvalidOperationException("Cannot transform to text: source is not a text composition");
    }

    private static TransformationResult TransformToHilbert(Atom source)
    {
        return new TransformationResult
        {
            SourceAtomId = source.Id,
            TargetRepresentation = "hilbert",
            Data = new { high = source.HilbertHigh, low = source.HilbertLow },
            Dimensions = 2
        };
    }

    private static TransformationResult TransformToCoordinates(Atom source)
    {
        var coord = source.Geom.Coordinate;
        return new TransformationResult
        {
            SourceAtomId = source.Id,
            TargetRepresentation = "coordinates",
            Data = new { x = coord.X, y = coord.Y, z = coord.Z, m = coord.M },
            Dimensions = 4
        };
    }

    /// <summary>
    /// Transform trajectory: apply function to all points on a trajectory
    /// </summary>
    public async Task<long> TransformTrajectoryAsync(
        long trajectoryAtomId,
        Func<Coordinate, Coordinate> transform,
        string newAtomType,
        CancellationToken cancellationToken = default)
    {
        var trajectory = await _context.Atoms
            .FirstOrDefaultAsync(a => a.Id == trajectoryAtomId && !a.IsConstant, cancellationToken);

        if (trajectory == null)
            throw new InvalidOperationException($"Trajectory {trajectoryAtomId} not found");

        // Get all points on trajectory
        var coords = trajectory.Geom.Coordinates;
        var transformedCoords = coords.Select(c =>
        {
            var t = transform(c);
            return new CoordinateZM(t.X, t.Y, t.Z, t.M);
        }).ToArray();

        // Create new geometry
        Geometry newGeom = transformedCoords.Length == 1
            ? _geometryFactory.CreatePoint(transformedCoords[0])
            : _geometryFactory.CreateLineString(transformedCoords);

        // Compute hash and Hilbert for new trajectory
        var centroid = newGeom.Centroid.Coordinate;
        var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
        {
            X = centroid.X,
            Y = centroid.Y,
            Z = centroid.Z,
            M = centroid.M
        });

        // For transformed trajectories, we create a new composition
        // referencing the original with a transformation marker
        var transformedAtom = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = newGeom,
            IsConstant = false,
            Refs = new[] { trajectoryAtomId },
            Multiplicities = new[] { 1 },
            ContentHash = NativeLibrary.ComputeCompositionHash(new[] { trajectoryAtomId }, new[] { 1 }),
            AtomType = newAtomType
        };

        _context.Atoms.Add(transformedAtom);
        await _context.SaveChangesAsync(cancellationToken);

        return transformedAtom.Id;
    }

    #endregion

    #region Generation Queries - Create new content from patterns

    /// <summary>
    /// Generate next likely atoms based on spatial context.
    /// "Given this trajectory, what comes next?"
    /// </summary>
    public async Task<List<GenerationCandidate>> GenerateNextAsync(
        long[] contextAtomIds,
        int numCandidates = 5,
        CancellationToken cancellationToken = default)
    {
        if (contextAtomIds.Length == 0)
            throw new ArgumentException("Context cannot be empty", nameof(contextAtomIds));

        var contextAtoms = await _context.Atoms
            .AsNoTracking()
            .Where(a => contextAtomIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        if (contextAtoms.Count == 0)
            throw new InvalidOperationException("No context atoms found");

        // Compute trajectory vector from context
        var contextCoords = contextAtoms.Select(a => a.Geom.Coordinate).ToList();
        var centroid = ComputeCentroid(contextCoords);

        // If we have a sequence, compute momentum (direction of travel)
        Coordinate momentum = new Coordinate(0, 0);
        if (contextCoords.Count >= 2)
        {
            var last = contextCoords[^1];
            var prev = contextCoords[^2];
            momentum = new Coordinate(last.X - prev.X, last.Y - prev.Y);
        }

        // Predict next position
        var predicted = new Coordinate(
            centroid.X + momentum.X,
            centroid.Y + momentum.Y
        );

        var predictedGeom = _geometryFactory.CreatePoint(predicted);

        // Find atoms near predicted position
        var candidates = await _context.Atoms
            .AsNoTracking()
            .Where(a => !contextAtomIds.Contains(a.Id))
            .OrderBy(a => a.Geom.Distance(predictedGeom))
            .Take(numCandidates * 2) // Get more to filter
            .ToListAsync(cancellationToken);

        return candidates.Take(numCandidates).Select((c, i) => new GenerationCandidate
        {
            AtomId = c.Id,
            Probability = 1.0 / (1.0 + c.Geom.Distance(predictedGeom)),
            Rank = i + 1,
            AtomType = c.AtomType,
            Distance = c.Geom.Distance(predictedGeom)
        }).ToList();
    }

    /// <summary>
    /// Generate by analogy: "A is to B as C is to ?"
    /// </summary>
    public async Task<List<GenerationCandidate>> GenerateByAnalogyAsync(
        long atomA,
        long atomB,
        long atomC,
        int numCandidates = 5,
        CancellationToken cancellationToken = default)
    {
        var atoms = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.Id == atomA || a.Id == atomB || a.Id == atomC)
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        if (!atoms.ContainsKey(atomA) || !atoms.ContainsKey(atomB) || !atoms.ContainsKey(atomC))
            throw new InvalidOperationException("One or more analogy atoms not found");

        var coordA = atoms[atomA].Geom.Coordinate;
        var coordB = atoms[atomB].Geom.Coordinate;
        var coordC = atoms[atomC].Geom.Coordinate;

        // Compute A→B vector and apply to C
        var vectorAB = new CoordinateZM(
            coordB.X - coordA.X,
            coordB.Y - coordA.Y,
            coordB.Z - coordA.Z,
            coordB.M - coordA.M
        );

        var predictedD = new CoordinateZM(
            coordC.X + vectorAB.X,
            coordC.Y + vectorAB.Y,
            coordC.Z + vectorAB.Z,
            coordC.M + vectorAB.M
        );

        var predictedGeom = _geometryFactory.CreatePoint(predictedD);

        // Find atoms near predicted D position
        var candidates = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.Id != atomA && a.Id != atomB && a.Id != atomC)
            .OrderBy(a => a.Geom.Distance(predictedGeom))
            .Take(numCandidates)
            .ToListAsync(cancellationToken);

        return candidates.Select((c, i) => new GenerationCandidate
        {
            AtomId = c.Id,
            Probability = 1.0 / (1.0 + c.Geom.Distance(predictedGeom)),
            Rank = i + 1,
            AtomType = c.AtomType,
            Distance = c.Geom.Distance(predictedGeom)
        }).ToList();
    }

    /// <summary>
    /// Generate composition: combine multiple atoms into new structure
    /// </summary>
    public async Task<long> GenerateCompositionAsync(
        long[] componentAtomIds,
        string compositionType,
        CancellationToken cancellationToken = default)
    {
        if (componentAtomIds.Length == 0)
            throw new ArgumentException("Components cannot be empty", nameof(componentAtomIds));

        var components = await _context.Atoms
            .AsNoTracking()
            .Where(a => componentAtomIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        if (components.Count != componentAtomIds.Length)
            throw new InvalidOperationException("Some component atoms not found");

        // Build geometry from component points
        var coords = componentAtomIds
            .Select(id => components[id].Geom.Coordinate)
            .Select(c => new CoordinateZM(c.X, c.Y, c.Z, c.M))
            .ToArray();

        Geometry geom = coords.Length == 1
            ? _geometryFactory.CreatePoint(coords[0])
            : _geometryFactory.CreateLineString(coords);

        // Compute hash and Hilbert
        var multiplicities = Enumerable.Repeat(1, componentAtomIds.Length).ToArray();
        var contentHash = NativeLibrary.ComputeCompositionHash(componentAtomIds, multiplicities);

        // Check if already exists
        var existing = await _context.Atoms
            .Where(a => a.ContentHash == contentHash)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
            return existing;

        var centroid = geom.Centroid.Coordinate;
        var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
        {
            X = centroid.X,
            Y = centroid.Y,
            Z = centroid.Z,
            M = centroid.M
        });

        var composition = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = geom,
            IsConstant = false,
            Refs = componentAtomIds,
            Multiplicities = multiplicities,
            ContentHash = contentHash,
            AtomType = compositionType
        };

        _context.Atoms.Add(composition);
        await _context.SaveChangesAsync(cancellationToken);

        return composition.Id;
    }

    private static Coordinate ComputeCentroid(List<Coordinate> coords)
    {
        if (coords.Count == 0)
            return new Coordinate(0, 0);

        double x = coords.Average(c => c.X);
        double y = coords.Average(c => c.Y);
        double z = coords.Average(c => double.IsNaN(c.Z) ? 0 : c.Z);
        double m = coords.Average(c => double.IsNaN(c.M) ? 0 : c.M);

        return new CoordinateZM(x, y, z, m);
    }

    #endregion
}

#region Result Types

public class AttentionResult
{
    public long KeyAtomId { get; set; }
    public double RawWeight { get; set; }
    public double NormalizedWeight { get; set; }
    public double Distance { get; set; }
}

public class MultiHeadAttentionResult
{
    public List<List<AttentionResult>> Heads { get; set; } = new();
    public List<AttentionResult> Aggregated { get; set; } = new();
    public int NumHeads { get; set; }
}

public class InferenceResult
{
    public long AtomId { get; set; }
    public double Confidence { get; set; }
    public string RelationType { get; set; } = "";
    public string AtomType { get; set; } = "";
    public bool IsConstant { get; set; }
}

public class GapInferenceResult
{
    public long GapStartAtomId { get; set; }
    public long GapEndAtomId { get; set; }
    public long GapSize { get; set; }
    public double PredictedX { get; set; }
    public double PredictedY { get; set; }
    public double PredictedZ { get; set; }
    public double PredictedM { get; set; }
    public double Confidence { get; set; }
}

public class InferenceChain
{
    public long RootAtomId { get; set; }
    public List<ChainNode> Nodes { get; set; } = new();
    public List<ChainEdge> Edges { get; set; } = new();
}

public class ChainNode
{
    public long AtomId { get; set; }
    public int Depth { get; set; }
    public string AtomType { get; set; } = "";
    public bool IsConstant { get; set; }
}

public class ChainEdge
{
    public long FromAtomId { get; set; }
    public long ToAtomId { get; set; }
    public int Depth { get; set; }
}

public class TransformationResult
{
    public long SourceAtomId { get; set; }
    public string TargetRepresentation { get; set; } = "";
    public object? Data { get; set; }
    public int Dimensions { get; set; }
}

public class GenerationCandidate
{
    public long AtomId { get; set; }
    public double Probability { get; set; }
    public int Rank { get; set; }
    public string AtomType { get; set; } = "";
    public double Distance { get; set; }
}

#endregion
