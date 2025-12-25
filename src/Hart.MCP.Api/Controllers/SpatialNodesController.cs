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
/// SpatialNodes are now just atoms. This controller provides backward-compatible
/// endpoints that work with the unified atom table.
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
            var existing = await _context.Atoms
                .Where(a => a.ContentHash == merkleHashBytes)
                .Select(a => a.Id)
                .FirstOrDefaultAsync();

            if (existing != 0)
            {
                var existingAtom = await _context.Atoms.FindAsync(existing);
                if (existingAtom != null)
                {
                    var existingDto = MapToDto(existingAtom);
                    return Ok(new ApiResponse<SpatialNodeDto> { Success = true, Data = existingDto });
                }
            }

            var hilbert = Hart.MCP.Core.Native.NativeLibrary.point_to_hilbert(
                new Hart.MCP.Core.Native.NativeLibrary.PointZM
                {
                    X = request.X, Y = request.Y, Z = request.Z, M = request.M
                });

            var atom = new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = location,
                IsConstant = false,
                Refs = Array.Empty<long>(),
                Multiplicities = Array.Empty<int>(),
                ContentHash = merkleHashBytes,
                AtomType = request.NodeType ?? "node",
                Metadata = request.Metadata
            };

            _context.Atoms.Add(atom);
            await _context.SaveChangesAsync();

            var dto = MapToDto(atom);
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

            var query = _context.Atoms.AsQueryable();

            // Spatial distance filter
            query = query.Where(n => n.Geom.Distance(centerPoint) <= request.Radius);

            // Z-dimension filter
            if (request.MinZ.HasValue)
                query = query.Where(n => n.Geom.Coordinate.Z >= request.MinZ.Value);
            if (request.MaxZ.HasValue)
                query = query.Where(n => n.Geom.Coordinate.Z <= request.MaxZ.Value);

            // Type filter
            if (!string.IsNullOrEmpty(request.NodeType))
                query = query.Where(n => n.AtomType == request.NodeType);

            var atoms = await query
                .AsNoTracking()
                .Take(request.MaxResults)
                .ToListAsync();

            var results = atoms.Select(MapToDto).ToList();

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
        var atom = await _context.Atoms.FindAsync(id);
        if (atom == null)
            return NotFound(new ApiResponse<SpatialNodeDto> { Success = false, Error = "Node not found" });

        return Ok(new ApiResponse<SpatialNodeDto> { Success = true, Data = MapToDto(atom) });
    }

    private static SpatialNodeDto MapToDto(Atom atom)
    {
        var coord = atom.Geom.Coordinate;
        return new SpatialNodeDto(
            atom.Id,
            coord.X,
            coord.Y,
            double.IsNaN(coord.Z) ? 0 : coord.Z,
            double.IsNaN(coord.M) ? 0 : coord.M,
            atom.AtomType,
            Convert.ToHexString(atom.ContentHash),
            null,  // ParentHash - legacy concept, refs now handle relationships
            atom.Metadata,
            DateTime.UtcNow  // We don't track created time in atoms - could add to metadata
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
