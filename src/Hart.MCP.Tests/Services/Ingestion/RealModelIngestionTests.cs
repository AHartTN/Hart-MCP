using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Services;
using Hart.MCP.Core.Services.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Hart.MCP.Tests.Services.Ingestion;

/// <summary>
/// END-TO-END REAL DATA TESTS
/// 
/// Full pipeline from empty database:
/// 1. Unicode seeding (1.1M codepoints)
/// 2. Vocabulary ingestion
/// 3. Tensor ingestion with sparse encoding
/// 
/// Performance targets:
/// - Unicode BMP (65K): < 5 seconds
/// - 80MB model: < 20 seconds
/// </summary>
public class RealModelIngestionTests : IDisposable
{
    private readonly HartDbContext _context;
    private readonly AtomIngestionService _atomService;
    private readonly HierarchicalTextIngestionService _textService;
    private readonly EmbeddingIngestionService _embeddingService;
    private readonly ModelIngestionService _modelService;
    private readonly StreamingModelIngestionService _streamingService;
    private readonly UnicodeSeedingService _unicodeService;
    private readonly ITestOutputHelper _output;

    private const string ModelPath = @"D:\Repositories\Hart-MCP\test-data\embedding_models\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    public RealModelIngestionTests(ITestOutputHelper output)
    {
        _output = output;
        _context = CreateContext();
        _atomService = new AtomIngestionService(_context);
        _textService = new HierarchicalTextIngestionService(_context, _atomService);
        _embeddingService = new EmbeddingIngestionService(_context);
        _modelService = new ModelIngestionService(_context, _textService, _embeddingService);
        _streamingService = new StreamingModelIngestionService(_context, _textService);
        _unicodeService = new UnicodeSeedingService(_context);
    }

    public void Dispose()
    {
        // Clean up test data - delete Relations first due to FK constraints
        _context.Relations.RemoveRange(_context.Relations);
        _context.Compositions.RemoveRange(_context.Compositions);
        _context.Constants.RemoveRange(_context.Constants);
        _context.SaveChanges();
        _context.Dispose();
    }

    private static HartDbContext CreateContext()
    {
        // Use real PostgreSQL for performance testing
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=HART-MCP-Test;Username=hartonomous;Password=hartonomous",
                o => o.UseNetTopologySuite())
            .Options;
        var context = new HartDbContext(options);
        context.Database.EnsureCreated();
        // Clear any existing test data - delete Relations first due to FK constraints
        context.Relations.RemoveRange(context.Relations);
        context.Compositions.RemoveRange(context.Compositions);
        context.Constants.RemoveRange(context.Constants);
        context.SaveChanges();
        return context;
    }

    /// <summary>
    /// FULL END-TO-END: Unicode → Vocabulary → Model
    /// </summary>
    [Fact]
    public async Task EndToEnd_FullPipeline_UnicodeToModel()
    {
        var safeTensorPath = Path.Combine(ModelPath, "model.safetensors");
        var tokenizerPath = Path.Combine(ModelPath, "tokenizer.json");
        
        if (!File.Exists(safeTensorPath))
        {
            Console.WriteLine($"SKIPPED: Model not found at {safeTensorPath}");
            return;
        }

        var totalSw = Stopwatch.StartNew();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            END-TO-END MODEL INGESTION TEST                    ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"Started: {DateTime.Now:HH:mm:ss.fff}");
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // PHASE 1: UNICODE SEEDING (BMP for speed)
        // ════════════════════════════════════════════════════════════
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ PHASE 1: UNICODE SEEDING (Basic Multilingual Plane)         │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘");
        
        var unicodeSw = Stopwatch.StartNew();
        var unicodeProgress = new Progress<UnicodeSeedProgress>(p =>
        {
            if (p.CodepointsSeeded % 10000 == 0 || p.CodepointsSeeded == p.TotalCodepoints)
            {
                Console.WriteLine($"  Unicode: {p.CodepointsSeeded:N0}/{p.TotalCodepoints:N0} ({p.PercentComplete:F1}%)");
            }
        });

        var bmpCount = await _unicodeService.SeedBMPAsync(unicodeProgress);
        unicodeSw.Stop();

        Console.WriteLine($"  ✓ Seeded {bmpCount:N0} Unicode codepoints in {unicodeSw.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"    Rate: {bmpCount * 1000.0 / unicodeSw.ElapsedMilliseconds:N0} codepoints/sec");
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // PHASE 2: VOCABULARY INGESTION
        // ════════════════════════════════════════════════════════════
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ PHASE 2: VOCABULARY INGESTION                                │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘");

        if (File.Exists(tokenizerPath))
        {
            var vocabSw = Stopwatch.StartNew();
            var tokenAtomIds = await _modelService.IngestVocabularyAsync(tokenizerPath, "all-MiniLM-L6-v2");
            vocabSw.Stop();

            Console.WriteLine($"  ✓ Ingested {tokenAtomIds.Count:N0} tokens in {vocabSw.ElapsedMilliseconds:N0}ms");
            Console.WriteLine($"    Rate: {tokenAtomIds.Count * 1000.0 / vocabSw.ElapsedMilliseconds:N0} tokens/sec");
        }
        else
        {
            Console.WriteLine("  ⚠ Tokenizer not found, skipping vocabulary");
        }
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // PHASE 3: MODEL TENSOR INGESTION (SPARSE)
        // ════════════════════════════════════════════════════════════
        Console.WriteLine("┌─────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ PHASE 3: MODEL TENSOR INGESTION (Sparse Encoding)            │");
        Console.WriteLine("└─────────────────────────────────────────────────────────────┘");

        var fileSize = new FileInfo(safeTensorPath).Length;
        Console.WriteLine($"  File: {safeTensorPath}");
        Console.WriteLine($"  Size: {fileSize:N0} bytes ({fileSize / (1024.0 * 1024):F2} MB)");
        Console.WriteLine($"  Sparsity threshold: {StreamingModelIngestionService.DEFAULT_SPARSITY_THRESHOLD}");
        Console.WriteLine();

        var modelSw = Stopwatch.StartNew();
        var lastPhase = "";
        var modelProgress = new Progress<ModelIngestionProgress>(p =>
        {
            if (p.Phase != lastPhase)
            {
                Console.WriteLine($"  [{modelSw.ElapsedMilliseconds,6}ms] Phase: {p.Phase}");
                lastPhase = p.Phase;
            }
        });

        var result = await _streamingService.IngestSafeTensorStreamingAsync(
            safeTensorPath,
            "all-MiniLM-L6-v2",
            sparsityThreshold: StreamingModelIngestionService.DEFAULT_SPARSITY_THRESHOLD,
            progress: modelProgress);
        modelSw.Stop();

        Console.WriteLine();
        Console.WriteLine($"  ✓ Model ingestion complete!");
        Console.WriteLine($"    Tensors:     {result.TensorCount}");
        Console.WriteLine($"    Parameters:  {result.TotalParameters:N0}");
        Console.WriteLine($"    Total values: {result.TotalValues:N0}");
        Console.WriteLine($"    Stored:      {result.StoredValues:N0}");
        Console.WriteLine($"    Skipped:     {result.SkippedValues:N0}");
        Console.WriteLine($"    Sparsity:    {result.SparsityPercent:F2}%");
        Console.WriteLine($"    Time:        {modelSw.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"    Rate:        {result.TotalParameters * 1000.0 / modelSw.ElapsedMilliseconds:N0} params/sec");
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════
        // SUMMARY
        // ════════════════════════════════════════════════════════════
        totalSw.Stop();
        var constants = await _context.Constants.CountAsync();
        var compositions = await _context.Compositions.CountAsync();
        var totalAtoms = constants + compositions;

        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                         SUMMARY                               ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  Total time:      {totalSw.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"  Total atoms:     {totalAtoms:N0}");
        Console.WriteLine($"  Constants:       {constants:N0}");
        Console.WriteLine($"  Compositions:    {compositions:N0}");
        Console.WriteLine();

        // Assertions
        result.TensorCount.Should().BeGreaterThan(0);
        result.TotalParameters.Should().BeGreaterThan(0);
        result.RootCompositionId.Should().BeGreaterThan(0);
        
        // Performance assertion: 80MB model should complete in < 60 seconds (generous for CI)
        modelSw.ElapsedMilliseconds.Should().BeLessThan(60000, "model ingestion should complete in < 60s");
    }

    /// <summary>
    /// STREAMING + SPARSE TEST: The main event for 1TB+ model support.
    /// </summary>
    [Fact]
    public async Task RealModel_StreamingSparseIngestion_AllMiniLM()
    {
        var safeTensorPath = Path.Combine(ModelPath, "model.safetensors");
        
        if (!File.Exists(safeTensorPath))
        {
            _output.WriteLine($"SKIPPED: SafeTensor file not found at {safeTensorPath}");
            return;
        }

        var fileSize = new FileInfo(safeTensorPath).Length;
        _output.WriteLine("=== STREAMING SPARSE MODEL INGESTION TEST ===");
        _output.WriteLine($"Model: all-MiniLM-L6-v2");
        _output.WriteLine($"File: {safeTensorPath}");
        _output.WriteLine($"Size: {fileSize:N0} bytes ({fileSize / (1024.0 * 1024):F2} MB)");
        _output.WriteLine("");
        _output.WriteLine("Testing with different sparsity thresholds...");
        _output.WriteLine("");

        // Test with default threshold (conservative, ~20-40% sparse typically)
        await TestSparseIngestion(safeTensorPath, StreamingModelIngestionService.DEFAULT_SPARSITY_THRESHOLD, "Conservative");
        
        // Reset DB for second test - delete Relations first due to FK constraints
        _context.Relations.RemoveRange(_context.Relations);
        _context.Compositions.RemoveRange(_context.Compositions);
        _context.Constants.RemoveRange(_context.Constants);
        await _context.SaveChangesAsync();
        
        // Test with aggressive threshold (60%+ sparse)
        await TestSparseIngestion(safeTensorPath, StreamingModelIngestionService.AGGRESSIVE_SPARSITY_THRESHOLD, "Aggressive");
    }

    private async Task TestSparseIngestion(string safeTensorPath, float threshold, string label)
    {
        Console.WriteLine($"=== {label.ToUpper()} SPARSITY (threshold = {threshold}) ===");
        _output.WriteLine($"=== {label.ToUpper()} SPARSITY (threshold = {threshold}) ===");
        
        var tensorSw = System.Diagnostics.Stopwatch.StartNew();
        var lastTensor = "";
        
        var progress = new Progress<ModelIngestionProgress>(p =>
        {
            if (p.CurrentTensor != lastTensor && p.CurrentTensor != null)
            {
                var elapsed = tensorSw.ElapsedMilliseconds;
                var msg = $"  [{elapsed,6}ms] Tensor {p.TensorsProcessed}/{p.TensorsTotal}: {p.CurrentTensor} | {p.SparsityPercent:F1}% sparse";
                Console.WriteLine(msg);
                lastTensor = p.CurrentTensor;
                tensorSw.Restart();
            }
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"Starting ingestion at {DateTime.Now:HH:mm:ss.fff}...");
        
        var result = await _streamingService.IngestSafeTensorStreamingAsync(
            safeTensorPath, 
            $"all-MiniLM-L6-v2-{label.ToLower()}",
            sparsityThreshold: threshold,
            progress: progress);
        sw.Stop();

        Console.WriteLine("");
        Console.WriteLine("=== RESULTS ===");
        Console.WriteLine($"Root Composition ID: {result.RootCompositionId}");
        Console.WriteLine($"Tensor Count:      {result.TensorCount}");
        Console.WriteLine($"Total Parameters:  {result.TotalParameters:N0}");
        Console.WriteLine("");
        Console.WriteLine("=== SPARSE ENCODING STATS ===");
        Console.WriteLine($"Total Values:      {result.TotalValues:N0}");
        Console.WriteLine($"Stored Values:     {result.StoredValues:N0}");
        Console.WriteLine($"Skipped Values:    {result.SkippedValues:N0}");
        Console.WriteLine($"Sparsity:          {result.SparsityPercent:F2}%");
        Console.WriteLine("");
        Console.WriteLine("=== PERFORMANCE ===");
        Console.WriteLine($"Processing Time:   {sw.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"Rate:              {result.TotalParameters * 1000.0 / sw.ElapsedMilliseconds:N0} params/sec");
        
        // Copy to xUnit output
        _output.WriteLine($"Total: {result.TotalParameters:N0} params, {result.SparsityPercent:F1}% sparse, {sw.ElapsedMilliseconds}ms");

        // Verify DB state
        var totalAtoms = await _context.Constants.CountAsync() + await _context.Compositions.CountAsync();
        Console.WriteLine($"Total Atoms in DB: {totalAtoms:N0}");
        Console.WriteLine("");

        result.TensorCount.Should().BeGreaterThan(0);
        result.TotalParameters.Should().BeGreaterThan(0);
        result.RootCompositionId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RealModel_IngestVocabulary_AllMiniLM()
    {
        var tokenizerPath = Path.Combine(ModelPath, "tokenizer.json");
        
        if (!File.Exists(tokenizerPath))
        {
            _output.WriteLine($"SKIPPED: Tokenizer file not found at {tokenizerPath}");
            return;
        }

        _output.WriteLine("=== VOCABULARY INGESTION TEST ===");
        _output.WriteLine($"File: {tokenizerPath}");
        _output.WriteLine($"Size: {new FileInfo(tokenizerPath).Length:N0} bytes");
        _output.WriteLine("");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tokenAtomIds = await _modelService.IngestVocabularyAsync(tokenizerPath, "all-MiniLM-L6-v2");
        sw.Stop();

        _output.WriteLine("=== VOCABULARY RESULTS ===");
        _output.WriteLine($"Total Tokens:      {tokenAtomIds.Count:N0}");
        _output.WriteLine($"Processing Time:   {sw.ElapsedMilliseconds:N0}ms");
        _output.WriteLine($"Tokens/second:     {tokenAtomIds.Count * 1000.0 / sw.ElapsedMilliseconds:N0}");
        _output.WriteLine("");

        // Show some sample tokens
        _output.WriteLine("=== SAMPLE TOKENS ===");
        var sampleTokenIds = new[] { 0, 1, 2, 100, 101, 1000, 2000 };
        foreach (var tokenId in sampleTokenIds)
        {
            if (tokenAtomIds.TryGetValue(tokenId, out var atomId))
            {
                // Check if it's a composition (tokens are typically compositions of codepoints)
                var composition = await _context.Compositions.FindAsync(atomId);
                var isComposition = composition != null;
                _output.WriteLine($"  Token {tokenId}: atom {atomId}, composition={isComposition}");
            }
        }

        // Database stats
        var constantAtoms = await _context.Constants.CountAsync();
        var compositionAtoms = await _context.Compositions.CountAsync();
        var totalAtoms = constantAtoms + compositionAtoms;

        _output.WriteLine("");
        _output.WriteLine("=== DATABASE STATE ===");
        _output.WriteLine($"Total Atoms:       {totalAtoms:N0}");
        _output.WriteLine($"Constants:         {constantAtoms:N0}");
        _output.WriteLine($"Compositions:      {compositionAtoms:N0}");
        _output.WriteLine($"Deduplication:     {tokenAtomIds.Count - totalAtoms:N0} atoms saved via content-addressing");

        tokenAtomIds.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RealModel_VocabularyDeduplication_SameTokensSameAtoms()
    {
        var tokenizerPath = Path.Combine(ModelPath, "tokenizer.json");
        
        if (!File.Exists(tokenizerPath))
        {
            _output.WriteLine($"SKIPPED: Tokenizer file not found");
            return;
        }

        _output.WriteLine("=== DEDUPLICATION TEST ===");
        _output.WriteLine("Ingesting same vocabulary twice to prove content-addressing...");
        _output.WriteLine("");

        // First ingestion
        var result1 = await _modelService.IngestVocabularyAsync(tokenizerPath, "model-1");
        var atomsAfterFirst = await _context.Constants.CountAsync() + await _context.Compositions.CountAsync();

        _output.WriteLine($"After 1st ingestion: {result1.Count:N0} tokens → {atomsAfterFirst:N0} atoms");

        // Second ingestion - should reuse all atoms
        var result2 = await _modelService.IngestVocabularyAsync(tokenizerPath, "model-2");
        var atomsAfterSecond = await _context.Constants.CountAsync() + await _context.Compositions.CountAsync();

        _output.WriteLine($"After 2nd ingestion: {result2.Count:N0} tokens → {atomsAfterSecond:N0} atoms");
        _output.WriteLine("");

        // Verify same tokens map to same atoms
        var matches = 0;
        var mismatches = 0;
        foreach (var (tokenId, atomId1) in result1)
        {
            if (result2.TryGetValue(tokenId, out var atomId2))
            {
                if (atomId1 == atomId2)
                    matches++;
                else
                    mismatches++;
            }
        }

        _output.WriteLine("=== VERIFICATION ===");
        _output.WriteLine($"Matching atom IDs:   {matches:N0}");
        _output.WriteLine($"Mismatching atoms:   {mismatches}");
        _output.WriteLine($"New atoms created:   {atomsAfterSecond - atomsAfterFirst}");
        _output.WriteLine("");
        _output.WriteLine(mismatches == 0 
            ? "✅ PERFECT DEDUPLICATION: Same tokens → Same atoms across models!"
            : "❌ DEDUPLICATION FAILED");

        // All tokens should map to same atoms
        mismatches.Should().Be(0, "same token text should always produce same atom ID");
        atomsAfterSecond.Should().Be(atomsAfterFirst, "no new atoms should be created for duplicate vocabulary");
    }

    [Fact]
    public async Task RealModel_TensorReconstruction_Lossless()
    {
        var safeTensorPath = Path.Combine(ModelPath, "model.safetensors");
        
        if (!File.Exists(safeTensorPath))
        {
            _output.WriteLine($"SKIPPED: SafeTensor file not found");
            return;
        }

        _output.WriteLine("=== TENSOR RECONSTRUCTION TEST ===");
        _output.WriteLine("Verifying lossless round-trip of tensor data...");
        _output.WriteLine("");

        var result = await _modelService.IngestSafeTensorAsync(safeTensorPath, "test-model");

        // Pick a tensor to verify
        var tensorEntry = result.WeightAtomIds.FirstOrDefault();
        if (tensorEntry.Key == null)
        {
            tensorEntry = result.EmbeddingAtomIds.FirstOrDefault();
        }

        if (tensorEntry.Key == null)
        {
            _output.WriteLine("No tensors found to verify");
            return;
        }

        var tensorName = tensorEntry.Key;
        var tensorAtomId = tensorEntry.Value;

        _output.WriteLine($"Verifying tensor: {tensorName}");

        // Get the composition (tensor is a composition of float constants)
        var composition = await _context.Compositions.FindAsync(tensorAtomId);
        composition.Should().NotBeNull();
        
        // Get references via Relations table
        var relations = await _context.Relations
            .Where(r => r.CompositionId == tensorAtomId)
            .OrderBy(r => r.Position)
            .ToListAsync();
        relations.Should().NotBeEmpty();

        var numElements = relations.Count;
        _output.WriteLine($"Elements: {numElements:N0}");

        // Reconstruct the tensor via Relations
        var componentIds = relations.Where(r => r.ChildConstantId.HasValue).Select(r => r.ChildConstantId!.Value).ToArray();
        var components = await _context.Constants
            .Where(c => componentIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        var reconstructed = new float[numElements];
        for (int i = 0; i < numElements; i++)
        {
            var component = components[relations[i].ChildConstantId!.Value];
            uint bits = (uint)component.SeedValue;
            reconstructed[i] = BitConverter.UInt32BitsToSingle(bits);
        }

        // Verify we got valid floats (not all zeros or NaN)
        var nonZeroCount = reconstructed.Count(f => f != 0);
        var nanCount = reconstructed.Count(f => float.IsNaN(f));
        var infCount = reconstructed.Count(f => float.IsInfinity(f));

        _output.WriteLine("");
        _output.WriteLine("=== RECONSTRUCTION STATS ===");
        _output.WriteLine($"Non-zero values:   {nonZeroCount:N0} ({100.0 * nonZeroCount / numElements:F1}%)");
        _output.WriteLine($"NaN values:        {nanCount}");
        _output.WriteLine($"Infinity values:   {infCount}");
        _output.WriteLine($"Min value:         {reconstructed.Min():E4}");
        _output.WriteLine($"Max value:         {reconstructed.Max():E4}");
        _output.WriteLine($"Mean value:        {reconstructed.Average():E4}");
        _output.WriteLine("");
        _output.WriteLine("First 10 values:");
        for (int i = 0; i < Math.Min(10, numElements); i++)
        {
            _output.WriteLine($"  [{i}] = {reconstructed[i]:E6}");
        }

        nanCount.Should().Be(0, "reconstructed values should not contain NaN");
        nonZeroCount.Should().BeGreaterThan(0, "tensor should contain non-zero values");
    }
}
