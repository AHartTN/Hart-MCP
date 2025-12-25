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
    /// Creates test atoms at specific positions for predictable spatial relationships:
    /// - Atom 1 (A): at origin (0.1, 0.1, 0.1, 0.1)
    /// - Atom 2 (B): very close to A (0.12, 0.12, 0.12, 0.12)
    /// - Atom 3 (C): close to A (0.15, 0.15, 0.15, 0.15)
    /// - Atom 4 (D): far from A (0.9, 0.9, 0.9, 0.9)
    /// - Atom 5 (E): middle (0.5, 0.5, 0.5, 0.5)
    /// </summary>
    private async Task<List<Atom>> SeedTestAtomsWithPredictableSpatialRelationshipsAsync()
    {
        var atoms = new List<Atom>
        {
            CreateAtom(1, 0.1, 0.1, 0.1, 0.1, isConstant: true, atomType: "char", seedValue: 65),  // A
            CreateAtom(2, 0.12, 0.12, 0.12, 0.12, isConstant: true, atomType: "char", seedValue: 66),  // B - closest to A
            CreateAtom(3, 0.15, 0.15, 0.15, 0.15, isConstant: true, atomType: "char", seedValue: 67),  // C - second closest to A
            CreateAtom(4, 0.9, 0.9, 0.9, 0.9, isConstant: true, atomType: "char", seedValue: 68),  // D - far from A
            CreateAtom(5, 0.5, 0.5, 0.5, 0.5, isConstant: true, atomType: "char", seedValue: 69),  // E - middle
        };

        _context.Atoms.AddRange(atoms);
        await _context.SaveChangesAsync();
        return atoms;
    }

    private Atom CreateAtom(long id, double x, double y, double z, double m,
        bool isConstant, string atomType, uint? seedValue = null, 
        long[]? refs = null, int[]? multiplicities = null)
    {
        var geom = _geometryFactory.CreatePoint(new CoordinateZM(x, y, z, m));
        return new Atom
        {
            Id = id,
            HilbertHigh = (long)(x * 10000),
            HilbertLow = (long)(y * 10000),
            Geom = geom,
            IsConstant = isConstant,
            AtomType = atomType,
            SeedValue = seedValue,
            SeedType = isConstant ? 0 : null,
            Refs = isConstant ? null : refs,
            Multiplicities = isConstant ? null : multiplicities,
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
        // Given: atoms at known positions
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();
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
    public async Task ComputeAttention_GivenQueryAndKeys_CloserAtomGetsHigherWeight()
    {
        // Given: atoms where Atom2 is closest to Atom1, Atom4 is farthest
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();
        
        // When: computing attention from Atom1 to all others
        var results = await _service.ComputeAttentionAsync(1, new long[] { 2, 3, 4, 5 });

        // Then: Atom2 should have HIGHEST weight (closest)
        var atom2Result = results.First(r => r.KeyAtomId == 2);
        var atom4Result = results.First(r => r.KeyAtomId == 4);
        
        atom2Result.NormalizedWeight.Should().BeGreaterThan(atom4Result.NormalizedWeight,
            "Closer atoms MUST receive higher attention weight (inverse distance relationship)");
    }

    [Fact]
    public async Task ComputeAttention_ResultsAreSortedByWeightDescending()
    {
        // Given: atoms at known positions
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

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
        // Given: seeded atoms without ID 999
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

        // When/Then: querying non-existent atom should throw
        var action = () => _service.ComputeAttentionAsync(999, new long[] { 1, 2 });
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Query atom 999 not found*");
    }

    [Fact]
    public async Task ComputeMultiHeadAttention_ReturnsRequestedNumberOfHeads()
    {
        // Given: test atoms and requested head count
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();
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
        // Given: test atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

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
        // Given: test atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

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
    public async Task InferRelatedConcepts_ReturnsOnlyAtomsWithinRadius()
    {
        // Given: atoms at known positions with specific distances from Atom1
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();
        
        // Distance from Atom1 (0.1,0.1,0.1,0.1) to:
        // Atom2: 0.04 (approx)
        // Atom3: 0.1  (approx)
        // Atom4: 1.6  (approx)
        // Atom5: 0.8  (approx)

        // When: querying with radius 0.2 (should include Atom2 and Atom3 only)
        var results = await _service.InferRelatedConceptsAsync(1, radius: 0.2, limit: 10);

        // Then: should find nearby atoms, not distant ones
        var foundIds = results.Select(r => r.AtomId).ToList();
        foundIds.Should().Contain(2, "Atom2 (distance ~0.04) MUST be found within radius 0.2");
        foundIds.Should().Contain(3, "Atom3 (distance ~0.1) MUST be found within radius 0.2");
        foundIds.Should().NotContain(4, "Atom4 (distance ~1.6) MUST NOT be found within radius 0.2");
    }

    [Fact]
    public async Task InferRelatedConcepts_ConfidenceIsInverseOfDistance()
    {
        // Given: atoms at known positions
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

        // When: querying with large radius
        var results = await _service.InferRelatedConceptsAsync(1, radius: 2.0, limit: 10);

        // Then: closer atoms should have higher confidence
        var atom2 = results.First(r => r.AtomId == 2);
        var atom4 = results.First(r => r.AtomId == 4);
        
        atom2.Confidence.Should().BeGreaterThan(atom4.Confidence,
            "Closer atoms MUST have higher inference confidence");
    }

    [Fact]
    public async Task InferRelatedConcepts_SmallRadiusReturnsFewerResults()
    {
        // Given: atoms at various distances
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

        // When: querying with different radii
        var smallRadiusResults = await _service.InferRelatedConceptsAsync(1, radius: 0.05, limit: 10);
        var largeRadiusResults = await _service.InferRelatedConceptsAsync(1, radius: 2.0, limit: 10);

        // Then: smaller radius should return fewer or equal results
        smallRadiusResults.Count.Should().BeLessThanOrEqualTo(largeRadiusResults.Count,
            "Smaller search radius MUST return fewer or equal results");
    }

    [Fact]
    public async Task InferChain_TraversesRefsToSpecifiedDepth()
    {
        // Given: a chain of compositions: A -> B -> C
        var atomA = CreateAtom(10, 0.1, 0.1, 0.1, 0.1, false, "composition", refs: new[] { 11L }, multiplicities: new[] { 1 });
        var atomB = CreateAtom(11, 0.2, 0.2, 0.2, 0.2, false, "composition", refs: new[] { 12L }, multiplicities: new[] { 1 });
        var atomC = CreateAtom(12, 0.3, 0.3, 0.3, 0.3, true, "constant", seedValue: 65);
        
        _context.Atoms.AddRange(atomA, atomB, atomC);
        await _context.SaveChangesAsync();

        // When: traversing chain from A with depth 3
        var chain = await _service.InferChainAsync(10, maxDepth: 3);

        // Then: should find all three atoms
        chain.Nodes.Should().HaveCount(3, "Chain traversal MUST find all connected atoms within depth");
        chain.Nodes.Select(n => n.AtomId).Should().Contain(new long[] { 10, 11, 12 });
    }

    [Fact]
    public async Task InferChain_RespectsMaxDepthLimit()
    {
        // Given: a chain of 5 compositions
        var atoms = new List<Atom>();
        for (int i = 0; i < 5; i++)
        {
            var refs = i < 4 ? new[] { (long)(100 + i + 1) } : null;
            var mults = refs != null ? new[] { 1 } : null;
            atoms.Add(CreateAtom(100 + i, i * 0.1, i * 0.1, i * 0.1, i * 0.1, 
                i == 4, i == 4 ? "constant" : "composition", 
                refs: refs, multiplicities: mults, seedValue: i == 4 ? 65u : null));
        }
        _context.Atoms.AddRange(atoms);
        await _context.SaveChangesAsync();

        // When: traversing with depth limit of 2
        var chain = await _service.InferChainAsync(100, maxDepth: 2);

        // Then: should stop at depth 2
        chain.Nodes.Should().HaveCount(2, "Chain traversal MUST respect maxDepth limit");
    }

    #endregion

    #region Transformation Tests

    [Fact]
    public async Task Transform_ToCoordinates_ReturnsCorrect4DVector()
    {
        // Given: an atom at known position
        var atom = CreateAtom(20, 1.5, 2.5, 3.5, 4.5, true, "constant", 65);
        _context.Atoms.Add(atom);
        await _context.SaveChangesAsync();

        // When: transforming to coordinates
        var result = await _service.TransformAsync(20, "coordinates");

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
    public async Task Transform_ToHilbert_ReturnsBothComponents()
    {
        // Given: an atom with known Hilbert values
        var atom = CreateAtom(21, 0.5, 0.5, 0.5, 0.5, true, "constant", 65);
        _context.Atoms.Add(atom);
        await _context.SaveChangesAsync();

        // When: transforming to Hilbert
        var result = await _service.TransformAsync(21, "hilbert");

        // Then: should return high and low components
        result.TargetRepresentation.Should().Be("hilbert");
        result.Dimensions.Should().Be(2);
        
        var data = result.Data as dynamic;
        Assert.NotNull(data);
    }

    [Fact]
    public async Task Transform_ToEmbedding_ReturnsFloatArray()
    {
        // Given: a constant atom
        var atom = CreateAtom(22, 0.25, 0.5, 0.75, 1.0, true, "constant", 65);
        _context.Atoms.Add(atom);
        await _context.SaveChangesAsync();

        // When: transforming to embedding
        var result = await _service.TransformAsync(22, "embedding");

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
    public async Task Transform_UnknownRepresentation_ThrowsArgumentException()
    {
        // Given: a valid atom
        var atom = CreateAtom(23, 0.5, 0.5, 0.5, 0.5, true, "constant", 65);
        _context.Atoms.Add(atom);
        await _context.SaveChangesAsync();

        // When/Then: transforming to unknown type should throw
        var action = () => _service.TransformAsync(23, "invalid_representation");
        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown target representation*");
    }

    #endregion

    #region Generation Tests

    [Fact]
    public async Task GenerateNext_ReturnsAtomsSortedByProbability()
    {
        // Given: test atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

        // When: generating next from context [1,2]
        var results = await _service.GenerateNextAsync(new long[] { 1, 2 }, numCandidates: 3);

        // Then: results should be sorted by probability descending
        for (int i = 0; i < results.Count - 1; i++)
        {
            results[i].Probability.Should().BeGreaterThanOrEqualTo(results[i + 1].Probability,
                "Generation candidates MUST be sorted by probability descending");
        }
    }

    [Fact]
    public async Task GenerateNext_RespectsNumCandidatesLimit()
    {
        // Given: test atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

        // When: generating with specific limit
        var results = await _service.GenerateNextAsync(new long[] { 1, 2 }, numCandidates: 2);

        // Then: should return at most requested number
        results.Count.Should().BeLessThanOrEqualTo(2,
            "Generation MUST respect numCandidates limit");
    }

    [Fact]
    public async Task GenerateNext_EmptyContext_ThrowsArgumentException()
    {
        // Given: seeded atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();

        // When/Then: empty context should throw
        var action = () => _service.GenerateNextAsync(Array.Empty<long>());
        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Context cannot be empty*");
    }

    [Fact]
    public async Task GenerateByAnalogy_FindsAtomNearPredictedPosition()
    {
        // Given: atoms arranged for analogy test
        // A (0.1, 0.1) to B (0.3, 0.3) defines vector (0.2, 0.2)
        // Applying to C (0.5, 0.5) predicts D at (0.7, 0.7)
        var atoms = new List<Atom>
        {
            CreateAtom(30, 0.1, 0.1, 0.0, 0.0, true, "char", 65),  // A
            CreateAtom(31, 0.3, 0.3, 0.0, 0.0, true, "char", 66),  // B
            CreateAtom(32, 0.5, 0.5, 0.0, 0.0, true, "char", 67),  // C
            CreateAtom(33, 0.69, 0.69, 0.0, 0.0, true, "char", 68),  // D - near predicted
            CreateAtom(34, 0.0, 0.0, 0.0, 0.0, true, "char", 69),  // E - far from predicted
        };
        _context.Atoms.AddRange(atoms);
        await _context.SaveChangesAsync();

        // When: A is to B as C is to ?
        var results = await _service.GenerateByAnalogyAsync(30, 31, 32, numCandidates: 2);

        // Then: D should be top candidate (closest to predicted position)
        results.Should().NotBeEmpty();
        results[0].AtomId.Should().Be(33,
            "Analogy generation MUST find atom closest to predicted position");
    }

    [Fact]
    public async Task GenerateComposition_CreatesNewAtomWithRefs()
    {
        // Given: component atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();
        var components = new long[] { 1, 2, 3 };

        // When: generating composition
        var newId = await _service.GenerateCompositionAsync(components, "test_composition");

        // Then: should create new atom referencing components
        var newAtom = await _context.Atoms.FindAsync(newId);
        newAtom.Should().NotBeNull();
        newAtom!.IsConstant.Should().BeFalse("Compositions MUST NOT be constants");
        newAtom.Refs.Should().BeEquivalentTo(components,
            "Composition MUST reference all component atoms");
        newAtom.AtomType.Should().Be("test_composition");
    }

    [Fact]
    public async Task GenerateComposition_DeduplicatesIdenticalComposition()
    {
        // Given: component atoms
        await SeedTestAtomsWithPredictableSpatialRelationshipsAsync();
        var components = new long[] { 1, 2 };

        // When: generating same composition twice
        var firstId = await _service.GenerateCompositionAsync(components, "dedup_test");
        var secondId = await _service.GenerateCompositionAsync(components, "dedup_test");

        // Then: should return same ID (deduplication)
        secondId.Should().Be(firstId,
            "Identical compositions MUST be deduplicated by content hash");
    }

    #endregion
}
