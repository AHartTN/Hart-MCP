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
/// AI Models are atoms with AtomType="ai_model".
/// Model layers are atoms with AtomType="model_layer" that reference the model.
/// Model comparisons are atoms with AtomType="model_comparison".
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ModelsController : ControllerBase
{
    private readonly HartDbContext _context;
    private readonly ILogger<ModelsController> _logger;
    private readonly GeometryFactory _geometryFactory;

    public ModelsController(HartDbContext context, ILogger<ModelsController> logger)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ModelDto>>> RegisterModel([FromBody] RegisterModelRequest request)
    {
        try
        {
            var metadata = JsonSerializer.Serialize(new
            {
                name = request.Name,
                modelType = request.ModelType,
                architecture = request.Architecture,
                version = request.Version,
                parameterCount = request.ParameterCount,
                sparsityRatio = request.SparsityRatio ?? 1.0,
                sourceFormat = request.SourceFormat,
                data = request.Metadata,
                registeredAt = DateTime.UtcNow
            });

            // Model positioned based on parameter count (size) and architecture
            var coord = new CoordinateZM(
                request.ParameterCount / 1e9,  // X = billions of params
                request.SparsityRatio ?? 1.0,  // Y = sparsity
                0,
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond
            );

            var geom = _geometryFactory.CreatePoint(coord);
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = coord.X, Y = coord.Y, Z = coord.Z, M = coord.M
            });

            var modelComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>())
            };

            _context.Compositions.Add(modelComposition);
            await _context.SaveChangesAsync();

            var dto = new ModelDto
            {
                Id = modelComposition.Id,
                Name = request.Name,
                ModelType = request.ModelType,
                Architecture = request.Architecture,
                Version = request.Version,
                ParameterCount = request.ParameterCount,
                SparsityRatio = request.SparsityRatio ?? 1.0,
                LayerCount = 0
            };

            return Ok(new ApiResponse<ModelDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering model");
            return BadRequest(new ApiResponse<ModelDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<ModelDto>>> GetModel(long id)
    {
        var model = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        if (model == null)
            return NotFound(new ApiResponse<ModelDto> { Success = false, Error = "Model not found" });

        // Get layer count from Relations
        var layerCount = await _context.Relations.CountAsync(r => r.CompositionId == model.Id);

        var dto = new ModelDto
        {
            Id = model.Id,
            Name = "",  // Metadata is now atomized
            ModelType = "",
            Architecture = "",
            Version = null,
            ParameterCount = 0,
            SparsityRatio = 1.0,
            LayerCount = layerCount
        };

        return Ok(new ApiResponse<ModelDto> { Success = true, Data = dto });
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<ModelDto>>>> ListModels(
        [FromQuery] string? modelType = null,
        [FromQuery] string? architecture = null)
    {
        var models = await _context.Compositions
            .AsNoTracking()
            .ToListAsync();

        // Get layer counts for all models
        var modelIds = models.Select(m => m.Id).ToList();
        var layerCounts = await _context.Relations
            .Where(r => modelIds.Contains(r.CompositionId))
            .GroupBy(r => r.CompositionId)
            .Select(g => new { CompositionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompositionId, x => x.Count);

        var dtos = models
            .Select(m => new ModelDto
            {
                Id = m.Id,
                Name = "",
                ModelType = "",
                Architecture = "",
                Version = null,
                ParameterCount = 0,
                SparsityRatio = 1.0,
                LayerCount = layerCounts.GetValueOrDefault(m.Id, 0)
            })
            .ToList();

        return Ok(new ApiResponse<List<ModelDto>> { Success = true, Data = dtos });
    }

    [HttpPost("compare")]
    public async Task<ActionResult<ApiResponse<ComparisonDto>>> CompareModels([FromBody] CompareModelsRequest request)
    {
        try
        {
            var model1 = await _context.Compositions.FindAsync(request.Model1Id);
            var model2 = await _context.Compositions.FindAsync(request.Model2Id);

            if (model1 == null || model2 == null)
                return NotFound(new ApiResponse<ComparisonDto> { Success = false, Error = "One or both models not found" });

            // Calculate similarity based on spatial distance
            var distance = model1.Geom.Distance(model2.Geom);
            var similarity = 1.0 / (1.0 + distance);

            var metadata = JsonSerializer.Serialize(new
            {
                model1Id = request.Model1Id,
                model2Id = request.Model2Id,
                comparisonType = request.ComparisonType,
                similarityScore = similarity,
                analyzedAt = DateTime.UtcNow
            });

            // Comparison positioned at midpoint between models
            var midpoint = new CoordinateZM(
                (model1.Geom.Coordinate.X + model2.Geom.Coordinate.X) / 2,
                (model1.Geom.Coordinate.Y + model2.Geom.Coordinate.Y) / 2,
                (model1.Geom.Coordinate.Z + model2.Geom.Coordinate.Z) / 2,
                DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond
            );

            var geom = _geometryFactory.CreatePoint(midpoint);
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = midpoint.X, Y = midpoint.Y, Z = midpoint.Z, M = midpoint.M
            });

            var comparisonComposition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = NativeLibrary.ComputeCompositionHash(
                    new[] { request.Model1Id, request.Model2Id },
                    new[] { 1, 1 })
            };

            _context.Compositions.Add(comparisonComposition);
            await _context.SaveChangesAsync();

            // Add Relations for the comparison
            _context.Relations.Add(new Relation { CompositionId = comparisonComposition.Id, ChildCompositionId = request.Model1Id, Position = 0, Multiplicity = 1 });
            _context.Relations.Add(new Relation { CompositionId = comparisonComposition.Id, ChildCompositionId = request.Model2Id, Position = 1, Multiplicity = 1 });
            await _context.SaveChangesAsync();

            // Get layer counts
            var model1LayerCount = await _context.Relations.CountAsync(r => r.CompositionId == request.Model1Id);
            var model2LayerCount = await _context.Relations.CountAsync(r => r.CompositionId == request.Model2Id);

            var dto = new ComparisonDto
            {
                Id = comparisonComposition.Id,
                Model1Id = request.Model1Id,
                Model2Id = request.Model2Id,
                ComparisonType = request.ComparisonType,
                SimilarityScore = similarity,
                UniqueToModel1Count = model1LayerCount,
                UniqueToModel2Count = model2LayerCount
            };

            return Ok(new ApiResponse<ComparisonDto> { Success = true, Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing models");
            return BadRequest(new ApiResponse<ComparisonDto> { Success = false, Error = ex.Message });
        }
    }

    [HttpGet("comparisons")]
    public async Task<ActionResult<ApiResponse<List<ComparisonDto>>>> ListComparisons()
    {
        var comparisons = await _context.Compositions
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .Take(100)  // Limit comparisons
            .ToListAsync();

        var dtos = comparisons.Select(c => new ComparisonDto
        {
            Id = c.Id,
            Model1Id = 0,  // Would need to query AtomRefs to get these
            Model2Id = 0,
            ComparisonType = "",
            SimilarityScore = 0
        }).ToList();

        return Ok(new ApiResponse<List<ComparisonDto>> { Success = true, Data = dtos });
    }

    private static ModelMetadata ParseModelMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new ModelMetadata();
        try
        {
            return JsonSerializer.Deserialize<ModelMetadata>(json) ?? new ModelMetadata();
        }
        catch { return new ModelMetadata(); }
    }

    private static ComparisonMetadata ParseComparisonMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new ComparisonMetadata();
        try
        {
            return JsonSerializer.Deserialize<ComparisonMetadata>(json) ?? new ComparisonMetadata();
        }
        catch { return new ComparisonMetadata(); }
    }
}

public record RegisterModelRequest(
    string Name,
    string ModelType,
    string Architecture,
    string? Version,
    long ParameterCount,
    double? SparsityRatio,
    string? SourceFormat,
    string? Metadata);

public record CompareModelsRequest(long Model1Id, long Model2Id, string ComparisonType);

public class ModelDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string ModelType { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string? Version { get; set; }
    public long ParameterCount { get; set; }
    public double SparsityRatio { get; set; }
    public int LayerCount { get; set; }
}

public class ComparisonDto
{
    public long Id { get; set; }
    public long Model1Id { get; set; }
    public long Model2Id { get; set; }
    public string ComparisonType { get; set; } = "";
    public double SimilarityScore { get; set; }
    public int UniqueToModel1Count { get; set; }
    public int UniqueToModel2Count { get; set; }
}

internal class ModelMetadata
{
    public string Name { get; set; } = "";
    public string ModelType { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string? Version { get; set; }
    public long ParameterCount { get; set; }
    public double SparsityRatio { get; set; } = 1.0;
}

internal class ComparisonMetadata
{
    public long Model1Id { get; set; }
    public long Model2Id { get; set; }
    public string ComparisonType { get; set; } = "";
    public double SimilarityScore { get; set; }
}
