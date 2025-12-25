using Hart.MCP.Core.Services;
using Hart.MCP.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Hart.MCP.Api.Controllers;

/// <summary>
/// Controller for AI/MLOps queries: attention, inference, transformation, generation
/// These operations treat the spatial substrate as a computational engine.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[EnableRateLimiting("query")]
public class AIQueryController : ControllerBase
{
    private readonly AIQueryService _aiQueryService;
    private readonly ILogger<AIQueryController> _logger;

    public AIQueryController(
        AIQueryService aiQueryService,
        ILogger<AIQueryController> logger)
    {
        _aiQueryService = aiQueryService ?? throw new ArgumentNullException(nameof(aiQueryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Attention Endpoints

    /// <summary>
    /// Compute attention weights between a query atom and key atoms.
    /// Uses spatial distance as inverse attention weight.
    /// </summary>
    [HttpPost("attention")]
    [ProducesResponseType(typeof(ApiResponse<List<AttentionResult>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ComputeAttention(
        [FromBody] AttentionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.ComputeAttentionAsync(
                request.QueryAtomId,
                request.KeyAtomIds,
                cancellationToken);

            return Ok(new ApiResponse<List<AttentionResult>>
            {
                Success = true,
                Data = results
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute attention");
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Compute multi-head attention from multiple dimensional perspectives.
    /// </summary>
    [HttpPost("attention/multi-head")]
    [ProducesResponseType(typeof(ApiResponse<MultiHeadAttentionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ComputeMultiHeadAttention(
        [FromBody] MultiHeadAttentionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.ComputeMultiHeadAttentionAsync(
                request.QueryAtomId,
                request.KeyAtomIds,
                request.NumHeads,
                cancellationToken);

            return Ok(new ApiResponse<MultiHeadAttentionResult>
            {
                Success = true,
                Data = results
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute multi-head attention");
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    #endregion

    #region Inference Endpoints

    /// <summary>
    /// Infer related concepts by spatial proximity.
    /// </summary>
    [HttpGet("inference/related/{atomId}")]
    [ProducesResponseType(typeof(ApiResponse<List<InferenceResult>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InferRelatedConcepts(
        long atomId,
        [FromQuery] double radius = 0.1,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.InferRelatedConceptsAsync(
                atomId, radius, limit, cancellationToken);

            return Ok(new ApiResponse<List<InferenceResult>>
            {
                Success = true,
                Data = results
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to infer related concepts for atom {AtomId}", atomId);
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Infer from gaps in Hilbert space (Mendeleev-style prediction).
    /// </summary>
    [HttpGet("inference/gaps")]
    [ProducesResponseType(typeof(ApiResponse<List<GapInferenceResult>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> InferFromGaps(
        [FromQuery] long hilbertHigh,
        [FromQuery] long hilbertLow,
        [FromQuery] long range = 1000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.InferFromGapsAsync(
                hilbertHigh, hilbertLow, range, cancellationToken);

            return Ok(new ApiResponse<List<GapInferenceResult>>
            {
                Success = true,
                Data = results
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to infer from gaps");
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Chain inference: traverse refs to discover implied relationships.
    /// </summary>
    [HttpGet("inference/chain/{atomId}")]
    [ProducesResponseType(typeof(ApiResponse<InferenceChain>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InferChain(
        long atomId,
        [FromQuery] int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _aiQueryService.InferChainAsync(
                atomId, maxDepth, cancellationToken);

            return Ok(new ApiResponse<InferenceChain>
            {
                Success = true,
                Data = result
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to infer chain for atom {AtomId}", atomId);
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    #endregion

    #region Transformation Endpoints

    /// <summary>
    /// Transform atom to a different representation.
    /// </summary>
    [HttpGet("transform/{atomId}")]
    [ProducesResponseType(typeof(ApiResponse<TransformationResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Transform(
        long atomId,
        [FromQuery] string target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _aiQueryService.TransformAsync(
                atomId, target, cancellationToken);

            return Ok(new ApiResponse<TransformationResult>
            {
                Success = true,
                Data = result
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transform atom {AtomId} to {Target}", atomId, target);
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    #endregion

    #region Generation Endpoints

    /// <summary>
    /// Generate next likely atoms given a context.
    /// </summary>
    [HttpPost("generate/next")]
    [ProducesResponseType(typeof(ApiResponse<List<GenerationCandidate>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateNext(
        [FromBody] GenerateNextRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.GenerateNextAsync(
                request.ContextAtomIds,
                request.NumCandidates,
                cancellationToken);

            return Ok(new ApiResponse<List<GenerationCandidate>>
            {
                Success = true,
                Data = results
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate next atoms");
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Generate by analogy: A is to B as C is to ?
    /// </summary>
    [HttpPost("generate/analogy")]
    [ProducesResponseType(typeof(ApiResponse<List<GenerationCandidate>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateByAnalogy(
        [FromBody] AnalogyRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.GenerateByAnalogyAsync(
                request.AtomA,
                request.AtomB,
                request.AtomC,
                request.NumCandidates,
                cancellationToken);

            return Ok(new ApiResponse<List<GenerationCandidate>>
            {
                Success = true,
                Data = results
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate by analogy");
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Generate a new composition from component atoms.
    /// </summary>
    [HttpPost("generate/composition")]
    [ProducesResponseType(typeof(ApiResponse<GeneratedCompositionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateComposition(
        [FromBody] GenerateCompositionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var compositionId = await _aiQueryService.GenerateCompositionAsync(
                request.ComponentAtomIds,
                request.CompositionType,
                cancellationToken);

            return Ok(new ApiResponse<GeneratedCompositionResult>
            {
                Success = true,
                Data = new GeneratedCompositionResult
                {
                    CompositionId = compositionId,
                    ComponentCount = request.ComponentAtomIds.Length,
                    CompositionType = request.CompositionType
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate composition");
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    #endregion
}

#region Request/Response DTOs

public record AttentionRequest(long QueryAtomId, long[] KeyAtomIds);

public record MultiHeadAttentionRequest(long QueryAtomId, long[] KeyAtomIds, int NumHeads = 4);

public record GenerateNextRequest(long[] ContextAtomIds, int NumCandidates = 5);

public record AnalogyRequest(long AtomA, long AtomB, long AtomC, int NumCandidates = 5);

public record GenerateCompositionRequest(long[] ComponentAtomIds, string CompositionType);

public class GeneratedCompositionResult
{
    public long CompositionId { get; set; }
    public int ComponentCount { get; set; }
    public string CompositionType { get; set; } = "";
}

#endregion
