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
/// Visualization views and bookmarks are atoms with AtomType="viz_view" and "bookmark".
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VisualizationController : ControllerBase
{
    private readonly HartDbContext _context;
    private readonly ILogger<VisualizationController> _logger;
    private readonly GeometryFactory _geometryFactory;

    public VisualizationController(HartDbContext context, ILogger<VisualizationController> logger)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    [HttpPost("views")]
    public async Task<ActionResult<ApiResponse<ViewDto>>> CreateView([FromBody] CreateViewRequest request)
    {
        try
        {
            var metadata = JsonSerializer.Serialize(new
            {
                name = request.Name,
                viewType = request.ViewType,
                projectionMethod = request.ProjectionMethod,
                minX = request.MinX,
                maxX = request.MaxX,
                minY = request.MinY,
                maxY = request.MaxY,
                minZ = request.MinZ,
                maxZ = request.MaxZ,
                colorScheme = request.ColorScheme,
                filterCriteria = request.FilterCriteria,
                userId = request.UserId,
                settings = request.Settings,
                createdAt = DateTime.UtcNow
            });

            // View positioned at center of its bounds
            var centerX = (request.MinX + request.MaxX) / 2;
            var centerY = (request.MinY + request.MaxY) / 2;
            var centerZ = ((request.MinZ ?? 0) + (request.MaxZ ?? 0)) / 2;

            var coord = new CoordinateZM(centerX, centerY, centerZ, DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
            var geom = _geometryFactory.CreatePoint(coord);
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var viewComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>())
            };

            _context.Compositions.Add(viewComposition);
            await _context.SaveChangesAsync();

            var dto = new ViewDto
            {
                Id = viewComposition.Id,
                Name = request.Name,
                ViewType = request.ViewType,
                ProjectionMethod = request.ProjectionMethod
            };

            return Ok(new ApiResponse<ViewDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating visualization view");
            return BadRequest(new ApiResponse<ViewDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("views")]
    public async Task<ActionResult<ApiResponse<List<ViewDto>>>> ListViews([FromQuery] string? userId = null)
    {
        var views = await _context.Compositions
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Take(100)  // Limit results
            .ToListAsync();

        var dtos = views
            .Select(v => new ViewDto
            {
                Id = v.Id,
                Name = "",  // Metadata is now atomized
                ViewType = "",
                ProjectionMethod = "",
                UserId = null
            })
            .Where(d => string.IsNullOrEmpty(userId) || d.UserId == userId)
            .ToList();

        return Ok(new ApiResponse<List<ViewDto>> { Success = true, Data = dtos });
    }

    [HttpPost("bookmarks")]
    public async Task<ActionResult<ApiResponse<BookmarkDto>>> CreateBookmark([FromBody] CreateBookmarkRequest request)
    {
        try
        {
            var metadata = JsonSerializer.Serialize(new
            {
                name = request.Name,
                description = request.Description,
                zoomLevel = request.ZoomLevel,
                userId = request.UserId,
                data = request.Metadata,
                createdAt = DateTime.UtcNow
            });

            var coord = new CoordinateZM(
                request.CenterX,
                request.CenterY,
                request.CenterZ ?? 0,
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond
            );

            var geom = _geometryFactory.CreatePoint(coord);
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var bookmarkComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>())
            };

            _context.Compositions.Add(bookmarkComposition);
            await _context.SaveChangesAsync();

            var dto = new BookmarkDto
            {
                Id = bookmarkComposition.Id,
                Name = request.Name,
                Description = request.Description,
                CenterX = request.CenterX,
                CenterY = request.CenterY,
                CenterZ = request.CenterZ,
                ZoomLevel = request.ZoomLevel
            };

            return Ok(new ApiResponse<BookmarkDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating bookmark");
            return BadRequest(new ApiResponse<BookmarkDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("bookmarks")]
    public async Task<ActionResult<ApiResponse<List<BookmarkDto>>>> ListBookmarks([FromQuery] string? userId = null)
    {
        var bookmarks = await _context.Compositions
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Take(100)  // Limit results
            .ToListAsync();

        var dtos = bookmarks
            .Select(b =>
            {
                var coord = b.Geom.Coordinate;
                return new BookmarkDto
                {
                    Id = b.Id,
                    Name = "",  // Metadata is now atomized
                    Description = null,
                    CenterX = coord.X,
                    CenterY = coord.Y,
                    CenterZ = double.IsNaN(coord.Z) ? null : coord.Z,
                    ZoomLevel = 1.0,
                    UserId = null
                };
            })
            .Where(d => string.IsNullOrEmpty(userId) || d.UserId == userId)
            .ToList();

        return Ok(new ApiResponse<List<BookmarkDto>> { Success = true, Data = dtos });
    }

    [HttpPost("render")]
    public async Task<ActionResult<ApiResponse<RenderResult>>> RenderRegion([FromBody] RenderRequest request)
    {
        try
        {
            var query = _context.Compositions.AsQueryable();

            // Spatial filtering using coordinate access
            query = query.Where(c =>
                c.Geom.Coordinate.X >= request.MinX && c.Geom.Coordinate.X <= request.MaxX &&
                c.Geom.Coordinate.Y >= request.MinY && c.Geom.Coordinate.Y <= request.MaxY);

            if (request.MinZ.HasValue && request.MaxZ.HasValue)
            {
                query = query.Where(c =>
                    c.Geom.Coordinate.Z >= request.MinZ.Value && c.Geom.Coordinate.Z <= request.MaxZ.Value);
            }

            // Type filter - can filter by TypeId if needed
            // if (!string.IsNullOrEmpty(request.NodeType))
            //     query = query.Where(c => c.TypeId == someTypeId);

            var compositions = await query
                .AsNoTracking()
                .Take(request.MaxNodes)
                .ToListAsync();

            var result = new RenderResult
            {
                Nodes = compositions.Select(c =>
                {
                    var coord = c.Geom.Coordinate;
                    return new RenderNode
                    {
                        Id = c.Id,
                        X = coord.X,
                        Y = coord.Y,
                        Z = double.IsNaN(coord.Z) ? 0 : coord.Z,
                        NodeType = c.TypeId?.ToString(),
                        Label = null  // Metadata is now atomized
                    };
                }).ToList(),
                TotalInRegion = compositions.Count
            };

            return Ok(new ApiResponse<RenderResult> { Success = true, Data = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rendering region");
            return BadRequest(new ApiResponse<RenderResult> { Success = false, Error = ex.Message });
        }
    }

    private static ViewMetadata ParseViewMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new ViewMetadata();
        try
        {
            return JsonSerializer.Deserialize<ViewMetadata>(json) ?? new ViewMetadata();
        }
        catch { return new ViewMetadata(); }
    }

    private static BookmarkMetadata ParseBookmarkMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new BookmarkMetadata();
        try
        {
            return JsonSerializer.Deserialize<BookmarkMetadata>(json) ?? new BookmarkMetadata();
        }
        catch { return new BookmarkMetadata(); }
    }
}

public record CreateViewRequest(
    string Name,
    string ViewType,
    string ProjectionMethod,
    double MinX, double MaxX,
    double MinY, double MaxY,
    double? MinZ, double? MaxZ,
    string? ColorScheme,
    string? FilterCriteria,
    string? UserId,
    string? Settings);

public record CreateBookmarkRequest(
    string Name,
    string? Description,
    double CenterX,
    double CenterY,
    double? CenterZ,
    double ZoomLevel,
    string? UserId,
    string? Metadata);

public record RenderRequest(
    double MinX, double MaxX,
    double MinY, double MaxY,
    double? MinZ, double? MaxZ,
    string? NodeType,
    int MaxNodes = 1000);

public class ViewDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string ViewType { get; set; } = "";
    public string ProjectionMethod { get; set; } = "";
    public string? UserId { get; set; }
}

public class BookmarkDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double? CenterZ { get; set; }
    public double ZoomLevel { get; set; }
    public string? UserId { get; set; }
}

public class RenderResult
{
    public List<RenderNode> Nodes { get; set; } = new();
    public int TotalInRegion { get; set; }
}

public class RenderNode
{
    public long Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string NodeType { get; set; } = "";
    public string? Label { get; set; }
}

internal class ViewMetadata
{
    public string Name { get; set; } = "";
    public string ViewType { get; set; } = "";
    public string ProjectionMethod { get; set; } = "";
    public string? UserId { get; set; }
}

internal class BookmarkMetadata
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public double ZoomLevel { get; set; }
    public string? UserId { get; set; }
}
