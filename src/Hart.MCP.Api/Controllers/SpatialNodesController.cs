using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Shared.DTOs;
using Hart.MCP.Shared.Models;
using NetTopologySuite.Geometries;
using System.Diagnostics;

namespace Hart.MCP.Api.Controllers;

/// <summary>
/// SpatialNodes are now Compositions. This controller provides backward-compatible
/// endpoints that work with the Compositions table.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SpatialNodesController : ControllerBase
{
    private readonly HartDbContext _context;
    private readonly ILogger<SpatialNodesController> _logger;
    private readonly GeometryFactory _geometryFactory;

    public SpatialNodesController(HartDbContext context, ILogger<SpatialNodesController> logger)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<SpatialNodeDto>>> CreateNode([FromBody] CreateSpatialNodeRequest request)
    {
        try
        {
            var location = _geometryFactory.CreatePoint(new CoordinateZM(request.X, request.Y, request.Z, request.M));
            var merkleHashHex = ComputeMerkleHash(request);
            var merkleHashBytes = Convert.FromHexString(merkleHashHex);

            // Check for existing by content hash
            var existing = await _context.Compositions
                .Where(c => c.ContentHash == merkleHashBytes)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();

            if (existing != 0)
            {
                var existingComposition = await _context.Compositions.FindAsync(existing);
                if (existingComposition != null)
                {
                    var existingDto = MapToDto(existingComposition);
                    return Ok(new ApiResponse<SpatialNodeDto> { Success = true, Data = existingDto });
                }
            }

            var hilbert = Hart.MCP.Core.Native.HartNative.point_to_hilbert(
                new Hart.MCP.Core.Native.HartNative.PointZM
                {
                    X = request.X, Y = request.Y, Z = request.Z, M = request.M
                });

            var composition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = location,
                ContentHash = merkleHashBytes,
                TypeId = null  // Can be set later if needed
            };

            _context.Compositions.Add(composition);
            await _context.SaveChangesAsync();

            var dto = MapToDto(composition);
            return Ok(new ApiResponse<SpatialNodeDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating spatial node");
            return BadRequest(new ApiResponse<SpatialNodeDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpPost("query")]
    public async Task<ActionResult<ApiResponse<SpatialQueryResult<SpatialNodeDto>>>> QueryNodes([FromBody] SpatialQueryRequest request)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var centerPoint = _geometryFactory.CreatePoint(new Coordinate(request.CenterX, request.CenterY));

            var query = _context.Compositions.AsQueryable();

            // Spatial distance filter
            query = query.Where(c => c.Geom != null && c.Geom.Distance(centerPoint) <= request.Radius);

            // Z-dimension filter
            if (request.MinZ.HasValue)
                query = query.Where(c => c.Geom != null && c.Geom.Coordinate.Z >= request.MinZ.Value);
            if (request.MaxZ.HasValue)
                query = query.Where(c => c.Geom != null && c.Geom.Coordinate.Z <= request.MaxZ.Value);

            // Type filter - can filter by TypeId if needed
            // if (!string.IsNullOrEmpty(request.NodeType))
            //     query = query.Where(c => c.TypeId == someTypeId);

            var compositions = await query
                .AsNoTracking()
                .Take(request.MaxResults)
                .ToListAsync();

            var results = compositions.Select(MapToDto).ToList();

            sw.Stop();

            var queryResult = new SpatialQueryResult<SpatialNodeDto>
            {
                Results = results,
                TotalCount = results.Count,
                QueryTimeMs = sw.Elapsed.TotalMilliseconds
            };

            return Ok(new ApiResponse<SpatialQueryResult<SpatialNodeDto>> { Success = true, Data = queryResult });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying spatial nodes");
            return BadRequest(new ApiResponse<SpatialQueryResult<SpatialNodeDto>> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<SpatialNodeDto>>> GetNode(long id)
    {
        var composition = await _context.Compositions.FindAsync(id);
        if (composition == null)
            return NotFound(new ApiResponse<SpatialNodeDto> { Success = false, Error = "Node not found" });

        return Ok(new ApiResponse<SpatialNodeDto> { Success = true, Data = MapToDto(composition) });
    }

    private static SpatialNodeDto MapToDto(Composition composition)
    {
        var coord = composition.Geom?.Coordinate ?? new Coordinate(0, 0);
        return new SpatialNodeDto(
            composition.Id,
            coord.X,
            coord.Y,
            double.IsNaN(coord.Z) ? 0 : coord.Z,
            double.IsNaN(coord.M) ? 0 : coord.M,
            composition.TypeId?.ToString(),  // NodeType from TypeId
            Convert.ToHexString(composition.ContentHash),
            null,  // ParentHash - legacy concept, Relations now handle relationships
            null,  // Metadata - now atomized
            DateTime.UtcNow  // CreatedAt - not stored on Composition, use current time
        );
    }

    private static string ComputeMerkleHash(CreateSpatialNodeRequest request)
    {
        var data = $"{request.X}{request.Y}{request.Z}{request.M}{request.NodeType}{request.ParentHash}";
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    }
}
