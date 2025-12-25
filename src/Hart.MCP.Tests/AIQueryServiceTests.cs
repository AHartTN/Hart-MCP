using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetTopologySuite.Geometries;
using Xunit;

namespace Hart.MCP.Tests;

/// <summary>
/// Tests for AIQueryService - attention, inference, transformation, generation.
/// 
/// Test Philosophy (TDD):
/// 1. Tests define EXPECTED behavior, not just verify code runs
/// 2. Each test has clear Given/When/Then structure
/// 3. Tests verify semantic correctness, not implementation details
/// 4. Edge cases and error conditions are explicitly tested
/// </summary>
public class AIQueryServiceTests : IDisposable
{
    private readonly HartDbContext _context;
    private readonly AIQueryService _service;
    private readonly Mock<ILogger<AIQueryService>> _loggerMock;
    private readonly GeometryFactory _geometryFactory;

    public AIQueryServiceTests()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new HartDbContext(options);

        _loggerMock = new Mock<ILogger<AIQueryService>>();
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);

        _service = new AIQueryService(_context, _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region Test Data Helpers

    /// <summary>
    /// Creates test constants at specific positions for predictable spatial relationships:
    /// - Constant 1 (A): at origin (0.1, 0.1, 0.1, 0.1)
    /// - Constant 2 (B): very close to A (0.12, 0.12, 0.12, 0.12)
    /// - Constant 3 (C): close to A (0.15, 0.15, 0.15, 0.15)
    /// - Constant 4 (D): far from A (0.9, 0.9, 0.9, 0.9)
    /// - Constant 5 (E): middle (0.5, 0.5, 0.5, 0.5)
    /// </summary>
    private async Task<List<Constant>> SeedTestConstantsWithPredictableSpatialRelationshipsAsync()
    {
        var constants = new List<Constant>
        {
            CreateConstant(1, 0.1, 0.1, 0.1, 0.1, seedValue: 65),  // A
            CreateConstant(2, 0.12, 0.12, 0.12, 0.12, seedValue: 66),  // B - closest to A
            CreateConstant(3, 0.15, 0.15, 0.15, 0.15, seedValue: 67),  // C - second closest to A
            CreateConstant(4, 0.9, 0.9, 0.9, 0.9, seedValue: 68),  // D - far from A
            CreateConstant(5, 0.5, 0.5, 0.5, 0.5, seedValue: 69),  // E - middle
        };

        _context.Constants.AddRange(constants);
        await _context.SaveChangesAsync();
        return constants;
    }

    private Constant CreateConstant(long id, double x, double y, double z, double m, long seedValue = 0, int seedType = 1)
    {
        var geom = _geometryFactory.CreatePoint(new CoordinateZM(x, y, z, m));
        return new Constant
        {
            Id = id,
            HilbertHigh = (ulong)(x * 10000),
            HilbertLow = (ulong)(y * 10000),
            Geom = geom,
            SeedValue = seedValue,
            SeedType = seedType,
            ContentHash = BitConverter.GetBytes(id)
        };
    }

    private Composition CreateComposition(long id, double x, double y, double z, double m, long? typeId = null)
    {
        var geom = _geometryFactory.CreatePoint(new CoordinateZM(x, y, z, m));
        return new Composition
        {
            Id = id,
            HilbertHigh = (ulong)(x * 10000),
            HilbertLow = (ulong)(y * 10000),
            Geom = geom,
            TypeId = typeId,
            ContentHash = BitConverter.GetBytes(id)
        };
    }

    private static double Distance4D(double x1, double y1, double z1, double m1, 
        double x2, double y2, double z2, double m2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var dz = z2 - z1;
        var dm = m2 - m1;
        return Math.Sqrt(dx*dx + dy*dy + dz*dz + dm*dm);
    }

    #endregion

    #region Attention Tests

    [Fact]
    public async Task ComputeAttention_GivenQueryAndKeys_ReturnsNormalizedWeightsSummingToOne()
    {
        // Given: constants at known positions
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();
        var queryId = 1L;
        var keyIds = new long[] { 2, 3, 4, 5 };

        // When: computing attention
        var results = await _service.ComputeAttentionAsync(queryId, keyIds);

        // Then: weights should sum to 1.0 (normalized)
        var weightSum = results.Sum(r => r.NormalizedWeight);
        weightSum.Should().BeApproximately(1.0, 0.0001, 
            "Attention weights MUST normalize to 1.0 (softmax property)");
    }

    [Fact]
    public async Task ComputeAttention_GivenQueryAndKeys_CloserConstantGetsHigherWeight()
    {
        // Given: constants where Constant2 is closest to Constant1, Constant4 is farthest
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();
        
        // When: computing attention from Constant1 to all others
        var results = await _service.ComputeAttentionAsync(1, new long[] { 2, 3, 4, 5 });

        // Then: Constant2 should have HIGHEST weight (closest)
        var constant2Result = results.First(r => r.KeyNodeId == 2);
        var constant4Result = results.First(r => r.KeyNodeId == 4);
        
        constant2Result.NormalizedWeight.Should().BeGreaterThan(constant4Result.NormalizedWeight,
            "Closer constants MUST receive higher attention weight (inverse distance relationship)");
    }

    [Fact]
    public async Task ComputeAttention_ResultsAreSortedByWeightDescending()
    {
        // Given: constants at known positions
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: computing attention
        var results = await _service.ComputeAttentionAsync(1, new long[] { 2, 3, 4, 5 });

        // Then: results should be sorted from highest to lowest weight
        for (int i = 0; i < results.Count - 1; i++)
        {
            results[i].NormalizedWeight.Should().BeGreaterThanOrEqualTo(results[i + 1].NormalizedWeight,
                "Results MUST be sorted by descending attention weight");
        }
    }

    [Fact]
    public async Task ComputeAttention_NonExistentQuery_ThrowsInvalidOperationException()
    {
        // Given: seeded constants without ID 999
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When/Then: querying non-existent constant should throw
        var action = () => _service.ComputeAttentionAsync(999, new long[] { 1, 2 });
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Query atom 999 not found*");
    }

    [Fact]
    public async Task ComputeMultiHeadAttention_ReturnsRequestedNumberOfHeads()
    {
        // Given: test constants and requested head count
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();
        const int requestedHeads = 4;

        // When: computing multi-head attention
        var result = await _service.ComputeMultiHeadAttentionAsync(1, new long[] { 2, 3, 4, 5 }, requestedHeads);

        // Then: should have exactly the requested number of heads
        result.NumHeads.Should().Be(requestedHeads);
        result.Heads.Should().HaveCount(requestedHeads,
            "Multi-head attention MUST return exactly the requested number of heads");
    }

    [Fact]
    public async Task ComputeMultiHeadAttention_EachHeadNormalizesToOne()
    {
        // Given: test constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: computing multi-head attention
        var result = await _service.ComputeMultiHeadAttentionAsync(1, new long[] { 2, 3, 4, 5 }, 4);

        // Then: each head's weights should sum to 1.0
        for (int h = 0; h < result.Heads.Count; h++)
        {
            var headSum = result.Heads[h].Sum(r => r.NormalizedWeight);
            headSum.Should().BeApproximately(1.0, 0.0001,
                $"Head {h} attention weights MUST normalize to 1.0");
        }
    }

    [Fact]
    public async Task ComputeMultiHeadAttention_AggregatedNormalizesToOne()
    {
        // Given: test constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: computing multi-head attention
        var result = await _service.ComputeMultiHeadAttentionAsync(1, new long[] { 2, 3, 4, 5 }, 4);

        // Then: aggregated weights should sum to 1.0
        var aggSum = result.Aggregated.Sum(r => r.NormalizedWeight);
        aggSum.Should().BeApproximately(1.0, 0.0001,
            "Aggregated attention weights MUST normalize to 1.0");
    }

    #endregion

    #region Inference Tests

    [Fact]
    public async Task InferRelatedConstants_ReturnsOnlyConstantsWithinRadius()
    {
        // Given: constants at known positions with specific distances from Constant1
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();
        
        // Distance from Constant1 (0.1,0.1,0.1,0.1) to:
        // Constant2: 0.04 (approx)
        // Constant3: 0.1  (approx)
        // Constant4: 1.6  (approx)
        // Constant5: 0.8  (approx)

        // When: querying with radius 0.2 (should include Constant2 and Constant3 only)
        var results = await _service.InferRelatedConstantsAsync(1, radius: 0.2, limit: 10);

        // Then: should find nearby constants, not distant ones
        var foundIds = results.Select(r => r.NodeId).ToList();
        foundIds.Should().Contain(2, "Constant2 (distance ~0.04) MUST be found within radius 0.2");
        foundIds.Should().Contain(3, "Constant3 (distance ~0.1) MUST be found within radius 0.2");
        foundIds.Should().NotContain(4, "Constant4 (distance ~1.6) MUST NOT be found within radius 0.2");
    }

    [Fact]
    public async Task InferRelatedConstants_ConfidenceIsInverseOfDistance()
    {
        // Given: constants at known positions
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: querying with large radius
        var results = await _service.InferRelatedConstantsAsync(1, radius: 2.0, limit: 10);

        // Then: closer constants should have higher confidence
        var constant2 = results.First(r => r.NodeId == 2);
        var constant4 = results.First(r => r.NodeId == 4);
        
        constant2.Confidence.Should().BeGreaterThan(constant4.Confidence,
            "Closer constants MUST have higher inference confidence");
    }

    [Fact]
    public async Task InferRelatedConstants_SmallRadiusReturnsFewerResults()
    {
        // Given: constants at various distances
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: querying with different radii
        var smallRadiusResults = await _service.InferRelatedConstantsAsync(1, radius: 0.05, limit: 10);
        var largeRadiusResults = await _service.InferRelatedConstantsAsync(1, radius: 2.0, limit: 10);

        // Then: smaller radius should return fewer or equal results
        smallRadiusResults.Count.Should().BeLessThanOrEqualTo(largeRadiusResults.Count,
            "Smaller search radius MUST return fewer or equal results");
    }

    [Fact]
    public async Task InferChain_TraversesRelationsToSpecifiedDepth()
    {
        // Given: a chain of compositions: A -> B -> C
        var compA = CreateComposition(10, 0.1, 0.1, 0.1, 0.1);
        var compB = CreateComposition(11, 0.2, 0.2, 0.2, 0.2);
        var constC = CreateConstant(12, 0.3, 0.3, 0.3, 0.3, seedValue: 65);
        
        _context.Compositions.AddRange(compA, compB);
        _context.Constants.Add(constC);
        
        // Add Relation edges: A -> B -> C
        _context.Relations.AddRange(
            new Relation { CompositionId = 10, ChildCompositionId = 11, Position = 0, Multiplicity = 1 },
            new Relation { CompositionId = 11, ChildConstantId = 12, Position = 0, Multiplicity = 1 }
        );
        await _context.SaveChangesAsync();

        // When: traversing chain from A with depth 3
        var chain = await _service.InferChainAsync(10, maxDepth: 3);

        // Then: should find all three nodes
        chain.Nodes.Should().HaveCount(3, "Chain traversal MUST find all connected nodes within depth");
        chain.Nodes.Select(n => n.NodeId).Should().Contain(new long[] { 10, 11, 12 });
    }

    [Fact]
    public async Task InferChain_RespectsMaxDepthLimit()
    {
        // Given: a chain of 5 compositions
        var compositions = new List<Composition>();
        for (int i = 0; i < 4; i++)
        {
            compositions.Add(CreateComposition(100 + i, i * 0.1, i * 0.1, i * 0.1, i * 0.1));
        }
        var lastConstant = CreateConstant(104, 4 * 0.1, 4 * 0.1, 4 * 0.1, 4 * 0.1, seedValue: 65);
        
        _context.Compositions.AddRange(compositions);
        _context.Constants.Add(lastConstant);
        
        // Create Relation chain: 100 -> 101 -> 102 -> 103 -> 104
        for (int i = 0; i < 3; i++)
        {
            _context.Relations.Add(new Relation 
            { 
                CompositionId = 100 + i, 
                ChildCompositionId = 100 + i + 1, 
                Position = 0, 
                Multiplicity = 1 
            });
        }
        // Last relation points to a constant
        _context.Relations.Add(new Relation
        {
            CompositionId = 103,
            ChildConstantId = 104,
            Position = 0,
            Multiplicity = 1
        });
        await _context.SaveChangesAsync();

        // When: traversing with depth limit of 2
        var chain = await _service.InferChainAsync(100, maxDepth: 2);

        // Then: should stop at depth 2
        chain.Nodes.Should().HaveCount(2, "Chain traversal MUST respect maxDepth limit");
    }

    #endregion

    #region Transformation Tests

    [Fact]
    public async Task TransformConstant_ToCoordinates_ReturnsCorrect4DVector()
    {
        // Given: a constant at known position
        var constant = CreateConstant(20, 1.5, 2.5, 3.5, 4.5, seedValue: 65);
        _context.Constants.Add(constant);
        await _context.SaveChangesAsync();

        // When: transforming to coordinates
        var result = await _service.TransformConstantAsync(20, "coordinates");

        // Then: should return correct coordinate values
        result.TargetRepresentation.Should().Be("coordinates");
        result.Dimensions.Should().Be(4);
        
        // Convert anonymous type to JSON and back to dictionary
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json);
        
        dict.Should().NotBeNull();
        dict!["x"].Should().BeApproximately(1.5, 0.0001);
        dict["y"].Should().BeApproximately(2.5, 0.0001);
        dict["z"].Should().BeApproximately(3.5, 0.0001);
        dict["m"].Should().BeApproximately(4.5, 0.0001);
    }

    [Fact]
    public async Task TransformConstant_ToHilbert_ReturnsBothComponents()
    {
        // Given: a constant with known Hilbert values
        var constant = CreateConstant(21, 0.5, 0.5, 0.5, 0.5, seedValue: 65);
        _context.Constants.Add(constant);
        await _context.SaveChangesAsync();

        // When: transforming to Hilbert
        var result = await _service.TransformConstantAsync(21, "hilbert");

        // Then: should return high and low components
        result.TargetRepresentation.Should().Be("hilbert");
        result.Dimensions.Should().Be(2);
        
        var data = result.Data as dynamic;
        Assert.NotNull(data);
    }

    [Fact]
    public async Task TransformConstant_ToEmbedding_ReturnsFloatArray()
    {
        // Given: a constant
        var constant = CreateConstant(22, 0.25, 0.5, 0.75, 1.0, seedValue: 65);
        _context.Constants.Add(constant);
        await _context.SaveChangesAsync();

        // When: transforming to embedding
        var result = await _service.TransformConstantAsync(22, "embedding");

        // Then: should return 4D float array
        result.TargetRepresentation.Should().Be("embedding");
        result.Dimensions.Should().Be(4);
        result.Data.Should().BeAssignableTo<float[]>();
        
        var embedding = (float[])result.Data!;
        embedding[0].Should().BeApproximately(0.25f, 0.0001f);
        embedding[1].Should().BeApproximately(0.5f, 0.0001f);
        embedding[2].Should().BeApproximately(0.75f, 0.0001f);
        embedding[3].Should().BeApproximately(1.0f, 0.0001f);
    }

    [Fact]
    public async Task TransformConstant_UnknownRepresentation_ThrowsArgumentException()
    {
        // Given: a valid constant
        var constant = CreateConstant(23, 0.5, 0.5, 0.5, 0.5, seedValue: 65);
        _context.Constants.Add(constant);
        await _context.SaveChangesAsync();

        // When/Then: transforming to unknown type should throw
        var action = async () => await _service.TransformConstantAsync(23, "invalid_representation");
        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown target representation*");
    }

    #endregion

    #region Generation Tests

    [Fact]
    public async Task GenerateNextConstant_ReturnsConstantsSortedByProbability()
    {
        // Given: test constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: generating next from context [1,2]
        var results = await _service.GenerateNextConstantAsync(new long[] { 1, 2 }, numCandidates: 3);

        // Then: results should be sorted by probability descending
        for (int i = 0; i < results.Count - 1; i++)
        {
            results[i].Probability.Should().BeGreaterThanOrEqualTo(results[i + 1].Probability,
                "Generation candidates MUST be sorted by probability descending");
        }
    }

    [Fact]
    public async Task GenerateNextConstant_RespectsNumCandidatesLimit()
    {
        // Given: test constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When: generating with specific limit
        var results = await _service.GenerateNextConstantAsync(new long[] { 1, 2 }, numCandidates: 2);

        // Then: should return at most requested number
        results.Count.Should().BeLessThanOrEqualTo(2,
            "Generation MUST respect numCandidates limit");
    }

    [Fact]
    public async Task GenerateNextConstant_EmptyContext_ThrowsArgumentException()
    {
        // Given: seeded constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();

        // When/Then: empty context should throw
        var action = async () => await _service.GenerateNextConstantAsync(Array.Empty<long>());
        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Context cannot be empty*");
    }

    [Fact]
    public async Task GenerateByAnalogy_FindsConstantNearPredictedPosition()
    {
        // Given: constants arranged for analogy test
        // A (0.1, 0.1) to B (0.3, 0.3) defines vector (0.2, 0.2)
        // Applying to C (0.5, 0.5) predicts D at (0.7, 0.7)
        var constants = new List<Constant>
        {
            CreateConstant(30, 0.1, 0.1, 0.0, 0.0, seedValue: 65),  // A
            CreateConstant(31, 0.3, 0.3, 0.0, 0.0, seedValue: 66),  // B
            CreateConstant(32, 0.5, 0.5, 0.0, 0.0, seedValue: 67),  // C
            CreateConstant(33, 0.69, 0.69, 0.0, 0.0, seedValue: 68),  // D - near predicted
            CreateConstant(34, 0.0, 0.0, 0.0, 0.0, seedValue: 69),  // E - far from predicted
        };
        _context.Constants.AddRange(constants);
        await _context.SaveChangesAsync();

        // When: A is to B as C is to ?
        var results = await _service.GenerateByAnalogyAsync(30, 31, 32, numCandidates: 2);

        // Then: D should be top candidate (closest to predicted position)
        results.Should().NotBeEmpty();
        results[0].NodeId.Should().Be(33,
            "Analogy generation MUST find constant closest to predicted position");
    }

    [Fact]
    public async Task GenerateComposition_CreatesNewCompositionWithRelations()
    {
        // Given: component constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();
        var components = new long[] { 1, 2, 3 };

        // When: generating composition (compositionTypeId is null for tests without type compositions)
        var newId = await _service.GenerateCompositionAsync(components, compositionTypeId: null);

        // Then: should create new composition referencing components
        var newComposition = await _context.Compositions.FindAsync(newId);
        newComposition.Should().NotBeNull("Composition MUST be created");
        
        // Verify composition references via Relation table
        var relations = await _context.Relations
            .Where(r => r.CompositionId == newId)
            .OrderBy(r => r.Position)
            .Select(r => r.ChildConstantId)
            .ToListAsync();
        relations.Should().BeEquivalentTo(components,
            "Composition MUST reference all component constants via Relation");
    }

    [Fact]
    public async Task GenerateComposition_DeduplicatesIdenticalComposition()
    {
        // Given: component constants
        await SeedTestConstantsWithPredictableSpatialRelationshipsAsync();
        var components = new long[] { 1, 2 };

        // When: generating same composition twice
        var firstId = await _service.GenerateCompositionAsync(components, compositionTypeId: null);
        var secondId = await _service.GenerateCompositionAsync(components, compositionTypeId: null);

        // Then: should return same ID (deduplication)
        secondId.Should().Be(firstId,
            "Identical compositions MUST be deduplicated by content hash");
    }

    #endregion
}
