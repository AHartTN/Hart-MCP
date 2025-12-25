using Hart.MCP.Core.Native;
using Hart.MCP.Core.Services.Optimized;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Diagnostics;

namespace Hart.MCP.Api.Controllers;

/// <summary>
/// Performance diagnostics and parallel ingestion endpoints
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PerformanceController : ControllerBase
{
    private readonly ParallelAtomIngestionService _parallelIngestion;
    private readonly ParallelSpatialQueryService _parallelQuery;
    private readonly ILogger<PerformanceController> _logger;

    public PerformanceController(
        ParallelAtomIngestionService parallelIngestion,
        ParallelSpatialQueryService parallelQuery,
        ILogger<PerformanceController> logger)
    {
        _parallelIngestion = parallelIngestion;
        _parallelQuery = parallelQuery;
        _logger = logger;
    }

    /// <summary>
    /// Get system performance capabilities
    /// </summary>
    [HttpGet("capabilities")]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        try
        {
            var simdCaps = NativeLibrary.detect_simd_capabilities();
            
            return Ok(new CapabilitiesResponse
            {
                ProcessorCount = Environment.ProcessorCount,
                Is64Bit = Environment.Is64BitProcess,
                DotNetVersion = Environment.Version.ToString(),
                SIMDCapabilities = new SIMDInfo
                {
                    SSE2 = simdCaps.HasSse2 != 0,
                    SSE41 = simdCaps.HasSse41 != 0,
                    AVX = simdCaps.HasAvx != 0,
                    AVX2 = simdCaps.HasAvx2 != 0,
                    AVX512 = simdCaps.HasAvx512F != 0,
                    Description = NativeLibrary.GetSIMDCapabilities()
                },
                VectorHardwareAccelerated = System.Numerics.Vector.IsHardwareAccelerated,
                VectorSize = System.Numerics.Vector<double>.Count,
                IngestionCacheSize = _parallelIngestion.CacheSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get capabilities");
            return StatusCode(500, new { error = "Failed to get capabilities", message = ex.Message });
        }
    }

    /// <summary>
    /// Parallel ingest text with performance metrics
    /// </summary>
    [HttpPost("ingest/parallel")]
    [EnableRateLimiting("ingestion")]
    public async Task<ActionResult<ParallelIngestionResponse>> IngestParallel(
        [FromBody] ParallelIngestionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Text))
            return BadRequest("Text is required");

        var sw = Stopwatch.StartNew();
        
        try
        {
            var atomId = await _parallelIngestion.IngestTextParallelAsync(
                request.Text,
                request.AtomType ?? "text",
                cancellationToken);
            
            sw.Stop();
            
            return Ok(new ParallelIngestionResponse
            {
                AtomId = atomId,
                CharacterCount = request.Text.Length,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                CharactersPerSecond = request.Text.Length * 1000.0 / sw.ElapsedMilliseconds,
                CacheSize = _parallelIngestion.CacheSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parallel ingestion failed");
            return StatusCode(500, new { error = "Ingestion failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Batch parallel ingest multiple texts
    /// </summary>
    [HttpPost("ingest/batch")]
    [EnableRateLimiting("ingestion")]
    public async Task<ActionResult<BatchIngestionResponse>> IngestBatch(
        [FromBody] BatchIngestionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Texts == null || request.Texts.Length == 0)
            return BadRequest("Texts array is required");

        var sw = Stopwatch.StartNew();
        
        try
        {
            var atomIds = await _parallelIngestion.IngestTextsParallelAsync(
                request.Texts,
                request.AtomType ?? "text",
                cancellationToken);
            
            sw.Stop();
            var totalChars = request.Texts.Sum(t => t.Length);
            
            return Ok(new BatchIngestionResponse
            {
                AtomIds = atomIds,
                TextCount = request.Texts.Length,
                TotalCharacters = totalChars,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                CharactersPerSecond = totalChars * 1000.0 / sw.ElapsedMilliseconds,
                TextsPerSecond = request.Texts.Length * 1000.0 / sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch ingestion failed");
            return StatusCode(500, new { error = "Batch ingestion failed", message = ex.Message });
        }
    }

    /// <summary>
    /// SIMD-accelerated k-nearest neighbors query
    /// </summary>
    [HttpPost("query/knn")]
    [EnableRateLimiting("query")]
    public async Task<ActionResult<KNNResponse>> QueryKNN(
        [FromBody] KNNRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            var results = await _parallelQuery.FindKNearestAsync(
                request.X, request.Y, request.Z, request.M,
                request.K ?? 10,
                request.AtomType,
                cancellationToken);
            
            sw.Stop();
            
            return Ok(new KNNResponse
            {
                Results = results.Select(r => new KNNResult
                {
                    AtomId = r.Atom.Id,
                    Distance = r.Distance,
                    AtomType = r.Atom.AtomType,
                    IsConstant = r.Atom.IsConstant
                }).ToList(),
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                QueryPoint = new double[] { request.X, request.Y, request.Z, request.M }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KNN query failed");
            return StatusCode(500, new { error = "Query failed", message = ex.Message });
        }
    }

    /// <summary>
    /// SIMD-accelerated attention computation
    /// </summary>
    [HttpPost("query/attention")]
    [EnableRateLimiting("query")]
    public async Task<ActionResult<SIMDAttentionResponse>> ComputeAttention(
        [FromBody] SIMDAttentionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.KeyAtomIds == null || request.KeyAtomIds.Length == 0)
            return BadRequest("KeyAtomIds array is required");

        var sw = Stopwatch.StartNew();
        
        try
        {
            var results = await _parallelQuery.ComputeAttentionScoresAsync(
                request.QueryAtomId,
                request.KeyAtomIds,
                cancellationToken);
            
            sw.Stop();
            
            return Ok(new SIMDAttentionResponse
            {
                Scores = results.Select(r => new AttentionScore
                {
                    AtomId = r.AtomId,
                    Score = r.Score
                }).ToList(),
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                QueryAtomId = request.QueryAtomId,
                KeyCount = request.KeyAtomIds.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attention computation failed");
            return StatusCode(500, new { error = "Attention computation failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Get atom type aggregation statistics
    /// </summary>
    [HttpGet("stats/aggregation")]
    public async Task<ActionResult<List<AtomTypeAggregate>>> GetAggregation(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _parallelQuery.AggregateByTypeAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregation query failed");
            return StatusCode(500, new { error = "Query failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Run performance benchmark
    /// </summary>
    [HttpPost("benchmark")]
    public async Task<ActionResult<BenchmarkResponse>> RunBenchmark(
        [FromBody] BenchmarkRequest request,
        CancellationToken cancellationToken)
    {
        var response = new BenchmarkResponse();
        
        // Generate test data
        var testText = new string('A', request.TextSize ?? 10000);
        var texts = Enumerable.Range(0, request.TextCount ?? 10)
            .Select(i => testText)
            .ToArray();
        
        // Benchmark ingestion
        var swIngestion = Stopwatch.StartNew();
        var atomIds = await _parallelIngestion.IngestTextsParallelAsync(texts, "benchmark", cancellationToken);
        swIngestion.Stop();
        
        var totalChars = texts.Sum(t => t.Length);
        response.IngestionResults = new IngestionBenchmark
        {
            TotalCharacters = totalChars,
            TotalTexts = texts.Length,
            ElapsedMs = swIngestion.ElapsedMilliseconds,
            CharactersPerSecond = totalChars * 1000.0 / swIngestion.ElapsedMilliseconds,
            TextsPerSecond = texts.Length * 1000.0 / swIngestion.ElapsedMilliseconds
        };
        
        // Benchmark KNN queries
        if (request.RunKNN ?? true)
        {
            var swKnn = Stopwatch.StartNew();
            var knnIterations = request.KNNIterations ?? 100;
            
            for (int i = 0; i < knnIterations; i++)
            {
                await _parallelQuery.FindKNearestAsync(0.5, 0.5, 0.5, 0.5, 10, null, cancellationToken);
            }
            swKnn.Stop();
            
            response.KNNResults = new KNNBenchmark
            {
                Iterations = knnIterations,
                ElapsedMs = swKnn.ElapsedMilliseconds,
                QueriesPerSecond = knnIterations * 1000.0 / swKnn.ElapsedMilliseconds
            };
        }
        
        response.ProcessorCount = Environment.ProcessorCount;
        response.SIMDInfo = NativeLibrary.GetSIMDCapabilities();
        
        return Ok(response);
    }

    /// <summary>
    /// Clear ingestion cache
    /// </summary>
    [HttpPost("cache/clear")]
    public ActionResult ClearCache()
    {
        _parallelIngestion.ClearCache();
        return Ok(new { message = "Cache cleared" });
    }
}

#region Request/Response DTOs

public class CapabilitiesResponse
{
    public int ProcessorCount { get; set; }
    public bool Is64Bit { get; set; }
    public string DotNetVersion { get; set; } = "";
    public SIMDInfo SIMDCapabilities { get; set; } = new();
    public bool VectorHardwareAccelerated { get; set; }
    public int VectorSize { get; set; }
    public int IngestionCacheSize { get; set; }
}

public class SIMDInfo
{
    public bool SSE2 { get; set; }
    public bool SSE41 { get; set; }
    public bool AVX { get; set; }
    public bool AVX2 { get; set; }
    public bool AVX512 { get; set; }
    public string Description { get; set; } = "";
}

public class ParallelIngestionRequest
{
    public string Text { get; set; } = "";
    public string? AtomType { get; set; }
}

public class ParallelIngestionResponse
{
    public long AtomId { get; set; }
    public int CharacterCount { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public double CharactersPerSecond { get; set; }
    public int CacheSize { get; set; }
}

public class BatchIngestionRequest
{
    public string[] Texts { get; set; } = Array.Empty<string>();
    public string? AtomType { get; set; }
}

public class BatchIngestionResponse
{
    public long[] AtomIds { get; set; } = Array.Empty<long>();
    public int TextCount { get; set; }
    public int TotalCharacters { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public double CharactersPerSecond { get; set; }
    public double TextsPerSecond { get; set; }
}

public class KNNRequest
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double M { get; set; }
    public int? K { get; set; }
    public string? AtomType { get; set; }
}

public class KNNResponse
{
    public List<KNNResult> Results { get; set; } = new();
    public long ElapsedMilliseconds { get; set; }
    public double[] QueryPoint { get; set; } = Array.Empty<double>();
}

public class KNNResult
{
    public long AtomId { get; set; }
    public double Distance { get; set; }
    public string? AtomType { get; set; }
    public bool IsConstant { get; set; }
}

public class SIMDAttentionRequest
{
    public long QueryAtomId { get; set; }
    public long[] KeyAtomIds { get; set; } = Array.Empty<long>();
}

public class SIMDAttentionResponse
{
    public List<AttentionScore> Scores { get; set; } = new();
    public long ElapsedMilliseconds { get; set; }
    public long QueryAtomId { get; set; }
    public int KeyCount { get; set; }
}

public class AttentionScore
{
    public long AtomId { get; set; }
    public double Score { get; set; }
}

public class BenchmarkRequest
{
    public int? TextSize { get; set; }
    public int? TextCount { get; set; }
    public bool? RunKNN { get; set; }
    public int? KNNIterations { get; set; }
}

public class BenchmarkResponse
{
    public IngestionBenchmark? IngestionResults { get; set; }
    public KNNBenchmark? KNNResults { get; set; }
    public int ProcessorCount { get; set; }
    public string SIMDInfo { get; set; } = "";
}

public class IngestionBenchmark
{
    public int TotalCharacters { get; set; }
    public int TotalTexts { get; set; }
    public long ElapsedMs { get; set; }
    public double CharactersPerSecond { get; set; }
    public double TextsPerSecond { get; set; }
}

public class KNNBenchmark
{
    public int Iterations { get; set; }
    public long ElapsedMs { get; set; }
    public double QueriesPerSecond { get; set; }
}

#endregion
