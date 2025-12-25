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

            var modelAtom = new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = geom,
                IsConstant = false,
                Refs = Array.Empty<long>(),  // Will contain layer atom IDs
                Multiplicities = Array.Empty<int>(),
                ContentHash = NativeLibrary.ComputeCompositionHash(Array.Empty<long>(), Array.Empty<int>()),
                AtomType = "ai_model",
                Metadata = metadata
            };

            _context.Atoms.Add(modelAtom);
            await _context.SaveChangesAsync();

            var dto = new ModelDto
            {
                Id = modelAtom.Id,
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
        var model = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.AtomType == "ai_model");

        if (model == null)
            return NotFound(new ApiResponse<ModelDto> { Success = false, Error = "Model not found" });

        var meta = ParseModelMetadata(model.Metadata);

        var dto = new ModelDto
        {
            Id = model.Id,
            Name = meta.Name,
            ModelType = meta.ModelType,
            Architecture = meta.Architecture,
            Version = meta.Version,
            ParameterCount = meta.ParameterCount,
            SparsityRatio = meta.SparsityRatio,
            LayerCount = model.Refs?.Length ?? 0
        };

        return Ok(new ApiResponse<ModelDto> { Success = true, Data = dto });
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<ModelDto>>>> ListModels(
        [FromQuery] string? modelType = null,
        [FromQuery] string? architecture = null)
    {
        var models = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.AtomType == "ai_model")
            .ToListAsync();

        var dtos = models
            .Select(m =>
            {
                var meta = ParseModelMetadata(m.Metadata);
                return new ModelDto
                {
                    Id = m.Id,
                    Name = meta.Name,
                    ModelType = meta.ModelType,
                    Architecture = meta.Architecture,
                    Version = meta.Version,
                    ParameterCount = meta.ParameterCount,
                    SparsityRatio = meta.SparsityRatio,
                    LayerCount = m.Refs?.Length ?? 0
                };
            })
            .Where(d =>
                (string.IsNullOrEmpty(modelType) || d.ModelType == modelType) &&
                (string.IsNullOrEmpty(architecture) || d.Architecture == architecture))
            .ToList();

        return Ok(new ApiResponse<List<ModelDto>> { Success = true, Data = dtos });
    }

    [HttpPost("compare")]
    public async Task<ActionResult<ApiResponse<ComparisonDto>>> CompareModels([FromBody] CompareModelsRequest request)
    {
        try
        {
            var model1 = await _context.Atoms.FindAsync(request.Model1Id);
            var model2 = await _context.Atoms.FindAsync(request.Model2Id);

            if (model1 == null || model2 == null || 
                model1.AtomType != "ai_model" || model2.AtomType != "ai_model")
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

            var comparisonAtom = new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = geom,
                IsConstant = false,
                Refs = new[] { request.Model1Id, request.Model2Id },
                Multiplicities = new[] { 1, 1 },
                ContentHash = NativeLibrary.ComputeCompositionHash(
                    new[] { request.Model1Id, request.Model2Id },
                    new[] { 1, 1 }),
                AtomType = "model_comparison",
                Metadata = metadata
            };

            _context.Atoms.Add(comparisonAtom);
            await _context.SaveChangesAsync();

            var dto = new ComparisonDto
            {
                Id = comparisonAtom.Id,
                Model1Id = request.Model1Id,
                Model2Id = request.Model2Id,
                ComparisonType = request.ComparisonType,
                SimilarityScore = similarity,
                UniqueToModel1Count = model1.Refs?.Length ?? 0,
                UniqueToModel2Count = model2.Refs?.Length ?? 0
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
        var comparisons = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.AtomType == "model_comparison")
            .OrderByDescending(a => a.Id)
            .ToListAsync();

        var dtos = comparisons.Select(c =>
        {
            var meta = ParseComparisonMetadata(c.Metadata);
            return new ComparisonDto
            {
                Id = c.Id,
                Model1Id = meta.Model1Id,
                Model2Id = meta.Model2Id,
                ComparisonType = meta.ComparisonType,
                SimilarityScore = meta.SimilarityScore
            };
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
