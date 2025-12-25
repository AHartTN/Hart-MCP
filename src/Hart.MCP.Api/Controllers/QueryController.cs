using Hart.MCP.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

// Re-export types from Core for API responses
using CompositionStats = Hart.MCP.Core.Services.CompositionStats;
using NodeCounts = Hart.MCP.Core.Services.NodeCounts;

namespace Hart.MCP.Api.Controllers;

/// <summary>
/// Controller for spatial queries on the knowledge substrate
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[EnableRateLimiting("query")]
public class QueryController : ControllerBase
{
    private readonly SpatialQueryService _queryService;
    private readonly ILogger<QueryController> _logger;

    public QueryController(
        SpatialQueryService queryService,
        ILogger<QueryController> logger)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Find nearest neighbors to a Unicode codepoint
    /// </summary>
    [HttpGet("neighbors/{seed}")]
    [ProducesResponseType(typeof(NeighborsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindNeighbors(
        uint seed, 
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindNearestNeighborsAsync(seed, limit, cancellationToken);
            return Ok(new NeighborsResponse(
                seed,
                limit,
                results.Count,
                results.Select(c => new ConstantSummary(
                    c.Id,
                    c.HilbertHigh,
                    c.HilbertLow,
                    c.SeedValue,
                    c.SeedType,
                    c.Geom?.GeometryType ?? "None"
                )).ToList()
            ));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find neighbors for seed {Seed}", seed);
            return StatusCode(500, new ErrorResponse("An error occurred while finding neighbors"));
        }
    }

    /// <summary>
    /// Find constants within Hilbert range
    /// </summary>
    [HttpGet("hilbert-range")]
    [ProducesResponseType(typeof(HilbertRangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindInHilbertRange(
        [FromQuery] ulong startHigh,
        [FromQuery] ulong startLow,
        [FromQuery] ulong endHigh,
        [FromQuery] ulong endLow,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindConstantsInHilbertRangeAsync(
                startHigh, startLow, endHigh, endLow, limit, cancellationToken);

            return Ok(new HilbertRangeResponse(
                new HilbertRange(startHigh, startLow, endHigh, endLow),
                results.Count,
                results.Select(c => new ConstantSummary(
                    c.Id,
                    c.HilbertHigh,
                    c.HilbertLow,
                    c.SeedValue,
                    c.SeedType,
                    c.Geom?.GeometryType ?? "None"
                )).ToList()
            ));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Hilbert range");
            return StatusCode(500, new ErrorResponse("An error occurred while querying Hilbert range"));
        }
    }

    /// <summary>
    /// Find compositions that reference a specific composition (backlinks)
    /// </summary>
    [HttpGet("backlinks/{compositionId:long}")]
    [ProducesResponseType(typeof(BacklinksResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindBacklinks(
        long compositionId, 
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindCompositionsReferencingCompositionAsync(compositionId, limit, cancellationToken);
            // Get ref counts for each composition via Relations table
            var refCounts = await _queryService.Context.Relations
                .Where(r => results.Select(c => c.Id).Contains(r.CompositionId))
                .GroupBy(r => r.CompositionId)
                .Select(g => new { CompositionId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CompositionId, x => x.Count, cancellationToken);

            return Ok(new BacklinksResponse(
                compositionId,
                results.Count,
                results.Select(c => new CompositionSummary(
                    c.Id,
                    refCounts.GetValueOrDefault(c.Id, 0),
                    c.TypeId?.ToString()
                )).ToList()
            ));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find backlinks for composition {CompositionId}", compositionId);
            return StatusCode(500, new ErrorResponse("An error occurred while finding backlinks"));
        }
    }

    /// <summary>
    /// Find similar compositions
    /// </summary>
    [HttpGet("similar/{compositionId:long}")]
    [ProducesResponseType(typeof(SimilarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FindSimilar(
        long compositionId, 
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindSimilarCompositionsAsync(compositionId, limit, cancellationToken);
            // Get ref counts for each composition via Relations table
            var similarRefCounts = await _queryService.Context.Relations
                .Where(r => results.Select(c => c.Id).Contains(r.CompositionId))
                .GroupBy(r => r.CompositionId)
                .Select(g => new { CompositionId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CompositionId, x => x.Count, cancellationToken);

            return Ok(new SimilarResponse(
                compositionId,
                results.Count,
                results.Select(c => new CompositionSummary(
                    c.Id,
                    similarRefCounts.GetValueOrDefault(c.Id, 0),
                    c.TypeId?.ToString()
                )).ToList()
            ));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new ErrorResponse(ex.Message));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find similar compositions for {CompositionId}", compositionId);
            return StatusCode(500, new ErrorResponse("An error occurred while finding similar compositions"));
        }
    }

    /// <summary>
    /// Get composition statistics
    /// </summary>
    [HttpGet("stats/{compositionId:long}")]
    [ProducesResponseType(typeof(CompositionStats), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStats(
        long compositionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _queryService.GetCompositionStatsAsync(compositionId, cancellationToken);
            return Ok(stats);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get composition stats for {CompositionId}", compositionId);
            return StatusCode(500, new ErrorResponse("An error occurred while getting composition stats"));
        }
    }

    /// <summary>
    /// Compute difference between two compositions
    /// </summary>
    [HttpGet("diff")]
    [ProducesResponseType(typeof(DiffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ComputeDifference(
        [FromQuery] long compositionA,
        [FromQuery] long compositionB,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var diff = await _queryService.ComputeCompositionDifferenceAsync(
                compositionA, compositionB, cancellationToken);
            
            return Ok(new DiffResponse(
                compositionA,
                compositionB,
                diff.OnlyInA.Count,
                diff.OnlyInB.Count,
                diff.Shared.Count,
                diff.OnlyInA,
                diff.OnlyInB,
                diff.Shared
            ));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new ErrorResponse(ex.Message));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute difference between {A} and {B}", compositionA, compositionB);
            return StatusCode(500, new ErrorResponse("An error occurred while computing difference"));
        }
    }

    /// <summary>
    /// Get node counts
    /// </summary>
    [HttpGet("counts")]
    [ProducesResponseType(typeof(NodeCounts), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCounts(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await _queryService.GetNodeCountsAsync(cancellationToken);
            return Ok(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get node counts");
            return StatusCode(500, new ErrorResponse("An error occurred while getting node counts"));
        }
    }

    /// <summary>
    /// Search compositions by type ID
    /// </summary>
    [HttpGet("by-type/{typeId:long}")]
    [ProducesResponseType(typeof(TypeSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindByType(
        long typeId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindByTypeIdAsync(typeId, limit, cancellationToken);
            return Ok(new TypeSearchResponse(
                typeId.ToString(),
                results.Count,
                results.Select(c => new CompositionSummaryExtended(
                    c.Id,
                    c.HilbertHigh,
                    c.HilbertLow,
                    c.TypeId?.ToString(),
                    c.Geom?.GeometryType ?? "None"
                )).ToList()
            ));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search compositions by type {TypeId}", typeId);
            return StatusCode(500, new ErrorResponse("An error occurred while searching by type"));
        }
    }
}

// Response DTOs
public record ConstantSummary(long Id, ulong HilbertHigh, ulong HilbertLow, long SeedValue, int SeedType, string GeometryType);
public record CompositionSummary(long Id, int RefCount, string? TypeId);
public record CompositionSummaryExtended(long Id, ulong HilbertHigh, ulong HilbertLow, string? TypeId, string GeometryType);
public record HilbertRange(ulong StartHigh, ulong StartLow, ulong EndHigh, ulong EndLow);

public record NeighborsResponse(uint Seed, int Limit, int Count, List<ConstantSummary> Results);
public record HilbertRangeResponse(HilbertRange Range, int Count, List<ConstantSummary> Results);
public record BacklinksResponse(long CompositionId, int Count, List<CompositionSummary> Results);
public record SimilarResponse(long CompositionId, int Count, List<CompositionSummary> Results);
public record TypeSearchResponse(string TypeId, int Count, List<CompositionSummaryExtended> Results);
public record DiffResponse(
    long CompositionA, 
    long CompositionB, 
    int OnlyInACount, 
    int OnlyInBCount, 
    int SharedCount,
    List<long> OnlyInA,
    List<long> OnlyInB,
    List<long> Shared
);
