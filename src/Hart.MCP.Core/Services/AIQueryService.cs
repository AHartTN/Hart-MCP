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
/// Uses Constants (leaf nodes), Compositions (internal nodes), and Relations (edges).
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
    /// Compute attention weights between a query constant and a set of key constants.
    /// Uses spatial distance as inverse attention weight.
    /// </summary>
    public async Task<List<AttentionResult>> ComputeAttentionAsync(
        long queryConstantId,
        IEnumerable<long> keyConstantIds,
        CancellationToken cancellationToken = default)
    {
        var queryConstant = await _context.Constants
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == queryConstantId, cancellationToken);

        if (queryConstant == null)
            throw new InvalidOperationException($"Query constant {queryConstantId} not found");

        var keyIds = keyConstantIds.ToList();
        var keyConstants = await _context.Constants
            .AsNoTracking()
            .Where(c => keyIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        var results = new List<AttentionResult>();
        double totalWeight = 0;

        foreach (var key in keyConstants)
        {
            double distance = queryConstant.Geom!.Distance(key.Geom!);
            double rawWeight = 1.0 / (1.0 + distance); // Inverse distance
            totalWeight += rawWeight;
            results.Add(new AttentionResult
            {
                KeyNodeId = key.Id,
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
        long queryConstantId,
        IEnumerable<long> keyConstantIds,
        int numHeads = 4,
        CancellationToken cancellationToken = default)
    {
        var queryConstant = await _context.Constants
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == queryConstantId, cancellationToken);

        if (queryConstant == null)
            throw new InvalidOperationException($"Query constant {queryConstantId} not found");

        var keyIds = keyConstantIds.ToList();
        var keyConstants = await _context.Constants
            .AsNoTracking()
            .Where(c => keyIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        var heads = new List<List<AttentionResult>>();
        var queryCoord = queryConstant.Geom!.Coordinate;

        for (int h = 0; h < numHeads; h++)
        {
            var headResults = new List<AttentionResult>();
            double totalWeight = 0;

            // Each head projects to different 2D subspace of the 4D space
            foreach (var key in keyConstants)
            {
                var keyCoord = key.Geom!.Coordinate;
                double distance = ComputeHeadDistance(queryCoord, keyCoord, h, numHeads);
                double rawWeight = 1.0 / (1.0 + distance);
                totalWeight += rawWeight;

                headResults.Add(new AttentionResult
                {
                    KeyNodeId = key.Id,
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
                if (!aggregated.ContainsKey(result.KeyNodeId))
                    aggregated[result.KeyNodeId] = 0;
                aggregated[result.KeyNodeId] += result.NormalizedWeight;
            }
        }

        // Normalize aggregated
        var totalAgg = aggregated.Values.Sum();
        var finalResults = aggregated.Select(kvp => new AttentionResult
        {
            KeyNodeId = kvp.Key,
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
    /// Infer related constants by spatial proximity.
    /// "What is semantically near this content?"
    /// </summary>
    public async Task<List<InferenceResult>> InferRelatedConstantsAsync(
        long constantId,
        double radius = 0.1,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var constant = await _context.Constants
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == constantId, cancellationToken);

        if (constant == null)
            throw new InvalidOperationException($"Constant {constantId} not found");

        _logger?.LogDebug("Inferring constants within radius {Radius} of constant {ConstantId}", radius, constantId);

        // Use PostGIS ST_DWithin for efficient spatial query
        var nearby = await _context.Constants
            .AsNoTracking()
            .Where(c => c.Id != constantId && c.Geom!.IsWithinDistance(constant.Geom!, radius))
            .OrderBy(c => c.Geom!.Distance(constant.Geom!))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return nearby.Select(n => new InferenceResult
        {
            NodeId = n.Id,
            Confidence = 1.0 / (1.0 + n.Geom!.Distance(constant.Geom!)),
            RelationType = "semantic_neighbor",
            IsConstant = true
        }).ToList();
    }

    /// <summary>
    /// Infer related compositions by spatial proximity.
    /// </summary>
    public async Task<List<InferenceResult>> InferRelatedCompositionsAsync(
        long compositionId,
        double radius = 0.1,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var composition = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);

        if (composition == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        _logger?.LogDebug("Inferring compositions within radius {Radius} of composition {CompositionId}", radius, compositionId);

        var nearby = await _context.Compositions
            .AsNoTracking()
            .Where(c => c.Id != compositionId && c.Geom!.IsWithinDistance(composition.Geom!, radius))
            .OrderBy(c => c.Geom!.Distance(composition.Geom!))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return nearby.Select(n => new InferenceResult
        {
            NodeId = n.Id,
            Confidence = 1.0 / (1.0 + n.Geom!.Distance(composition.Geom!)),
            RelationType = "composition_similarity",
            TypeId = n.TypeId,
            IsConstant = false
        }).ToList();
    }

    /// <summary>
    /// Infer missing connections using Hilbert gap analysis on constants.
    /// "What should exist in this region but doesn't?"
    /// </summary>
    public async Task<List<GapInferenceResult>> InferFromGapsAsync(
        ulong hilbertHigh,
        ulong hilbertLow,
        ulong range = 1000,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Analyzing gaps near Hilbert index [{High}:{Low}]", hilbertHigh, hilbertLow);

        // Find constants in range
        var neighbors = await _context.Constants
            .AsNoTracking()
            .Where(c => c.HilbertHigh >= hilbertHigh - 1 && c.HilbertHigh <= hilbertHigh + 1)
            .OrderBy(c => c.HilbertHigh)
            .ThenBy(c => c.HilbertLow)
            .Take(100)
            .ToListAsync(cancellationToken);

        var results = new List<GapInferenceResult>();

        // Find gaps in the Hilbert sequence
        for (int i = 0; i < neighbors.Count - 1; i++)
        {
            var current = neighbors[i];
            var next = neighbors[i + 1];

            // Calculate gap size
            ulong gapSize = 0;
            if (current.HilbertHigh == next.HilbertHigh)
            {
                gapSize = next.HilbertLow - current.HilbertLow;
            }
            else
            {
                gapSize = ulong.MaxValue; // Large gap
            }

            // If gap is significant, infer potential content
            if (gapSize > range)
            {
                // Calculate midpoint coordinates
                var midpoint = new NativeLibrary.PointZM
                {
                    X = (current.Geom!.Coordinate.X + next.Geom!.Coordinate.X) / 2,
                    Y = (current.Geom!.Coordinate.Y + next.Geom!.Coordinate.Y) / 2,
                    Z = (current.Geom!.Coordinate.Z + next.Geom!.Coordinate.Z) / 2,
                    M = (current.Geom!.Coordinate.M + next.Geom!.Coordinate.M) / 2
                };

                results.Add(new GapInferenceResult
                {
                    GapStartNodeId = current.Id,
                    GapEndNodeId = next.Id,
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
    /// Chain inference: follow composition relations to derive implications.
    /// "If A references B which references C, what does this imply about A and C?"
    /// </summary>
    public async Task<InferenceChain> InferChainAsync(
        long rootCompositionId,
        int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<long>();
        var chain = new InferenceChain { RootNodeId = rootCompositionId };

        await TraverseChainAsync(rootCompositionId, 0, maxDepth, visited, chain, cancellationToken);

        return chain;
    }

    private async Task TraverseChainAsync(
        long compositionId,
        int depth,
        int maxDepth,
        HashSet<long> visited,
        InferenceChain chain,
        CancellationToken cancellationToken)
    {
        if (depth >= maxDepth || visited.Contains(compositionId))
            return;

        visited.Add(compositionId);

        var composition = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);

        if (composition == null)
            return;

        chain.Nodes.Add(new ChainNode
        {
            NodeId = compositionId,
            Depth = depth,
            TypeId = composition.TypeId,
            IsConstant = false
        });

        // Get child relations
        var relations = await _context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);

        foreach (var relation in relations)
        {
            if (relation.ChildConstantId.HasValue)
            {
                chain.Edges.Add(new ChainEdge
                {
                    FromNodeId = compositionId,
                    ToNodeId = relation.ChildConstantId.Value,
                    Depth = depth,
                    IsToConstant = true
                });
                
                // Add constant as a leaf node if not already visited
                if (!visited.Contains(relation.ChildConstantId.Value))
                {
                    visited.Add(relation.ChildConstantId.Value);
                    chain.Nodes.Add(new ChainNode
                    {
                        NodeId = relation.ChildConstantId.Value,
                        Depth = depth + 1,
                        TypeId = null, // Constants don't have TypeId
                        IsConstant = true
                    });
                }
            }
            else if (relation.ChildCompositionId.HasValue)
            {
                chain.Edges.Add(new ChainEdge
                {
                    FromNodeId = compositionId,
                    ToNodeId = relation.ChildCompositionId.Value,
                    Depth = depth,
                    IsToConstant = false
                });

                await TraverseChainAsync(relation.ChildCompositionId.Value, depth + 1, maxDepth, visited, chain, cancellationToken);
            }
        }
    }

    #endregion

    #region Transformation Queries - Map between representations

    /// <summary>
    /// Transform constant content to another representation.
    /// E.g., constant → embedding, or constant → text character
    /// </summary>
    public async Task<TransformationResult> TransformConstantAsync(
        long constantId,
        string targetRepresentation,
        CancellationToken cancellationToken = default)
    {
        var constant = await _context.Constants
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == constantId, cancellationToken);

        if (constant == null)
            throw new InvalidOperationException($"Constant {constantId} not found");

        _logger?.LogDebug("Transforming constant {ConstantId} to {Target}", constantId, targetRepresentation);

        return targetRepresentation.ToLowerInvariant() switch
        {
            "embedding" => TransformConstantToEmbedding(constant),
            "text" => TransformConstantToText(constant),
            "hilbert" => TransformConstantToHilbert(constant),
            "coordinates" => TransformConstantToCoordinates(constant),
            _ => throw new ArgumentException($"Unknown target representation: {targetRepresentation}")
        };
    }

    /// <summary>
    /// Transform composition content to another representation.
    /// E.g., composition → embedding trajectory, or composition → reconstructed text
    /// </summary>
    public async Task<TransformationResult> TransformCompositionAsync(
        long compositionId,
        string targetRepresentation,
        CancellationToken cancellationToken = default)
    {
        var composition = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);

        if (composition == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        _logger?.LogDebug("Transforming composition {CompositionId} to {Target}", compositionId, targetRepresentation);

        return targetRepresentation.ToLowerInvariant() switch
        {
            "embedding" => await TransformCompositionToEmbeddingAsync(composition, cancellationToken),
            "text" => await TransformCompositionToTextAsync(composition, cancellationToken),
            "hilbert" => TransformCompositionToHilbert(composition),
            "coordinates" => TransformCompositionToCoordinates(composition),
            _ => throw new ArgumentException($"Unknown target representation: {targetRepresentation}")
        };
    }

    private static TransformationResult TransformConstantToEmbedding(Constant constant)
    {
        var coord = constant.Geom!.Coordinate;
        return new TransformationResult
        {
            SourceNodeId = constant.Id,
            TargetRepresentation = "embedding",
            Data = new float[] { (float)coord.X, (float)coord.Y, (float)coord.Z, (float)coord.M },
            Dimensions = 4
        };
    }

    private static TransformationResult TransformConstantToText(Constant constant)
    {
        // Unicode constant (SeedType == 1) → character
        if (constant.SeedType == 1)
        {
            var text = char.ConvertFromUtf32((int)constant.SeedValue);
            return new TransformationResult
            {
                SourceNodeId = constant.Id,
                TargetRepresentation = "text",
                Data = text,
                Dimensions = text.Length
            };
        }
        throw new InvalidOperationException("Constant is not a text (Unicode) constant");
    }

    private static TransformationResult TransformConstantToHilbert(Constant constant)
    {
        return new TransformationResult
        {
            SourceNodeId = constant.Id,
            TargetRepresentation = "hilbert",
            Data = new { high = constant.HilbertHigh, low = constant.HilbertLow },
            Dimensions = 2
        };
    }

    private static TransformationResult TransformConstantToCoordinates(Constant constant)
    {
        var coord = constant.Geom!.Coordinate;
        return new TransformationResult
        {
            SourceNodeId = constant.Id,
            TargetRepresentation = "coordinates",
            Data = new { x = coord.X, y = coord.Y, z = coord.Z, m = coord.M },
            Dimensions = 4
        };
    }

    private async Task<TransformationResult> TransformCompositionToEmbeddingAsync(Composition composition, CancellationToken cancellationToken)
    {
        // Get all child constant coordinates as embedding dimensions
        var relations = await _context.Relations
            .Where(r => r.CompositionId == composition.Id && r.ChildConstantId.HasValue)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);

        if (relations.Count > 0)
        {
            var childIds = relations.Select(r => r.ChildConstantId!.Value).ToList();
            var children = await _context.Constants
                .AsNoTracking()
                .Where(c => childIds.Contains(c.Id))
                .ToListAsync(cancellationToken);

            var embedding = children.Select(c => (float)c.Geom!.Coordinate.X).ToArray();
            return new TransformationResult
            {
                SourceNodeId = composition.Id,
                TargetRepresentation = "embedding",
                Data = embedding,
                Dimensions = embedding.Length
            };
        }

        // Composition with no constant children → 4D vector from centroid
        var coord = composition.Geom!.Coordinate;
        return new TransformationResult
        {
            SourceNodeId = composition.Id,
            TargetRepresentation = "embedding",
            Data = new float[] { (float)coord.X, (float)coord.Y, (float)coord.Z, (float)coord.M },
            Dimensions = 4
        };
    }

    private async Task<TransformationResult> TransformCompositionToTextAsync(Composition composition, CancellationToken cancellationToken)
    {
        // Get relations ordered by position
        var relations = await _context.Relations
            .Where(r => r.CompositionId == composition.Id)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);

        if (relations.Count == 0)
            throw new InvalidOperationException("Cannot transform to text: composition has no children");

        // Get all child constants that are Unicode (SeedType == 1)
        var constantIds = relations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();

        var children = await _context.Constants
            .AsNoTracking()
            .Where(c => constantIds.Contains(c.Id) && c.SeedType == 1)
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var text = new System.Text.StringBuilder();
        foreach (var relation in relations)
        {
            if (relation.ChildConstantId.HasValue && children.TryGetValue(relation.ChildConstantId.Value, out var child))
            {
                var ch = char.ConvertFromUtf32((int)child.SeedValue);
                for (int m = 0; m < relation.Multiplicity; m++)
                    text.Append(ch);
            }
        }

        if (text.Length == 0)
            throw new InvalidOperationException("Cannot transform to text: no Unicode constants found");

        return new TransformationResult
        {
            SourceNodeId = composition.Id,
            TargetRepresentation = "text",
            Data = text.ToString(),
            Dimensions = text.Length
        };
    }

    private static TransformationResult TransformCompositionToHilbert(Composition composition)
    {
        return new TransformationResult
        {
            SourceNodeId = composition.Id,
            TargetRepresentation = "hilbert",
            Data = new { high = composition.HilbertHigh, low = composition.HilbertLow },
            Dimensions = 2
        };
    }

    private static TransformationResult TransformCompositionToCoordinates(Composition composition)
    {
        var coord = composition.Geom!.Coordinate;
        return new TransformationResult
        {
            SourceNodeId = composition.Id,
            TargetRepresentation = "coordinates",
            Data = new { x = coord.X, y = coord.Y, z = coord.Z, m = coord.M },
            Dimensions = 4
        };
    }

    /// <summary>
    /// Transform trajectory: apply function to all points on a trajectory composition
    /// </summary>
    public async Task<long> TransformTrajectoryAsync(
        long trajectoryCompositionId,
        Func<Coordinate, Coordinate> transform,
        long? newTypeId,
        CancellationToken cancellationToken = default)
    {
        var trajectory = await _context.Compositions
            .FirstOrDefaultAsync(c => c.Id == trajectoryCompositionId, cancellationToken);

        if (trajectory == null)
            throw new InvalidOperationException($"Trajectory composition {trajectoryCompositionId} not found");

        // Get all points on trajectory
        var coords = trajectory.Geom!.Coordinates;
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
        var transformedComposition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = newGeom,
            ContentHash = NativeLibrary.ComputeCompositionHash(new[] { trajectoryCompositionId }, new[] { 1 }),
            TypeId = newTypeId
        };

        _context.Compositions.Add(transformedComposition);
        await _context.SaveChangesAsync(cancellationToken);

        // Create Relation for the reference to original trajectory
        var relation = new Relation
        {
            CompositionId = transformedComposition.Id,
            ChildCompositionId = trajectoryCompositionId,
            Position = 0,
            Multiplicity = 1
        };
        _context.Relations.Add(relation);
        await _context.SaveChangesAsync(cancellationToken);

        return transformedComposition.Id;
    }

    #endregion

    #region Generation Queries - Create new content from patterns

    /// <summary>
    /// Generate next likely constants based on spatial context.
    /// "Given this trajectory, what comes next?"
    /// </summary>
    public async Task<List<GenerationCandidate>> GenerateNextConstantAsync(
        long[] contextConstantIds,
        int numCandidates = 5,
        CancellationToken cancellationToken = default)
    {
        if (contextConstantIds.Length == 0)
            throw new ArgumentException("Context cannot be empty", nameof(contextConstantIds));

        var contextConstants = await _context.Constants
            .AsNoTracking()
            .Where(c => contextConstantIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        if (contextConstants.Count == 0)
            throw new InvalidOperationException("No context constants found");

        // Compute trajectory vector from context
        var contextCoords = contextConstants.Select(c => c.Geom!.Coordinate).ToList();
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

        // Find constants near predicted position
        var candidates = await _context.Constants
            .AsNoTracking()
            .Where(c => !contextConstantIds.Contains(c.Id))
            .OrderBy(c => c.Geom!.Distance(predictedGeom))
            .Take(numCandidates * 2) // Get more to filter
            .ToListAsync(cancellationToken);

        return candidates.Take(numCandidates).Select((c, i) => new GenerationCandidate
        {
            NodeId = c.Id,
            Probability = 1.0 / (1.0 + c.Geom!.Distance(predictedGeom)),
            Rank = i + 1,
            IsConstant = true,
            Distance = c.Geom!.Distance(predictedGeom)
        }).ToList();
    }

    /// <summary>
    /// Generate by analogy using constants: "A is to B as C is to ?"
    /// </summary>
    public async Task<List<GenerationCandidate>> GenerateByAnalogyAsync(
        long constantA,
        long constantB,
        long constantC,
        int numCandidates = 5,
        CancellationToken cancellationToken = default)
    {
        var constants = await _context.Constants
            .AsNoTracking()
            .Where(c => c.Id == constantA || c.Id == constantB || c.Id == constantC)
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (!constants.ContainsKey(constantA) || !constants.ContainsKey(constantB) || !constants.ContainsKey(constantC))
            throw new InvalidOperationException("One or more analogy constants not found");

        var coordA = constants[constantA].Geom!.Coordinate;
        var coordB = constants[constantB].Geom!.Coordinate;
        var coordC = constants[constantC].Geom!.Coordinate;

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

        // Find constants near predicted D position
        var candidates = await _context.Constants
            .AsNoTracking()
            .Where(c => c.Id != constantA && c.Id != constantB && c.Id != constantC)
            .OrderBy(c => c.Geom!.Distance(predictedGeom))
            .Take(numCandidates)
            .ToListAsync(cancellationToken);

        return candidates.Select((c, i) => new GenerationCandidate
        {
            NodeId = c.Id,
            Probability = 1.0 / (1.0 + c.Geom!.Distance(predictedGeom)),
            Rank = i + 1,
            IsConstant = true,
            Distance = c.Geom!.Distance(predictedGeom)
        }).ToList();
    }

    /// <summary>
    /// Generate composition: combine multiple constants into new structure
    /// </summary>
    public async Task<long> GenerateCompositionAsync(
        long[] componentConstantIds,
        long? compositionTypeId,
        CancellationToken cancellationToken = default)
    {
        if (componentConstantIds.Length == 0)
            throw new ArgumentException("Components cannot be empty", nameof(componentConstantIds));

        var components = await _context.Constants
            .AsNoTracking()
            .Where(c => componentConstantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (components.Count != componentConstantIds.Length)
            throw new InvalidOperationException("Some component constants not found");

        // Build geometry from component points
        var coords = componentConstantIds
            .Select(id => components[id].Geom!.Coordinate)
            .Select(c => new CoordinateZM(c.X, c.Y, c.Z, c.M))
            .ToArray();

        Geometry geom = coords.Length == 1
            ? _geometryFactory.CreatePoint(coords[0])
            : _geometryFactory.CreateLineString(coords);

        // Compute hash and Hilbert
        var multiplicities = Enumerable.Repeat(1, componentConstantIds.Length).ToArray();
        var contentHash = NativeLibrary.ComputeCompositionHash(componentConstantIds, multiplicities);

        // Check if already exists
        var existing = await _context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
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

        var composition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash,
            TypeId = compositionTypeId
        };

        _context.Compositions.Add(composition);
        await _context.SaveChangesAsync(cancellationToken);

        // Create Relation edges for the composition
        for (int i = 0; i < componentConstantIds.Length; i++)
        {
            var relation = new Relation
            {
                CompositionId = composition.Id,
                ChildConstantId = componentConstantIds[i],
                Position = i,
                Multiplicity = 1
            };
            _context.Relations.Add(relation);
        }
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
    public long KeyNodeId { get; set; }
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
    public long NodeId { get; set; }
    public double Confidence { get; set; }
    public string RelationType { get; set; } = "";
    public long? TypeId { get; set; }
    public bool IsConstant { get; set; }
}

public class GapInferenceResult
{
    public long GapStartNodeId { get; set; }
    public long GapEndNodeId { get; set; }
    public ulong GapSize { get; set; }
    public double PredictedX { get; set; }
    public double PredictedY { get; set; }
    public double PredictedZ { get; set; }
    public double PredictedM { get; set; }
    public double Confidence { get; set; }
}

public class InferenceChain
{
    public long RootNodeId { get; set; }
    public List<ChainNode> Nodes { get; set; } = new();
    public List<ChainEdge> Edges { get; set; } = new();
}

public class ChainNode
{
    public long NodeId { get; set; }
    public int Depth { get; set; }
    public long? TypeId { get; set; }
    public bool IsConstant { get; set; }
}

public class ChainEdge
{
    public long FromNodeId { get; set; }
    public long ToNodeId { get; set; }
    public int Depth { get; set; }
    public bool IsToConstant { get; set; }
}

public class TransformationResult
{
    public long SourceNodeId { get; set; }
    public string TargetRepresentation { get; set; } = "";
    public object? Data { get; set; }
    public int Dimensions { get; set; }
}

public class GenerationCandidate
{
    public long NodeId { get; set; }
    public double Probability { get; set; }
    public int Rank { get; set; }
    public long? TypeId { get; set; }
    public bool IsConstant { get; set; }
    public double Distance { get; set; }
}

#endregion
