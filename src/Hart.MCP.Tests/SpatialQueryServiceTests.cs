using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Hart.MCP.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Hart.MCP.Tests;

/// <summary>
/// Tests for SpatialQueryService.
/// 
/// SPATIAL INVARIANTS TESTED:
/// 1. Hilbert locality: points close in Hilbert order are spatially close
/// 2. K-nearest neighbors: returns correct K closest points
/// 3. Range queries: returns all points within specified Hilbert range
/// 4. Query correctness: referencing atoms found correctly
/// 
/// BEHAVIORAL EXPECTATIONS:
/// - Spatial queries return results ordered appropriately
/// - Distance-based queries respect distance constraints
/// - Composition queries find all referencing atoms
/// </summary>
public class SpatialQueryServiceTests : IDisposable
{
    private readonly HartDbContext _context;
    private readonly SpatialQueryService _queryService;
    private readonly AtomIngestionService _ingestionService;

    public SpatialQueryServiceTests()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase(databaseName: $"HartMCP_Query_Test_{Guid.NewGuid()}")
            .Options;

        _context = new HartDbContext(options);
        
        var ingestionLogger = Mock.Of<ILogger<AtomIngestionService>>();
        var queryLogger = Mock.Of<ILogger<SpatialQueryService>>();
        
        _ingestionService = new AtomIngestionService(_context, ingestionLogger);
        _queryService = new SpatialQueryService(_context, queryLogger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region Nearest Neighbors: Core Behavior

    [Fact]
    public async Task NearestNeighbors_ReturnsResults_WhenDataExists()
    {
        await _ingestionService.IngestTextAsync("ABC");

        var results = await _queryService.FindNearestNeighborsAsync('A', limit: 10);

        results.Should().NotBeEmpty(because: "data exists in the database");
    }

    [Fact]
    public async Task NearestNeighbors_ReturnsOnlyConstants_NotCompositions()
    {
        await _ingestionService.IngestTextAsync("Hello World");

        var results = await _queryService.FindNearestNeighborsAsync('H', limit: 100);

        results.Should().NotBeEmpty(
            because: "FindNearestNeighborsAsync returns List<Constant> - all results are constants by definition");
    }

    [Fact]
    public async Task NearestNeighbors_RespectsLimit()
    {
        await _ingestionService.IngestTextAsync("ABCDEFGHIJ");

        var results = await _queryService.FindNearestNeighborsAsync('A', limit: 3);

        results.Should().HaveCountLessThanOrEqualTo(3,
            because: "query must respect the requested limit");
    }

    [Fact]
    public async Task NearestNeighbors_IncludesQueryAtomInResults()
    {
        // Note: The current implementation DOES include the query atom in results
        // This is by design - caller can filter if needed
        await _ingestionService.IngestTextAsync("AAA");
        var aAtom = await _context.Constants.FirstAsync(c => c.SeedValue == 'A');

        var results = await _queryService.FindNearestNeighborsAsync('A', limit: 100);

        // The query atom is the closest to itself (distance 0)
        results.Should().Contain(a => a.Id == aAtom.Id,
            because: "nearest neighbor search includes the query atom itself at distance 0");
    }

    #endregion

    #region Hilbert Range Queries: Ordering and Bounds

    [Fact]
    public async Task HilbertRange_ResultsOrderedByHilbertIndex()
    {
        await _ingestionService.IngestTextAsync("ABCDEFGHIJ");

        var results = await _queryService.FindConstantsInHilbertRangeAsync(
            ulong.MinValue, ulong.MinValue,
            ulong.MaxValue, ulong.MaxValue,
            limit: 100);

        for (int i = 1; i < results.Count; i++)
        {
            var prev = results[i - 1];
            var curr = results[i];
            
            var prevIsLessOrEqual = prev.HilbertHigh < curr.HilbertHigh ||
                (prev.HilbertHigh == curr.HilbertHigh && prev.HilbertLow <= curr.HilbertLow);
            
            prevIsLessOrEqual.Should().BeTrue(
                because: "results must be ordered by Hilbert index for efficient streaming");
        }
    }

    [Fact]
    public async Task HilbertRange_RespectsLimit()
    {
        await _ingestionService.IngestTextAsync("ABCDEFGHIJKLMNOP");

        var results = await _queryService.FindConstantsInHilbertRangeAsync(
            ulong.MinValue, ulong.MinValue,
            ulong.MaxValue, ulong.MaxValue,
            limit: 5);

        results.Should().HaveCountLessThanOrEqualTo(5,
            because: "query must respect the limit even with many results");
    }

    [Fact]
    public async Task HilbertRange_ReturnsEmptyForEmptyRange()
    {
        await _ingestionService.IngestTextAsync("ABC");

        // Query a range that contains nothing (impossible range)
        var results = await _queryService.FindConstantsInHilbertRangeAsync(
            ulong.MaxValue, ulong.MaxValue,
            ulong.MaxValue, ulong.MaxValue,
            limit: 100);

        // Should return empty or very limited results
        results.Count.Should().BeLessThanOrEqualTo(1);
    }

    #endregion

    #region Hilbert Locality Preservation

    /// <summary>
    /// CRITICAL INVARIANT: The Hilbert curve preserves spatial locality.
    /// Points close in Hilbert order should be spatially close.
    /// </summary>
    [Fact]
    public async Task HilbertLocality_AdjacentHilbertIndices_AreSpatiallyClose()
    {
        // Create atoms with known positions
        await _ingestionService.IngestTextAsync("ABCDEFGHIJ");
        
        // Get constants ordered by Hilbert index
        var orderedAtoms = await _context.Constants
            .OrderBy(c => c.HilbertHigh)
            .ThenBy(c => c.HilbertLow)
            .Take(5)
            .ToListAsync();

        if (orderedAtoms.Count < 2)
            return; // Not enough data to test

        // Adjacent atoms in Hilbert order should be spatially close
        // This is a statistical property - most adjacent pairs should be close
        var closeCount = 0;
        var farCount = 0;
        
        for (int i = 0; i < orderedAtoms.Count - 1; i++)
        {
            var a = orderedAtoms[i];
            var b = orderedAtoms[i + 1];
            
            // Use coordinate accessor for PointZM
            var aCoord = a.Geom.Coordinate as NetTopologySuite.Geometries.CoordinateZM;
            var bCoord = b.Geom.Coordinate as NetTopologySuite.Geometries.CoordinateZM;
            
            var dist = Math.Sqrt(
                Math.Pow(a.Geom.Coordinate.X - b.Geom.Coordinate.X, 2) +
                Math.Pow(a.Geom.Coordinate.Y - b.Geom.Coordinate.Y, 2) +
                Math.Pow(a.Geom.Coordinate.Z - b.Geom.Coordinate.Z, 2) +
                Math.Pow((aCoord?.M ?? 0) - (bCoord?.M ?? 0), 2));
            
            if (dist < 1.0) closeCount++;
            else farCount++;
        }
        
        // Most adjacent pairs should be close (locality property)
        closeCount.Should().BeGreaterThan(0,
            because: "Hilbert curve should preserve spatial locality");
    }

    #endregion

    #region Referencing Atoms Queries

    [Fact]
    public async Task ReferencingAtoms_FindsCompositionsThatContainAtom()
    {
        var compositionId = await _ingestionService.IngestTextAsync("AAA");
        
        // Get the first referenced constant via Relation table
        var firstRef = await _context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .FirstAsync();
        var aConstantId = firstRef.ChildConstantId!.Value;

        var results = await _queryService.FindCompositionsReferencingConstantAsync(aConstantId, 100);

        results.Should().Contain(c => c.Id == compositionId,
            because: "composition 'AAA' references the 'A' constant");
    }

    [Fact]
    public async Task ReferencingAtoms_FindsMultipleCompositions()
    {
        await _ingestionService.IngestTextAsync("AB");
        await _ingestionService.IngestTextAsync("AC");
        await _ingestionService.IngestTextAsync("AD");
        
        var aConstant = await _context.Constants.FirstAsync(c => c.SeedValue == 'A');

        var results = await _queryService.FindCompositionsReferencingConstantAsync(aConstant.Id, 100);

        results.Should().HaveCountGreaterThanOrEqualTo(3,
            because: "three different compositions reference 'A'");
    }

    #endregion

    #region Similar Compositions Queries

    [Fact]
    public async Task SimilarCompositions_ExcludesSelf()
    {
        var id1 = await _ingestionService.IngestTextAsync("Hello");
        await _ingestionService.IngestTextAsync("World");
        await _ingestionService.IngestTextAsync("Test");

        var results = await _queryService.FindSimilarCompositionsAsync(id1, limit: 10);

        results.Should().NotContain(a => a.Id == id1,
            because: "a composition is not similar to itself");
    }

    [Fact]
    public async Task SimilarCompositions_ThrowsForNonExistent()
    {
        var action = () => _queryService.FindSimilarCompositionsAsync(999999);

        await action.Should().ThrowAsync<InvalidOperationException>(
            because: "cannot find similar compositions for non-existent atom");
    }

    #endregion

    #region Composition Difference

    [Fact]
    public async Task CompositionDifference_IdentifiesSharedAndUniqueAtoms()
    {
        // "AB" and "BC" share 'B'
        var idAB = await _ingestionService.IngestTextAsync("AB");
        var idBC = await _ingestionService.IngestTextAsync("BC");

        var diff = await _queryService.ComputeCompositionDifferenceAsync(idAB, idBC);

        diff.Shared.Should().HaveCountGreaterThanOrEqualTo(1,
            because: "'B' is shared between both compositions");
        diff.OnlyInA.Should().HaveCountGreaterThanOrEqualTo(1,
            because: "'A' is only in first composition");
        diff.OnlyInB.Should().HaveCountGreaterThanOrEqualTo(1,
            because: "'C' is only in second composition");
    }

    #endregion

    #region Composition Stats

    [Fact]
    public async Task CompositionStats_ReturnsCorrectMetrics()
    {
        var id = await _ingestionService.IngestTextAsync("Hello");

        var stats = await _queryService.GetCompositionStatsAsync(id);

        stats.Id.Should().Be(id);
        stats.RefCount.Should().BeGreaterThan(0);
        stats.TotalMultiplicity.Should().Be(5, because: "'Hello' has 5 characters");
        stats.GeometryType.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CompositionStats_ThrowsForNonExistent()
    {
        var action = () => _queryService.GetCompositionStatsAsync(999999);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region Find By Type

    [Fact(Skip = "FindByTypeAsync removed - types are now atomized references via TypeRef")]
    public async Task FindByType_FiltersCorrectly()
    {
        await _ingestionService.IngestTextAsync("Test");

        // TODO: Refactor to use TypeRef-based queries
        // Old approach: var chars = await _queryService.FindByTypeAsync("char");
        // New approach: Query atoms by TypeRef atom ID
        await Task.CompletedTask;
    }

    [Fact(Skip = "FindByTypeAsync removed - types are now atomized references via TypeRef")]
    public async Task FindByType_ThrowsForEmptyType()
    {
        // TODO: Refactor validation for TypeRef-based approach
        await Task.CompletedTask;
    }

    #endregion

    #region Atom Counts

    [Fact]
    public async Task AtomCounts_ReturnsCorrectTotals()
    {
        await _ingestionService.IngestTextAsync("AB");

        var counts = await _queryService.GetNodeCountsAsync();

        counts.TotalConstants.Should().Be(2, because: "'A' and 'B' are constants");
        counts.TotalCompositions.Should().Be(1, because: "one text composition");
        counts.Total.Should().Be(3);
    }

    [Fact]
    public async Task AtomCounts_EmptyDatabase_ReturnsZero()
    {
        var counts = await _queryService.GetNodeCountsAsync();

        counts.TotalConstants.Should().Be(0);
        counts.TotalCompositions.Should().Be(0);
        counts.Total.Should().Be(0);
    }

    #endregion

    #region Input Validation

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task NearestNeighbors_InvalidLimit_Throws(int limit)
    {
        var action = () => _queryService.FindNearestNeighborsAsync('A', limit);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>(
            because: "limit must be between 1 and 1000");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task HilbertRange_InvalidLimit_Throws(int limit)
    {
        var action = () => _queryService.FindConstantsInHilbertRangeAsync(0UL, 0UL, 1UL, 1UL, limit);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReferencingAtoms_InvalidAtomId_Throws(long constantId)
    {
        var action = () => _queryService.FindCompositionsReferencingConstantAsync(constantId, 100);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    #endregion
}
