using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Hart.MCP.Tests;

/// <summary>
/// Tests for AtomIngestionService.
/// 
/// CORE GUARANTEES TESTED:
/// 1. Lossless reconstruction: any ingested content can be perfectly reconstructed
/// 2. Content-addressing: identical content produces identical atom IDs (deduplication)
/// 3. Determinism: same input always produces same output
/// 4. Unicode correctness: all valid Unicode is handled correctly
/// 
/// BEHAVIORAL EXPECTATIONS:
/// - Text reconstruction is byte-for-byte identical to input
/// - Deduplication works at both character and composition levels
/// - Edge cases (empty, null, surrogates) are handled correctly
/// </summary>
public class AtomIngestionServiceTests : IDisposable
{
    private readonly HartDbContext _context;
    private readonly AtomIngestionService _service;

    public AtomIngestionServiceTests()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase(databaseName: $"HartMCP_Test_{Guid.NewGuid()}")
            .Options;

        _context = new HartDbContext(options);
        var logger = Mock.Of<ILogger<AtomIngestionService>>();
        _service = new AtomIngestionService(_context, logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region Core Guarantee: Lossless Reconstruction

    /// <summary>
    /// THE MOST CRITICAL INVARIANT: Text reconstruction must be exactly lossless.
    /// This is the fundamental guarantee of the spatial knowledge substrate.
    /// </summary>
    [Fact]
    public async Task LosslessReconstruction_SimpleText_ByteIdentical()
    {
        const string original = "Hello, World!";

        var compositionId = await _service.IngestTextAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);

        reconstructed.Should().Be(original, 
            because: "text reconstruction MUST be exactly lossless - this is the core guarantee");
    }

    [Theory]
    [InlineData("a", "single character")]
    [InlineData("Hello", "simple word")]
    [InlineData("Hello, World!", "with punctuation")]
    [InlineData("The quick brown fox jumps over the lazy dog.", "pangram")]
    [InlineData("   spaces   ", "whitespace handling")]
    [InlineData("\t\n\r", "control characters")]
    public async Task LosslessReconstruction_VariousTexts_AllBytesPreserved(string original, string description)
    {
        var compositionId = await _service.IngestTextAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);

        reconstructed.Should().Be(original, 
            because: $"{description} must reconstruct exactly");
    }

    [Theory]
    [InlineData("日本語", "Japanese")]
    [InlineData("한국어", "Korean")]
    [InlineData("العربية", "Arabic (RTL)")]
    [InlineData("Привет", "Russian/Cyrillic")]
    [InlineData("Ελληνικά", "Greek")]
    [InlineData("עברית", "Hebrew")]
    public async Task LosslessReconstruction_NonLatinScripts_ByteIdentical(string original, string script)
    {
        var compositionId = await _service.IngestTextAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);

        reconstructed.Should().Be(original, 
            because: $"{script} script must be perfectly preserved");
    }

    [Fact]
    public async Task LosslessReconstruction_EmojiAndSurrogates_HandledCorrectly()
    {
        // Emoji are represented as surrogate pairs in UTF-16
        const string original = "Hello 😀🌍🚀 World";

        var compositionId = await _service.IngestTextAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);

        reconstructed.Should().Be(original, 
            because: "emoji (surrogate pairs) must be correctly handled");
        
        // Verify correct codepoint count
        var originalCodepoints = original.EnumerateRunes().Count();
        var reconstructedCodepoints = reconstructed.EnumerateRunes().Count();
        reconstructedCodepoints.Should().Be(originalCodepoints,
            because: "codepoint count must be preserved");
    }

    [Fact]
    public async Task LosslessReconstruction_MixedContent_ComplexCase()
    {
        // Mix of ASCII, Unicode, emoji, whitespace, numbers
        const string original = "User123 said: \"日本語は beautiful!\" 🎉\n\tTab here.";

        var compositionId = await _service.IngestTextAsync(original);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);

        reconstructed.Should().Be(original);
    }

    #endregion

    #region Core Guarantee: Content-Addressing (Deduplication)

    /// <summary>
    /// Content-addressing means identical content → identical ID.
    /// This is the foundation of deduplication and structural sharing.
    /// </summary>
    [Fact]
    public async Task ContentAddressing_IdenticalText_SameCompositionId()
    {
        const string text = "Duplicate me!";

        var id1 = await _service.IngestTextAsync(text);
        var id2 = await _service.IngestTextAsync(text);
        var id3 = await _service.IngestTextAsync(text);

        id1.Should().Be(id2, because: "identical content must produce identical ID");
        id2.Should().Be(id3);
    }

    [Fact]
    public async Task ContentAddressing_IdenticalCharacter_SingleAtom()
    {
        const uint codepoint = 'A';

        var id1 = await _service.GetOrCreateConstantAsync(codepoint);
        var id2 = await _service.GetOrCreateConstantAsync(codepoint);
        
        id1.Should().Be(id2, because: "same codepoint must resolve to same atom");
        
        var atomCount = await _context.Atoms.CountAsync(a => a.IsConstant && a.SeedValue == codepoint);
        atomCount.Should().Be(1, because: "only one atom should exist for each unique character");
    }

    [Fact]
    public async Task ContentAddressing_SharedCharacters_StructuralSharing()
    {
        // "AA" and "AB" both use 'A' - they should share that atom
        await _service.IngestTextAsync("AA");
        await _service.IngestTextAsync("AB");
        
        var aAtomCount = await _context.Atoms.CountAsync(a => a.IsConstant && a.SeedValue == 'A');
        aAtomCount.Should().Be(1, because: "'A' should be shared between compositions");
    }

    [Fact]
    public async Task ContentAddressing_DifferentText_DifferentCompositionIds()
    {
        var id1 = await _service.IngestTextAsync("Hello");
        var id2 = await _service.IngestTextAsync("World");

        id1.Should().NotBe(id2, because: "different content must produce different IDs");
    }

    #endregion

    #region Core Guarantee: Determinism

    [Fact]
    public async Task Determinism_SameInput_IdenticalAtomProperties()
    {
        const string text = "Test determinism";
        
        var id1 = await _service.IngestTextAsync(text);
        var atom1 = await _context.Atoms.AsNoTracking().FirstAsync(a => a.Id == id1);
        
        // Clear and re-ingest
        _context.ChangeTracker.Clear();
        
        var id2 = await _service.IngestTextAsync(text);
        var atom2 = await _context.Atoms.AsNoTracking().FirstAsync(a => a.Id == id2);
        
        id1.Should().Be(id2);
        atom1.ContentHash.Should().Equal(atom2.ContentHash, 
            because: "content hash must be deterministic");
    }

    #endregion

    #region Compression: Run-Length Encoding

    [Fact]
    public async Task RLECompression_ConsecutiveRepeats_CompressedCorrectly()
    {
        const string text = "aaa";
        
        var compositionId = await _service.IngestTextAsync(text);
        var composition = await _context.Atoms.FindAsync(compositionId);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);
        
        // Verify lossless first (most important)
        reconstructed.Should().Be(text);
        
        // Verify compression occurred (fewer refs than characters)
        composition!.Refs.Should().HaveCountLessThan(text.Length,
            because: "RLE should compress consecutive identical characters");
    }

    [Fact]
    public async Task RLECompression_NoRepeats_NoCompression()
    {
        const string text = "abc";
        
        var compositionId = await _service.IngestTextAsync(text);
        var composition = await _context.Atoms.FindAsync(compositionId);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);
        
        reconstructed.Should().Be(text);
        composition!.Refs.Should().HaveCount(text.Length,
            because: "no consecutive repeats means no RLE compression");
    }

    [Fact]
    public async Task RLECompression_MixedPattern_CorrectReconstruction()
    {
        const string text = "aaabbbcccabc";
        
        var compositionId = await _service.IngestTextAsync(text);
        var reconstructed = await _service.ReconstructTextAsync(compositionId);
        
        reconstructed.Should().Be(text,
            because: "mixed patterns must still reconstruct exactly");
    }

    #endregion

    #region Atom Properties

    [Fact]
    public async Task AtomProperties_Constant_CorrectlyMarked()
    {
        const uint codepoint = 'X';
        
        var id = await _service.GetOrCreateConstantAsync(codepoint);
        var atom = await _context.Atoms.FindAsync(id);
        
        atom.Should().NotBeNull();
        atom!.IsConstant.Should().BeTrue(because: "character atoms are constants");
        atom.SeedValue.Should().Be(codepoint);
        atom.SeedType.Should().Be(0, because: "0 = SEED_TYPE_UNICODE");
        atom.AtomType.Should().Be("char");
        atom.Refs.Should().BeNull(because: "constants have no references");
        atom.ContentHash.Should().HaveCount(32, because: "BLAKE3 produces 32-byte hashes");
        atom.Geom.Should().NotBeNull(because: "all atoms have spatial coordinates");
    }

    [Fact]
    public async Task AtomProperties_Composition_CorrectlyMarked()
    {
        var refId = await _service.GetOrCreateConstantAsync('A');
        
        var id = await _service.CreateCompositionAsync([refId], [1], "test");
        var atom = await _context.Atoms.FindAsync(id);
        
        atom.Should().NotBeNull();
        atom!.IsConstant.Should().BeFalse(because: "compositions are not constants");
        atom.SeedValue.Should().BeNull();
        atom.AtomType.Should().Be("test");
        atom.Refs.Should().NotBeNull(because: "compositions reference other atoms");
        atom.Multiplicities.Should().NotBeNull();
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task ErrorHandling_EmptyString_ThrowsArgumentException()
    {
        var action = () => _service.IngestTextAsync("");
        
        await action.Should().ThrowAsync<ArgumentException>(
            because: "empty string has no content to ingest");
    }

    [Fact]
    public async Task ErrorHandling_NullString_ThrowsArgumentException()
    {
        var action = () => _service.IngestTextAsync(null!);
        
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ErrorHandling_EmptyRefs_ThrowsArgumentException()
    {
        var action = () => _service.CreateCompositionAsync([], []);
        
        await action.Should().ThrowAsync<ArgumentException>(
            because: "compositions must reference at least one atom");
    }

    [Fact]
    public async Task ErrorHandling_MismatchedArrays_ThrowsArgumentException()
    {
        var action = () => _service.CreateCompositionAsync([1, 2], [1]);
        
        await action.Should().ThrowAsync<ArgumentException>(
            because: "refs and multiplicities must have equal length");
    }

    [Fact]
    public async Task ErrorHandling_NonExistentId_ThrowsInvalidOperationException()
    {
        var action = () => _service.ReconstructTextAsync(999999);
        
        await action.Should().ThrowAsync<InvalidOperationException>(
            because: "cannot reconstruct non-existent composition");
    }

    #endregion
}
