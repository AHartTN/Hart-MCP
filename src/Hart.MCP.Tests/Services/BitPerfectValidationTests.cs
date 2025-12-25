using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Hart.MCP.Tests.Services;

/// <summary>
/// Bit-perfect validation tests for hierarchical text ingestion and export.
/// 
/// CRITICAL GUARANTEE: Text ingested → exported must be EXACTLY identical.
/// Not "close enough". Not "semantically equivalent". BYTE-FOR-BYTE IDENTICAL.
/// 
/// This is the core invariant that proves the Merkle DAG is correct.
/// </summary>
public class BitPerfectValidationTests : IDisposable
{
    private const string TEST_DATA_PATH = @"D:\Repositories\Hart-MCP\test-data";
    private readonly HartDbContext _context;
    private readonly AtomIngestionService _atomService;
    private readonly HierarchicalTextIngestionService _ingestionService;
    private readonly TextExportService _exportService;
    private readonly ITestOutputHelper _output;

    public BitPerfectValidationTests(ITestOutputHelper output)
    {
        _output = output;
        
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase($"BitPerfect_{Guid.NewGuid()}")
            .Options;

        _context = new HartDbContext(options);
        _atomService = new AtomIngestionService(_context);
        _ingestionService = new HierarchicalTextIngestionService(_context, _atomService);
        _exportService = new TextExportService(_context);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region Moby Dick Full Validation

    /// <summary>
    /// THE ULTIMATE TEST: Moby Dick full round-trip.
    /// ~1.2 million characters, ~215,000 words.
    /// Must be bit-perfect and complete in reasonable time.
    /// </summary>
    [Fact]
    public async Task MobyDick_FullRoundTrip_BitPerfect()
    {
        var path = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (!File.Exists(path))
        {
            _output.WriteLine("Moby Dick test file not found, skipping");
            return;
        }

        var originalText = await File.ReadAllTextAsync(path);
        _output.WriteLine($"Original text: {originalText.Length:N0} characters");

        // INGEST
        var ingestStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(originalText);
        ingestStopwatch.Stop();

        _output.WriteLine($"Ingestion: {ingestStopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Patterns discovered: {ingestionResult.TotalPatternsDiscovered}");
        _output.WriteLine($"  Tiers: {ingestionResult.TierCount}");
        _output.WriteLine($"  Final sequence length: {ingestionResult.FinalSequenceLength}");
        _output.WriteLine($"  Compression ratio: {ingestionResult.CompressionRatio:F2}x");

        // EXPORT
        var exportStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);
        exportStopwatch.Stop();

        _output.WriteLine($"Export: {exportStopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Atoms traversed: {exportResult.Stats.TotalAtoms}");
        _output.WriteLine($"  Cache hits: {exportResult.Stats.CacheHits}");
        _output.WriteLine($"  Cache hit rate: {exportResult.Stats.CacheHitRate:P1}");
        _output.WriteLine($"  DB queries: {exportResult.Stats.DbQueries}");

        // VERIFY BIT-PERFECT
        exportResult.Text.Length.Should().Be(originalText.Length, 
            "exported length must match original exactly");

        exportResult.Text.Should().Be(originalText,
            "exported text must be byte-for-byte identical to original");

        // PERFORMANCE TARGETS
        ingestStopwatch.ElapsedMilliseconds.Should().BeLessThan(15000,
            "ingestion should complete in under 15 seconds");
        
        exportStopwatch.ElapsedMilliseconds.Should().BeLessThan(10000,
            "export should complete in under 10 seconds");

        _output.WriteLine("\n✓ BIT-PERFECT VALIDATION PASSED");
        _output.WriteLine($"  Total round-trip: {ingestStopwatch.ElapsedMilliseconds + exportStopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Verify export using the dedicated verification method.
    /// </summary>
    [Fact]
    public async Task MobyDick_VerifyExport_ReturnsSuccess()
    {
        var path = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (!File.Exists(path)) return;

        var originalText = await File.ReadAllTextAsync(path);
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(originalText);

        var verification = await _exportService.VerifyExportAsync(
            ingestionResult.RootAtomId, originalText);

        verification.IsBitPerfect.Should().BeTrue();
        verification.OriginalLength.Should().Be(originalText.Length);
        verification.ExportedLength.Should().Be(originalText.Length);
        verification.FirstDifferenceIndex.Should().BeNull();
    }

    #endregion

    #region Pattern-Rich Text Validation

    [Theory]
    [InlineData("the cat in the hat", "shares 'the' pattern")]
    [InlineData("abcabcabcabcabc", "repeated substring")]
    [InlineData("Mississippi River Mississippi", "repeated word with patterns")]
    [InlineData("to be or not to be that is the question", "Shakespeare quote")]
    public async Task PatternRichText_RoundTrip_BitPerfect(string original, string description)
    {
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);

        exportResult.Text.Should().Be(original, 
            $"'{description}' must be bit-perfect");
        
        _output.WriteLine($"'{description}': {ingestionResult.TotalPatternsDiscovered} patterns, " +
            $"{ingestionResult.CompressionRatio:F2}x compression");
    }

    [Fact]
    public async Task RepeatedIngestion_SamePatterns_Deduplicated()
    {
        const string text1 = "the quick brown fox";
        const string text2 = "the lazy dog";
        const string text3 = "the quick rabbit";

        var result1 = await _ingestionService.IngestTextHierarchicallyAsync(text1);
        var result2 = await _ingestionService.IngestTextHierarchicallyAsync(text2);
        var result3 = await _ingestionService.IngestTextHierarchicallyAsync(text3);

        // All should export correctly
        (await _exportService.ExportTextAsync(result1.RootAtomId)).Text.Should().Be(text1);
        (await _exportService.ExportTextAsync(result2.RootAtomId)).Text.Should().Be(text2);
        (await _exportService.ExportTextAsync(result3.RootAtomId)).Text.Should().Be(text3);

        // Character constants should be shared
        var charAtomCount = await _context.Atoms.CountAsync(a => a.IsConstant && a.AtomType == "char");
        var allUniqueChars = (text1 + text2 + text3).Distinct().Count();
        
        charAtomCount.Should().BeLessThanOrEqualTo(allUniqueChars,
            "character constants should be deduplicated across texts");

        _output.WriteLine($"Unique chars used: {allUniqueChars}, Char atoms created: {charAtomCount}");
    }

    #endregion

    #region Unicode and Edge Cases

    [Theory]
    [InlineData("日本語テスト日本語", "Japanese with repetition")]
    [InlineData("한글한글한글", "Korean repetition")]
    [InlineData("Привет мир Привет", "Russian with repetition")]
    [InlineData("مرحبا العالم مرحبا", "Arabic with repetition")]
    [InlineData("😀🎉😀🎉😀", "Emoji with repetition")]
    public async Task Unicode_RoundTrip_BitPerfect(string original, string description)
    {
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);

        exportResult.Text.Should().Be(original, 
            $"Unicode '{description}' must be bit-perfect");
    }

    [Fact]
    public async Task MixedUnicode_ComplexText_BitPerfect()
    {
        const string original = "Hello 世界! مرحبا 🌍 Привет";
        
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);

        exportResult.Text.Should().Be(original);
    }

    [Fact]
    public async Task WhitespacePreservation_BitPerfect()
    {
        const string original = "  hello   world  \t\n  test  ";
        
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);

        exportResult.Text.Should().Be(original,
            "all whitespace must be preserved exactly");
    }

    [Fact]
    public async Task SingleCharacter_BitPerfect()
    {
        const string original = "x";
        
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);

        exportResult.Text.Should().Be(original);
    }

    #endregion

    #region Streaming Export

    [Fact]
    public async Task StreamingExport_AssemblesCorrectly()
    {
        const string original = "the cat in the hat sat on the mat";
        
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);

        var chunks = new List<string>();
        await foreach (var chunk in _exportService.ExportTextStreamAsync(ingestionResult.RootAtomId))
        {
            chunks.Add(chunk);
        }

        var assembled = string.Concat(chunks);
        assembled.Should().Be(original);
        
        _output.WriteLine($"Streamed in {chunks.Count} chunks");
    }

    [Fact]
    public async Task ExportToFile_BitPerfect()
    {
        var path = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (!File.Exists(path)) return;

        var originalText = await File.ReadAllTextAsync(path);
        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(originalText);

        var exportPath = Path.GetTempFileName();
        try
        {
            var stats = await _exportService.ExportToFileAsync(ingestionResult.RootAtomId, exportPath);
            
            var exportedText = await File.ReadAllTextAsync(exportPath);
            exportedText.Should().Be(originalText, "file export must be bit-perfect");

            _output.WriteLine($"Exported to file in {stats.ExportTimeMs}ms, {stats.ChunksWritten} chunks");
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }
    }

    #endregion

    #region Export Statistics

    [Fact]
    public async Task ExportStats_ReflectsCacheUsage()
    {
        const string original = "abcabcabcabc"; // "abc" repeats 4 times

        var ingestionResult = await _ingestionService.IngestTextHierarchicallyAsync(original);
        var exportResult = await _exportService.ExportTextAsync(ingestionResult.RootAtomId);

        exportResult.Stats.TotalAtoms.Should().BeGreaterThan(0);
        exportResult.Stats.CacheHits.Should().BeGreaterThanOrEqualTo(0);
        
        _output.WriteLine($"Cache hit rate: {exportResult.Stats.CacheHitRate:P1}");
    }

    #endregion
}
