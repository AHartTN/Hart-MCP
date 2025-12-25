using FluentAssertions;
using Hart.MCP.Core.Services.Ingestion;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Hart.MCP.Tests.Services.Ingestion;

/// <summary>
/// Tests for native C++ bulk ingestion via PostgreSQL COPY.
/// These tests use REAL PostgreSQL - not InMemory.
/// 
/// Performance targets:
/// - Unicode BMP (65K): < 2 seconds
/// - 80MB SafeTensor: < 10 seconds
/// </summary>
public class NativeBulkIngestionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly NativeBulkIngestionService _service;
    
    // libpq connection string format (lowercase keywords, dbname not Database)
    private const string ConnectionString = "host=localhost port=5432 dbname=HART-MCP user=hartonomous password=hartonomous";
    private const string ModelPath = @"D:\Repositories\Hart-MCP\test-data\embedding_models\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    public NativeBulkIngestionTests(ITestOutputHelper output)
    {
        _output = output;
        _service = new NativeBulkIngestionService(ConnectionString);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    [Fact]
    public async Task Native_UnicodeSeeding_BMP_Under2Seconds()
    {
        _output.WriteLine("=== NATIVE UNICODE SEEDING (BMP) ===");
        _output.WriteLine($"Target: 65,536 codepoints in < 2 seconds");
        _output.WriteLine("");

        var sw = Stopwatch.StartNew();
        
        var progress = new Progress<UnicodeSeedProgress>(p =>
        {
            if (p.CodepointsSeeded % 10000 == 0)
            {
                _output.WriteLine($"  Progress: {p.CodepointsSeeded:N0}/{p.TotalCodepoints:N0} ({p.PercentComplete:F1}%)");
            }
        });

        var count = await _service.SeedUnicodeAsync(fullUnicode: false, progress: progress);
        sw.Stop();

        _output.WriteLine("");
        _output.WriteLine($"✓ Seeded {count:N0} codepoints in {sw.ElapsedMilliseconds:N0}ms");
        _output.WriteLine($"  Rate: {count * 1000.0 / sw.ElapsedMilliseconds:N0} codepoints/sec");

        count.Should().BeGreaterThan(60000, "should seed most of BMP");
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "native seeding should complete in < 2 seconds");
    }

    [Fact]
    public async Task Native_SafeTensor_AllMiniLM_Under10Seconds()
    {
        var safeTensorPath = Path.Combine(ModelPath, "model.safetensors");
        
        if (!File.Exists(safeTensorPath))
        {
            _output.WriteLine($"SKIPPED: SafeTensor file not found at {safeTensorPath}");
            return;
        }

        var fileInfo = new FileInfo(safeTensorPath);
        _output.WriteLine("=== NATIVE SAFETENSOR INGESTION ===");
        _output.WriteLine($"Model: all-MiniLM-L6-v2");
        _output.WriteLine($"File: {safeTensorPath}");
        _output.WriteLine($"Size: {fileInfo.Length:N0} bytes ({fileInfo.Length / (1024.0 * 1024):F2} MB)");
        _output.WriteLine($"Target: < 10 seconds with 25% sparsity");
        _output.WriteLine("");

        var sw = Stopwatch.StartNew();
        
        var progress = new Progress<ModelIngestionProgress>(p =>
        {
            _output.WriteLine($"  [{sw.ElapsedMilliseconds,6}ms] {p.Phase}: {p.TensorsProcessed}/{p.TensorsTotal} tensors, {p.SparsityPercent:F1}% sparse");
        });

        var result = await _service.IngestSafeTensorAsync(
            safeTensorPath,
            "all-MiniLM-L6-v2-native",
            targetSparsityPercent: 25.0f,
            progress: progress);
        sw.Stop();

        _output.WriteLine("");
        _output.WriteLine("=== RESULTS ===");
        _output.WriteLine($"Tensors:          {result.TensorCount}");
        _output.WriteLine($"Total Parameters: {result.TotalParameters:N0}");
        _output.WriteLine($"Total Values:     {result.TotalValues:N0}");
        _output.WriteLine($"Stored Values:    {result.StoredValues:N0}");
        _output.WriteLine($"Skipped Values:   {result.SkippedValues:N0}");
        _output.WriteLine($"Sparsity:         {result.SparsityPercent:F2}%");
        _output.WriteLine($"Processing Time:  {result.ProcessingTimeMs:N0}ms");
        _output.WriteLine($"Rate:             {result.TotalParameters * 1000.0 / result.ProcessingTimeMs:N0} params/sec");
        _output.WriteLine("");
        _output.WriteLine($"Wall clock time:  {sw.ElapsedMilliseconds:N0}ms");

        result.TensorCount.Should().BeGreaterThan(0);
        result.TotalParameters.Should().BeGreaterThan(0);
        result.SparsityPercent.Should().BeGreaterThan(20, "should achieve at least 20% sparsity");
        sw.ElapsedMilliseconds.Should().BeLessThan(10000, "native ingestion should complete in < 10 seconds");
    }

    [Fact]
    public async Task Native_SafeTensor_NoSparsity_Benchmark()
    {
        var safeTensorPath = Path.Combine(ModelPath, "model.safetensors");
        
        if (!File.Exists(safeTensorPath))
        {
            _output.WriteLine($"SKIPPED: SafeTensor file not found at {safeTensorPath}");
            return;
        }

        _output.WriteLine("=== NATIVE SAFETENSOR - NO SPARSITY BENCHMARK ===");
        _output.WriteLine("Testing raw ingestion speed with 0% sparsity (all values stored)");
        _output.WriteLine("");

        var sw = Stopwatch.StartNew();

        var result = await _service.IngestSafeTensorAsync(
            safeTensorPath,
            "all-MiniLM-L6-v2-full",
            targetSparsityPercent: 0,  // No sparsity - store everything
            sparsityThreshold: 0);
        sw.Stop();

        _output.WriteLine($"Tensors:          {result.TensorCount}");
        _output.WriteLine($"Total Parameters: {result.TotalParameters:N0}");
        _output.WriteLine($"Stored Values:    {result.StoredValues:N0}");
        _output.WriteLine($"Sparsity:         {result.SparsityPercent:F2}%");
        _output.WriteLine($"Processing Time:  {result.ProcessingTimeMs:N0}ms");
        _output.WriteLine($"Rate:             {result.TotalParameters * 1000.0 / result.ProcessingTimeMs:N0} params/sec");

        result.StoredValues.Should().Be(result.TotalValues, "with 0 threshold, all values should be stored");
    }
}
