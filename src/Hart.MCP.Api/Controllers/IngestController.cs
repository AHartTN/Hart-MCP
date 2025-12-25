using Hart.MCP.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.ComponentModel.DataAnnotations;

namespace Hart.MCP.Api.Controllers;

/// <summary>
/// Controller for ingesting content into the spatial knowledge substrate
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[EnableRateLimiting("ingestion")]
public class IngestController : ControllerBase
{
    private readonly AtomIngestionService _ingestionService;
    private readonly ILogger<IngestController> _logger;

    public IngestController(
        AtomIngestionService ingestionService,
        ILogger<IngestController> logger)
    {
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ingest UTF-8 text into atoms
    /// </summary>
    /// <param name="request">The text to ingest</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The root composition atom ID</returns>
    /// <response code="200">Text successfully ingested</response>
    /// <response code="400">Invalid request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("text")]
    [ProducesResponseType(typeof(IngestTextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> IngestText(
        [FromBody] IngestTextRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ErrorResponse("Invalid request", ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .ToArray()));

        try
        {
            var rootAtomId = await _ingestionService.IngestTextAsync(request.Text, cancellationToken);
            
            _logger.LogInformation("Successfully ingested text, root atom ID: {AtomId}", rootAtomId);
            
            return Ok(new IngestTextResponse(rootAtomId, request.Text.Length, "success"));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid text ingestion request");
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Text ingestion cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest text");
            return StatusCode(500, new ErrorResponse("An error occurred while ingesting text", ex.Message));
        }
    }

    /// <summary>
    /// Reconstruct text from composition atom
    /// </summary>
    /// <param name="atomId">The composition atom ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The reconstructed text</returns>
    /// <response code="200">Text successfully reconstructed</response>
    /// <response code="404">Composition atom not found</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("text/{atomId:long}")]
    [ProducesResponseType(typeof(ReconstructTextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReconstructText(long atomId, CancellationToken cancellationToken)
    {
        if (atomId <= 0)
            return BadRequest(new ErrorResponse("Invalid atom ID"));

        try
        {
            var text = await _ingestionService.ReconstructTextAsync(atomId, cancellationToken);
            return Ok(new ReconstructTextResponse(atomId, text, text.Length, "success"));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            _logger.LogWarning("Composition atom {AtomId} not found", atomId);
            return NotFound(new ErrorResponse($"Composition atom {atomId} not found"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid composition atom {AtomId}", atomId);
            return BadRequest(new ErrorResponse(ex.Message));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Text reconstruction cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconstruct text from atom {AtomId}", atomId);
            return StatusCode(500, new ErrorResponse("An error occurred while reconstructing text", ex.Message));
        }
    }

    /// <summary>
    /// Get atom details by ID
    /// </summary>
    /// <param name="compositionId">The composition ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The composition details</returns>
    [HttpGet("atom/{compositionId:long}")]
    [ProducesResponseType(typeof(CompositionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAtom(long compositionId, CancellationToken cancellationToken)
    {
        if (compositionId <= 0)
            return BadRequest(new ErrorResponse("Invalid composition ID"));

        try
        {
            var composition = await _ingestionService.GetCompositionAsync(compositionId, cancellationToken);
            
            if (composition == null)
                return NotFound(new ErrorResponse($"Composition {compositionId} not found"));

            return Ok(new CompositionResponse(
                composition.Id,
                composition.TypeId?.ToString(),
                (long)composition.HilbertHigh,
                (long)composition.HilbertLow,
                Convert.ToHexString(composition.ContentHash)
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get composition {CompositionId}", compositionId);
            return StatusCode(500, new ErrorResponse("An error occurred while retrieving composition", ex.Message));
        }
    }
}

// Request/Response DTOs
public record IngestTextRequest(
    [Required]
    [MinLength(1, ErrorMessage = "Text cannot be empty")]
    [MaxLength(10_000_000, ErrorMessage = "Text exceeds maximum length of 10MB")]
    string Text
);

public record IngestTextResponse(
    long RootAtomId,
    int CharacterCount,
    string Status
);

public record ReconstructTextResponse(
    long AtomId,
    string Text,
    int CharacterCount,
    string Status
);

public record CompositionResponse(
    long Id,
    string? TypeId,
    long HilbertHigh,
    long HilbertLow,
    string ContentHash
);

public record ErrorResponse(string Error, params string[] Details);
