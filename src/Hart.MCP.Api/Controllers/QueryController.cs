using Hart.MCP.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
                results.Select(a => new AtomSummary(
                    a.Id,
                    a.HilbertHigh,
                    a.HilbertLow,
                    a.IsConstant,
                    a.AtomType,
                    a.Geom.GeometryType
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
    /// Find atoms within Hilbert range
    /// </summary>
    [HttpGet("hilbert-range")]
    [ProducesResponseType(typeof(HilbertRangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindInHilbertRange(
        [FromQuery] long startHigh,
        [FromQuery] long startLow,
        [FromQuery] long endHigh,
        [FromQuery] long endLow,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindInHilbertRangeAsync(
                startHigh, startLow, endHigh, endLow, limit, cancellationToken);

            return Ok(new HilbertRangeResponse(
                new HilbertRange(startHigh, startLow, endHigh, endLow),
                results.Count,
                results.Select(a => new AtomSummary(
                    a.Id,
                    a.HilbertHigh,
                    a.HilbertLow,
                    a.IsConstant,
                    a.AtomType,
                    a.Geom.GeometryType
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
    /// Find atoms that reference a specific atom (backlinks)
    /// </summary>
    [HttpGet("backlinks/{atomId:long}")]
    [ProducesResponseType(typeof(BacklinksResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindBacklinks(
        long atomId, 
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindReferencingAtomsAsync(atomId, limit, cancellationToken);
            return Ok(new BacklinksResponse(
                atomId,
                results.Count,
                results.Select(a => new CompositionSummary(
                    a.Id,
                    a.Refs?.Length ?? 0,
                    a.AtomType
                )).ToList()
            ));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find backlinks for atom {AtomId}", atomId);
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
            return Ok(new SimilarResponse(
                compositionId,
                results.Count,
                results.Select(a => new CompositionSummary(
                    a.Id,
                    a.Refs?.Length ?? 0,
                    a.AtomType
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
    /// Get atom counts
    /// </summary>
    [HttpGet("counts")]
    [ProducesResponseType(typeof(AtomCounts), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCounts(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await _queryService.GetAtomCountsAsync(cancellationToken);
            return Ok(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get atom counts");
            return StatusCode(500, new ErrorResponse("An error occurred while getting atom counts"));
        }
    }

    /// <summary>
    /// Search atoms by type
    /// </summary>
    [HttpGet("by-type/{atomType}")]
    [ProducesResponseType(typeof(TypeSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindByType(
        string atomType,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _queryService.FindByTypeAsync(atomType, limit, cancellationToken);
            return Ok(new TypeSearchResponse(
                atomType,
                results.Count,
                results.Select(a => new AtomSummary(
                    a.Id,
                    a.HilbertHigh,
                    a.HilbertLow,
                    a.IsConstant,
                    a.AtomType,
                    a.Geom.GeometryType
                )).ToList()
            ));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search atoms by type {AtomType}", atomType);
            return StatusCode(500, new ErrorResponse("An error occurred while searching by type"));
        }
    }
}

// Response DTOs
public record AtomSummary(long Id, long HilbertHigh, long HilbertLow, bool IsConstant, string? AtomType, string GeometryType);
public record CompositionSummary(long Id, int RefCount, string? AtomType);
public record HilbertRange(long StartHigh, long StartLow, long EndHigh, long EndLow);

public record NeighborsResponse(uint Seed, int Limit, int Count, List<AtomSummary> Results);
public record HilbertRangeResponse(HilbertRange Range, int Count, List<AtomSummary> Results);
public record BacklinksResponse(long AtomId, int Count, List<CompositionSummary> Results);
public record SimilarResponse(long CompositionId, int Count, List<CompositionSummary> Results);
public record TypeSearchResponse(string AtomType, int Count, List<AtomSummary> Results);
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
