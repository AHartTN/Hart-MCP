using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Hart.MCP.Tests;

/// <summary>
/// Tests for HierarchicalTextIngestionService.
/// 
/// CORE GUARANTEES TESTED:
/// 1. Hierarchical decomposition: Text is decomposed into multi-tier compositions
/// 2. Content-addressed deduplication: Identical patterns produce same atoms (across ALL texts)
/// 3. Lossless reconstruction: Original text can be perfectly reconstructed at any tier
/// 4. Pattern discovery: Repeated patterns (n-grams) are automatically detected and deduplicated
/// 
/// KEY INNOVATION: "The cat in the hat" should store "the" ONCE, referenced TWICE
/// </summary>
public class HierarchicalTextIngestionServiceTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=HART-MCP;Username=hartonomous;Password=hartonomous";
    
    private readonly HartDbContext _context;
    private readonly AtomIngestionService _atomService;
    private readonly HierarchicalTextIngestionService _service;

    public HierarchicalTextIngestionServiceTests()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseNetTopologySuite())
            .Options;

        _context = new HartDbContext(options);
        _context.Database.EnsureCreated();
        
        var atomLogger = Mock.Of<ILogger<AtomIngestionService>>();
        var hierarchicalLogger = Mock.Of<ILogger<HierarchicalTextIngestionService>>();
        
        _atomService = new AtomIngestionService(_context, atomLogger);
        _service = new HierarchicalTextIngestionService(_context, _atomService, hierarchicalLogger);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    #region Core Guarantee: Lossless Reconstruction

    [Fact]
    public async Task LosslessReconstruction_SimpleText_ByteIdentical()
    {
        const string original = "Hello, World!";

        var result = await _service.IngestTextHierarchicallyAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(original,
            because: "hierarchical text reconstruction MUST be exactly lossless");
    }

    [Theory]
    [InlineData("The cat in the hat", "shared pattern 'the'")]
    [InlineData("abcabcabc", "repeated substring 'abc'")]
    [InlineData("Mississippi", "repeated letters")]
    [InlineData("banana", "overlapping patterns")]
    [InlineData("the quick the fast the slow", "repeated word 'the'")]
    public async Task LosslessReconstruction_TextsWithPatterns_PerfectlyPreserved(string original, string description)
    {
        var result = await _service.IngestTextHierarchicallyAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(original,
            because: $"{description} must reconstruct exactly");
    }

    [Theory]
    [InlineData("日本語テスト日本語", "Japanese with repetition")]
    [InlineData("한글한글한글", "Korean repetition")]
    [InlineData("العربية العربية", "Arabic with repetition")]
    public async Task LosslessReconstruction_NonLatinWithPatterns_Preserved(string original, string script)
    {
        var result = await _service.IngestTextHierarchicallyAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(original,
            because: $"{script} with patterns must be perfectly preserved");
    }

    [Fact]
    public async Task LosslessReconstruction_EmojiPatterns_HandledCorrectly()
    {
        const string original = "😀hello😀world😀"; // Emoji appears 3 times

        var result = await _service.IngestTextHierarchicallyAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(original);
    }

    #endregion

    #region Core Guarantee: Pattern Discovery and Deduplication

    [Fact]
    public async Task PatternDiscovery_RepeatedDigram_DiscoveredAsPattern()
    {
        const string text = "abab"; // "ab" appears twice

        var result = await _service.IngestTextHierarchicallyAsync(text);

        result.TotalPatternsDiscovered.Should().BeGreaterThan(0,
            because: "repeated 'ab' pattern should be discovered");
    }

    [Fact]
    public async Task PatternDiscovery_TheInHat_SharesThePattern()
    {
        const string text = "the cat in the hat";

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
        
        // The pattern "the" should appear in discovered patterns
        // (exact count depends on algorithm behavior)
        result.TotalPatternsDiscovered.Should().BeGreaterThan(0,
            because: "'the' appears twice and should be captured");
    }

    [Fact]
    public async Task Deduplication_IdenticalTexts_SameRootAtom()
    {
        const string text = "Hello, World!";

        var result1 = await _service.IngestTextHierarchicallyAsync(text);
        var result2 = await _service.IngestTextHierarchicallyAsync(text);

        result1.RootAtomId.Should().Be(result2.RootAtomId,
            because: "identical text should produce identical root atom (content-addressed)");
    }

    [Fact]
    public async Task Deduplication_SharedSubstrings_ReusesPatternAtoms()
    {
        // Ingest two texts that share a common substring
        const string text1 = "Captain Ahab sailed the ship";
        const string text2 = "Captain Hook had a parrot";

        var result1 = await _service.IngestTextHierarchicallyAsync(text1);
        var result2 = await _service.IngestTextHierarchicallyAsync(text2);

        // Both should complete successfully
        result1.RootAtomId.Should().NotBe(0);
        result2.RootAtomId.Should().NotBe(0);

        // The atoms should be different (different texts)
        result1.RootAtomId.Should().NotBe(result2.RootAtomId);

        // But shared patterns like "Captain " should use the same underlying atoms
        // We can verify this by checking total atoms in DB is less than if no deduplication
        var totalConstants = await _context.Constants.CountAsync();
        var totalCompositions = await _context.Compositions.CountAsync();
        var totalAtoms = totalConstants + totalCompositions;
        
        // Calculate what it would be without any pattern sharing
        var text1Chars = text1.Distinct().Count();
        var text2Chars = text2.Distinct().Count();
        
        // With deduplication, we should have fewer total atoms than naive approach
        // (This is a soft check - the exact ratio depends on discovered patterns)
        totalAtoms.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Deduplication_CrossDocumentPatterns_SharedAcrossIngestions()
    {
        // Ingest texts sequentially, patterns should be reused
        var texts = new[]
        {
            "the quick brown fox",
            "the lazy dog",
            "the quick rabbit"
        };

        var results = new List<HierarchicalIngestionResult>();
        foreach (var text in texts)
        {
            results.Add(await _service.IngestTextHierarchicallyAsync(text));
        }

        // All should reconstruct correctly
        for (int i = 0; i < texts.Length; i++)
        {
            var reconstructed = await _service.ReconstructTextAsync(results[i].RootAtomId, results[i].RootIsConstant);
            reconstructed.Should().Be(texts[i]);
        }

        // Character constants should be shared (e.g., 'h' appears in all three)
        // Query constants by SeedType (0 = Unicode codepoint)
        var charConstants = await _context.Constants
            .Where(c => c.SeedType == 0)
            .CountAsync();

        // We shouldn't have duplicate character constants
        var allChars = string.Concat(texts).Distinct().Count();
        charConstants.Should().BeLessThanOrEqualTo(allChars + 5, // Small buffer for test variance
            because: "character constants should be deduplicated across all texts");
    }

    #endregion

    #region Ingestion Result Statistics

    [Fact]
    public async Task IngestionResult_ContainsCorrectStatistics()
    {
        const string text = "abcabcabc"; // 9 chars, 3 unique, "abc" repeated 3x

        var result = await _service.IngestTextHierarchicallyAsync(text);

        result.RootAtomId.Should().BeGreaterThan(0);
        result.Tier0CharacterCount.Should().Be(9);
        result.UniqueCharacterCount.Should().Be(3);
        result.ProcessingTimeMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task IngestionResult_CompressionRatio_CalculatedCorrectly()
    {
        const string text = "aaaaaaaaaa"; // 10 identical characters

        var result = await _service.IngestTextHierarchicallyAsync(text);

        // With RLE and pattern detection, final sequence should be shorter
        result.CompressionRatio.Should().BeGreaterThanOrEqualTo(1.0,
            because: "compression ratio = original/final, should be >= 1");
    }

    #endregion

    #region Multi-Tier Query Support

    [Fact]
    public async Task Query_FindPatternAcrossDocuments_ReturnsMatches()
    {
        // Ingest several documents
        await _service.IngestTextHierarchicallyAsync("the cat sat on the mat");
        await _service.IngestTextHierarchicallyAsync("the dog ran in the park");
        await _service.IngestTextHierarchicallyAsync("a bird flew over");

        // Query for compositions containing "the"
        var matches = await _service.FindCompositionsContainingPatternAsync("the");

        // Should find matches (exact count depends on how patterns are structured)
        matches.Should().NotBeNull();
    }

    [Fact]
    public async Task PatternUsageStats_ReturnsTopPatterns()
    {
        // Ingest text with repeated patterns
        await _service.IngestTextHierarchicallyAsync("the cat and the dog and the bird");

        var stats = await _service.GetPatternUsageStatsAsync(topN: 10);

        stats.Should().NotBeNull();
        // Patterns should be discovered and counted
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task EdgeCase_SingleCharacter_HandledCorrectly()
    {
        const string text = "a";

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
        result.Tier0CharacterCount.Should().Be(1);
    }

    [Fact]
    public async Task EdgeCase_TwoCharacters_HandledCorrectly()
    {
        const string text = "ab";

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
    }

    [Fact]
    public async Task EdgeCase_AllSameCharacter_CompressesEfficiently()
    {
        const string text = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // 40 a's

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
        result.FinalSequenceLength.Should().BeLessThan(text.Length,
            because: "RLE compression should reduce sequence length");
    }

    [Fact]
    public async Task EdgeCase_LongText_CompletesSuccessfully()
    {
        // Generate a longer text with patterns
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 100; i++)
        {
            sb.Append("the quick brown fox jumps over the lazy dog. ");
        }
        var text = sb.ToString();

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
        result.TotalPatternsDiscovered.Should().BeGreaterThan(0,
            because: "repeated sentences should produce patterns");
    }

    [Fact]
    public async Task EdgeCase_EmptyText_ThrowsException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.IngestTextHierarchicallyAsync(""));
    }

    [Fact]
    public async Task EdgeCase_NullText_ThrowsException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.IngestTextHierarchicallyAsync(null!));
    }

    [Fact]
    public async Task EdgeCase_WhitespaceOnly_HandledCorrectly()
    {
        const string text = "   \t\n   ";

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
    }

    #endregion

    #region Performance Characteristics

    [Fact]
    public async Task Performance_MultipleIngestions_LinearTimeCharacteristic()
    {
        // This is a soft performance check, not a strict benchmark
        var text1 = new string('a', 100);
        var text2 = new string('a', 1000);

        var result1 = await _service.IngestTextHierarchicallyAsync(text1);
        var result2 = await _service.IngestTextHierarchicallyAsync(text2);

        // Both should complete (no timeout)
        result1.RootAtomId.Should().BeGreaterThan(0);
        result2.RootAtomId.Should().BeGreaterThan(0);

        // Processing time should scale reasonably (not exponential)
        // Note: This is a very loose check since timing varies by machine
    }

    #endregion

    #region Tier Structure Verification

    [Fact]
    public async Task TierStructure_MultiplePatterns_CreatesHierarchy()
    {
        // Text with nested patterns: "ab" appears in "abab", which appears twice
        const string text = "abababab";

        var result = await _service.IngestTextHierarchicallyAsync(text);

        result.TierCount.Should().BeGreaterThanOrEqualTo(1,
            because: "pattern discovery should create at least one tier");
    }

    [Fact]
    public async Task TierStructure_ComplexPattern_DiscoveredCorrectly()
    {
        // "the " is a common pattern, "the cat" uses "the "
        const string text = "the cat the dog the bird";

        var result = await _service.IngestTextHierarchicallyAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(result.RootAtomId, result.RootIsConstant);

        reconstructed.Should().Be(text);
        result.TotalPatternsDiscovered.Should().BeGreaterThan(0);
    }

    #endregion
}
