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
    /// Infer related compositions by spatial proximity.
    /// </summary>
    [HttpGet("inference/related/{compositionId}")]
    [ProducesResponseType(typeof(ApiResponse<List<InferenceResult>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InferRelatedConcepts(
        long compositionId,
        [FromQuery] double radius = 0.1,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await _aiQueryService.InferRelatedCompositionsAsync(
                compositionId, radius, limit, cancellationToken);

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
            _logger.LogError(ex, "Failed to infer related concepts for composition {CompositionId}", compositionId);
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Infer from gaps in Hilbert space (Mendeleev-style prediction).
    /// </summary>
    [HttpGet("inference/gaps")]
    [ProducesResponseType(typeof(ApiResponse<List<GapInferenceResult>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> InferFromGaps(
        [FromQuery] ulong hilbertHigh,
        [FromQuery] ulong hilbertLow,
        [FromQuery] ulong range = 1000,
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
    /// Chain inference: traverse relations to discover implied relationships.
    /// </summary>
    [HttpGet("inference/chain/{compositionId}")]
    [ProducesResponseType(typeof(ApiResponse<InferenceChain>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> InferChain(
        long compositionId,
        [FromQuery] int maxDepth = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _aiQueryService.InferChainAsync(
                compositionId, maxDepth, cancellationToken);

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
            _logger.LogError(ex, "Failed to infer chain for composition {CompositionId}", compositionId);
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    #endregion

    #region Transformation Endpoints

    /// <summary>
    /// Transform composition to a different representation.
    /// </summary>
    [HttpGet("transform/{compositionId}")]
    [ProducesResponseType(typeof(ApiResponse<TransformationResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Transform(
        long compositionId,
        [FromQuery] string target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _aiQueryService.TransformCompositionAsync(
                compositionId, target, cancellationToken);

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
            _logger.LogError(ex, "Failed to transform composition {CompositionId} to {Target}", compositionId, target);
            return BadRequest(new ApiResponse<object> { Success = false, Error = ex.Message });
        }
    }

    #endregion

    #region Generation Endpoints

    /// <summary>
    /// Generate next likely constants given a context.
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
            var results = await _aiQueryService.GenerateNextConstantAsync(
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
            // Parse compositionType as long if possible, otherwise pass null
            long? typeRef = long.TryParse(request.CompositionType, out var parsed) ? parsed : null;

            var compositionId = await _aiQueryService.GenerateCompositionAsync(
                request.ComponentAtomIds,
                typeRef,
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
