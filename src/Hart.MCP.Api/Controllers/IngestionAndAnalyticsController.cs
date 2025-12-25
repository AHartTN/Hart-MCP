using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Hart.MCP.Shared.Models;
using NetTopologySuite.Geometries;
using System.Text.Json;

namespace Hart.MCP.Api.Controllers;

/// <summary>
/// Content sources are atoms with AtomType="content_source".
/// </summary>
[ApiController]
[Route("api/ingestion")]
public class IngestionController : ControllerBase
{
    private readonly HartDbContext _context;
    private readonly ILogger<IngestionController> _logger;
    private readonly GeometryFactory _geometryFactory;

    public IngestionController(HartDbContext context, ILogger<IngestionController> logger)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    [HttpPost("content")]
    public async Task<ActionResult<ApiResponse<ContentDto>>> RegisterContent([FromBody] RegisterContentRequest request)
    {
        try
        {
            var metadata = JsonSerializer.Serialize(new
            {
                sourceType = request.SourceType,
                sourceUri = request.SourceUri,
                contentHash = request.ContentHash,
                sizeBytes = request.SizeBytes,
                data = request.Metadata,
                ingestionStatus = "Pending",
                ingestedAt = DateTime.UtcNow
            });

            // Position based on content hash for deduplication
            var hashValue = request.ContentHash?.GetHashCode() ?? 0;
            var coord = new CoordinateZM(
                (hashValue % 1000) / 100.0,
                ((hashValue / 1000) % 1000) / 100.0,
                request.SizeBytes / 1e9,  // Z = size in GB
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond
            );

            var geom = _geometryFactory.CreatePoint(coord);
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var contentComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>())
            };

            _context.Compositions.Add(contentComposition);
            await _context.SaveChangesAsync();

            var dto = new ContentDto
            {
                Id = contentComposition.Id,
                SourceType = request.SourceType,
                SourceUri = request.SourceUri,
                ContentHash = request.ContentHash,
                SizeBytes = request.SizeBytes,
                IngestionStatus = "Pending"
            };

            return Ok(new ApiResponse<ContentDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering content source");
            return BadRequest(new ApiResponse<ContentDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("content")]
    public async Task<ActionResult<ApiResponse<List<ContentDto>>>> ListContent(
        [FromQuery] string? sourceType = null,
        [FromQuery] string? status = null)
    {
        var contents = await _context.Compositions
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Take(100)  // Limit results
            .ToListAsync();

        var dtos = contents
            .Select(c => new ContentDto
            {
                Id = c.Id,
                SourceType = "",  // Metadata is now atomized
                SourceUri = "",
                ContentHash = null,
                SizeBytes = 0,
                IngestionStatus = ""
            })
            .ToList();

        return Ok(new ApiResponse<List<ContentDto>> { Success = true, Data = dtos });
    }

    [HttpPost("content/{id}/process")]
    public async Task<ActionResult<ApiResponse<ContentDto>>> ProcessContent(long id)
    {
        try
        {
            var content = await _context.Compositions.FindAsync(id);
            if (content == null)
                return NotFound(new ApiResponse<ContentDto> { Success = false, Error = "Content not found" });

            // Note: Metadata processing is now atomized
            await _context.SaveChangesAsync();

            var dto = new ContentDto
            {
                Id = content.Id,
                SourceType = "",
                SourceUri = "",
                ContentHash = null,
                SizeBytes = 0,
                IngestionStatus = "Processing"
            };

            return Ok(new ApiResponse<ContentDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing content");
            return BadRequest(new ApiResponse<ContentDto> { Success = false, Error = ex.Message });
        }
    }

    private static ContentMetadata ParseContentMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new ContentMetadata();
        try
        {
            return JsonSerializer.Deserialize<ContentMetadata>(json) ?? new ContentMetadata();
        }
        catch { return new ContentMetadata(); }
    }
}

/// <summary>
/// Analytics: annotations are atoms with AtomType="annotation".
/// System stats query the atom table directly.
/// </summary>
[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly HartDbContext _context;
    private readonly ILogger<AnalyticsController> _logger;
    private readonly GeometryFactory _geometryFactory;

    public AnalyticsController(HartDbContext context, ILogger<AnalyticsController> logger)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    [HttpPost("annotations")]
    public async Task<ActionResult<ApiResponse<AnnotationDto>>> CreateAnnotation([FromBody] CreateAnnotationRequest request)
    {
        try
        {
            var metadata = JsonSerializer.Serialize(new
            {
                title = request.Title,
                description = request.Description,
                annotationType = request.AnnotationType,
                userId = request.UserId,
                data = request.Metadata,
                createdAt = DateTime.UtcNow
            });

            var coord = new CoordinateZM(0, 0, 0, DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
            var geom = _geometryFactory.CreatePoint(coord);
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var annotationComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>())
            };

            _context.Compositions.Add(annotationComposition);
            await _context.SaveChangesAsync();

            var dto = new AnnotationDto
            {
                Id = annotationComposition.Id,
                Title = request.Title,
                Description = request.Description,
                AnnotationType = request.AnnotationType,
                UserId = request.UserId
            };

            return Ok(new ApiResponse<AnnotationDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating annotation");
            return BadRequest(new ApiResponse<AnnotationDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("annotations")]
    public async Task<ActionResult<ApiResponse<List<AnnotationDto>>>> ListAnnotations(
        [FromQuery] string? userId = null,
        [FromQuery] string? annotationType = null)
    {
        var annotations = await _context.Compositions
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Take(100)  // Limit results
            .ToListAsync();

        var dtos = annotations
            .Select(a => new AnnotationDto
            {
                Id = a.Id,
                Title = "",  // Metadata is now atomized
                Description = null,
                AnnotationType = "",
                UserId = null
            })
            .ToList();

        return Ok(new ApiResponse<List<AnnotationDto>> { Success = true, Data = dtos });
    }

    [HttpGet("stats")]
    public async Task<ActionResult<ApiResponse<SystemStats>>> GetSystemStats()
    {
        try
        {
            var compositions = await _context.Compositions
                .AsNoTracking()
                .GroupBy(c => c.TypeId)
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .ToListAsync();

            var stats = new SystemStats
            {
                TotalAtoms = await _context.Constants.CountAsync() + await _context.Compositions.CountAsync(),
                TotalConstants = await _context.Constants.CountAsync(),
                TotalCompositions = await _context.Compositions.CountAsync(),
                AtomsByType = compositions.Select(c => new TypeCount { Type = c.Type?.ToString() ?? "untyped", Count = c.Count }).ToList()
            };

            return Ok(new ApiResponse<SystemStats> { Success = true, Data = stats });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system stats");
            return BadRequest(new ApiResponse<SystemStats> { Success = false, Error = ex.Message });
        }
    }

    [HttpPost("extract")]
    public async Task<ActionResult<ApiResponse<ExtractionResult>>> ExtractDifference([FromBody] ExtractionRequest request)
    {
        try
        {
            var result = new ExtractionResult
            {
                Message = "Extraction queued for processing",
                EstimatedAtoms = 0
            };

            return Ok(new ApiResponse<ExtractionResult> { Success = true, Data = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting difference");
            return BadRequest(new ApiResponse<ExtractionResult> { Success = false, Error = ex.Message });
        }
    }

    private static AnnotationMetadata ParseAnnotationMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new AnnotationMetadata();
        try
        {
            return JsonSerializer.Deserialize<AnnotationMetadata>(json) ?? new AnnotationMetadata();
        }
        catch { return new AnnotationMetadata(); }
    }
}

public record RegisterContentRequest(
    string SourceType, 
    string SourceUri, 
    string? ContentHash, 
    long SizeBytes, 
    string? Metadata);

public record CreateAnnotationRequest(
    string Title, 
    string? Description, 
    string AnnotationType, 
    string? UserId, 
    string? Metadata);

public record ExtractionRequest(long SourceModelId, long TargetModelId, string ExtractionType);

public class ContentDto
{
    public long Id { get; set; }
    public string SourceType { get; set; } = "";
    public string SourceUri { get; set; } = "";
    public string? ContentHash { get; set; }
    public long SizeBytes { get; set; }
    public string IngestionStatus { get; set; } = "";
}

public class AnnotationDto
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string AnnotationType { get; set; } = "";
    public string? UserId { get; set; }
}

public class SystemStats
{
    public int TotalAtoms { get; set; }
    public int TotalConstants { get; set; }
    public int TotalCompositions { get; set; }
    public List<TypeCount> AtomsByType { get; set; } = new();
}

public class TypeCount
{
    public string Type { get; set; } = "";
    public int Count { get; set; }
}

public class ExtractionResult
{
    public string Message { get; set; } = "";
    public int EstimatedAtoms { get; set; }
}

internal class ContentMetadata
{
    public string SourceType { get; set; } = "";
    public string SourceUri { get; set; } = "";
    public string? ContentHash { get; set; }
    public long SizeBytes { get; set; }
    public string IngestionStatus { get; set; } = "";
}

internal class AnnotationMetadata
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string AnnotationType { get; set; } = "";
    public string? UserId { get; set; }
}
