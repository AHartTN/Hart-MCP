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
/// Conversations are compositions with related turns.
/// Conversation turns are compositions that reference the conversation via Relations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ConversationsController : ControllerBase
{
    private readonly HartDbContext _context;
    private readonly ILogger<ConversationsController> _logger;
    private readonly GeometryFactory _geometryFactory;

    public ConversationsController(HartDbContext context, ILogger<ConversationsController> logger)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> CreateSession([FromBody] CreateSessionRequest request)
    {
        try
        {
            // Conversation metadata in JSONB
            var metadata = JsonSerializer.Serialize(new
            {
                sessionType = request.SessionType,
                userId = request.UserId,
                data = request.Metadata,
                startedAt = DateTime.UtcNow
            });

            // Create conversation composition at origin (will be updated as turns accumulate)
            var geom = _geometryFactory.CreatePoint(new CoordinateZM(0, 0, 0, 0));
            var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM { X = 0, Y = 0, Z = 0, M = 0 });

            var conversationComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = HartNative.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>())
            };

            _context.Compositions.Add(conversationComposition);
            await _context.SaveChangesAsync();

            var dto = new ConversationDto
            {
                Id = conversationComposition.Id,
                SessionType = request.SessionType,
                UserId = request.UserId,
                Metadata = request.Metadata,
                StartedAt = DateTime.UtcNow,
                TurnCount = 0
            };

            return Ok(new ApiResponse<ConversationDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating conversation session");
            return BadRequest(new ApiResponse<ConversationDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpPost("sessions/{sessionId}/turns")]
    public async Task<ActionResult<ApiResponse<TurnDto>>> AddTurn(
        long sessionId, 
        [FromBody] AddTurnRequest request)
    {
        try
        {
            var session = await _context.Compositions
                .FirstOrDefaultAsync(c => c.Id == sessionId);

            if (session == null)
                return NotFound(new ApiResponse<TurnDto> { Success = false, Error = "Session not found" });

            // Get current turn count from Relations
            var turnCount = await _context.Relations.CountAsync(r => r.CompositionId == sessionId);

            // Create turn composition with spatial coordinates if provided
            var coord = new CoordinateZM(
                request.SpatialX ?? turnCount,  // Use turn number as X if not provided
                request.SpatialY ?? 0,
                0,
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond  // M = timestamp
            );

            var turnMetadata = JsonSerializer.Serialize(new
            {
                role = request.Role,
                content = request.Content,
                turnNumber = turnCount + 1,
                createdAt = DateTime.UtcNow
            });

            var turnGeom = _geometryFactory.CreatePoint(coord);
            var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var turnComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = turnGeom,
                ContentHash = HartNative.ComputeCompositionHash(new[] { sessionId, turnCount + 1 }, new[] { 1, 1 })
            };

            _context.Compositions.Add(turnComposition);
            await _context.SaveChangesAsync();

            // Create Relation linking session to turn
            _context.Relations.Add(new Relation
            {
                CompositionId = sessionId,
                ChildCompositionId = turnComposition.Id,
                Position = turnCount,
                Multiplicity = 1
            });

            var dto = new TurnDto
            {
                Id = turnComposition.Id,
                SessionId = sessionId,
                TurnNumber = turnCount + 1,
                Role = request.Role,
                Content = request.Content,
                SpatialX = coord.X,
                SpatialY = coord.Y
            };

            return Ok(new ApiResponse<TurnDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding conversation turn");
            return BadRequest(new ApiResponse<TurnDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("sessions/{sessionId}")]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> GetSession(long sessionId)
    {
        var session = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == sessionId);

        if (session == null)
            return NotFound(new ApiResponse<ConversationDto> { Success = false, Error = "Session not found" });

        // Get turn count from Relations
        var turnCount = await _context.Relations.CountAsync(r => r.CompositionId == sessionId);

        var dto = new ConversationDto
        {
            Id = session.Id,
            SessionType = "",  // Metadata is now atomized
            UserId = null,
            Metadata = null,
            StartedAt = DateTime.UtcNow, // Composition doesn't have CreatedAt
            TurnCount = turnCount
        };

        return Ok(new ApiResponse<ConversationDto> { Success = true, Data = dto });
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<List<ConversationDto>>>> ListSessions(
        [FromQuery] string? userId = null,
        [FromQuery] string? sessionType = null,
        [FromQuery] int limit = 50)
    {
        var query = _context.Compositions
            .AsNoTracking();

        // Get all compositions (sessions are compositions)
        var sessions = await query
            .OrderByDescending(c => c.Id)
            .Take(limit)
            .ToListAsync();

        // Get turn counts for all sessions
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var turnCounts = await _context.Relations
            .Where(r => sessionIds.Contains(r.CompositionId))
            .GroupBy(r => r.CompositionId)
            .Select(g => new { CompositionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompositionId, x => x.Count);

        var dtos = sessions
            .Select(s => new ConversationDto
            {
                Id = s.Id,
                SessionType = "",
                UserId = null,
                Metadata = null,
                StartedAt = DateTime.UtcNow, // Composition doesn't have CreatedAt
                TurnCount = turnCounts.GetValueOrDefault(s.Id, 0)
            })
            .Take(limit)
            .ToList();

        return Ok(new ApiResponse<List<ConversationDto>> { Success = true, Data = dtos });
    }

    private static ConversationMetadata ParseConversationMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new ConversationMetadata();

        try
        {
            return JsonSerializer.Deserialize<ConversationMetadata>(json) ?? new ConversationMetadata();
        }
        catch
        {
            return new ConversationMetadata();
        }
    }
}

public record CreateSessionRequest(string SessionType, string? UserId, string? Metadata);
public record AddTurnRequest(string Role, string Content, double? SpatialX, double? SpatialY);

public class ConversationDto
{
    public long Id { get; set; }
    public string SessionType { get; set; } = "";
    public string? UserId { get; set; }
    public string? Metadata { get; set; }
    public DateTime StartedAt { get; set; }
    public int TurnCount { get; set; }
}

public class TurnDto
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int TurnNumber { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public double SpatialX { get; set; }
    public double SpatialY { get; set; }
}

internal class ConversationMetadata
{
    public string SessionType { get; set; } = "";
    public string? UserId { get; set; }
    public string? Data { get; set; }
    public DateTime StartedAt { get; set; }
}
