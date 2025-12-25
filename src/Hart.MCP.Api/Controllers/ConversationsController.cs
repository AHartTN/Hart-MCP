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
/// Conversations are atoms with AtomType="conversation".
/// Conversation turns are atoms with AtomType="turn" that reference the conversation.
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

            // Create conversation atom at origin (will be updated as turns accumulate)
            var geom = _geometryFactory.CreatePoint(new CoordinateZM(0, 0, 0, 0));
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM { X = 0, Y = 0, Z = 0, M = 0 });

            var conversationAtom = new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = geom,
                IsConstant = false,
                Refs = Array.Empty<long>(),
                Multiplicities = Array.Empty<int>(),
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>()),
                AtomType = "conversation",
                Metadata = metadata
            };

            _context.Atoms.Add(conversationAtom);
            await _context.SaveChangesAsync();

            var dto = new ConversationDto
            {
                Id = conversationAtom.Id,
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
            var session = await _context.Atoms
                .FirstOrDefaultAsync(a => a.Id == sessionId && a.AtomType == "conversation");

            if (session == null)
                return NotFound(new ApiResponse<TurnDto> { Success = false, Error = "Session not found" });

            // Get current turn count
            var turnCount = session.Refs?.Length ?? 0;

            // Create turn atom with spatial coordinates if provided
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
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var turnAtom = new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = turnGeom,
                IsConstant = false,
                Refs = new[] { sessionId },  // Turn references its session
                Multiplicities = new[] { 1 },
                ContentHash = NativeLibrary.ComputeCompositionHash(new[] { sessionId, turnCount + 1 }, new[] { 1, 1 }),
                AtomType = "turn",
                Metadata = turnMetadata
            };

            _context.Atoms.Add(turnAtom);

            // Update session to reference all turns
            var newRefs = (session.Refs ?? Array.Empty<long>()).Append(turnAtom.Id).ToArray();
            session.Refs = newRefs;
            session.Multiplicities = Enumerable.Repeat(1, newRefs.Length).ToArray();

            await _context.SaveChangesAsync();

            var dto = new TurnDto
            {
                Id = turnAtom.Id,
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
        var session = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == sessionId && a.AtomType == "conversation");

        if (session == null)
            return NotFound(new ApiResponse<ConversationDto> { Success = false, Error = "Session not found" });

        var meta = ParseConversationMetadata(session.Metadata);

        var dto = new ConversationDto
        {
            Id = session.Id,
            SessionType = meta.SessionType,
            UserId = meta.UserId,
            Metadata = meta.Data,
            StartedAt = meta.StartedAt,
            TurnCount = session.Refs?.Length ?? 0
        };

        return Ok(new ApiResponse<ConversationDto> { Success = true, Data = dto });
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<List<ConversationDto>>>> ListSessions(
        [FromQuery] string? userId = null,
        [FromQuery] string? sessionType = null,
        [FromQuery] int limit = 50)
    {
        var query = _context.Atoms
            .AsNoTracking()
            .Where(a => a.AtomType == "conversation");

        var sessions = await query
            .OrderByDescending(a => a.Id)
            .Take(limit * 2)  // Over-fetch to filter
            .ToListAsync();

        var dtos = sessions
            .Select(s =>
            {
                var meta = ParseConversationMetadata(s.Metadata);
                return new ConversationDto
                {
                    Id = s.Id,
                    SessionType = meta.SessionType,
                    UserId = meta.UserId,
                    Metadata = meta.Data,
                    StartedAt = meta.StartedAt,
                    TurnCount = s.Refs?.Length ?? 0
                };
            })
            .Where(d =>
                (string.IsNullOrEmpty(userId) || d.UserId == userId) &&
                (string.IsNullOrEmpty(sessionType) || d.SessionType == sessionType))
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
