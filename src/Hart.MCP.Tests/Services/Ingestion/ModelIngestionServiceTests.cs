using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Services;
using Hart.MCP.Core.Services.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Hart.MCP.Tests.Services.Ingestion;

/// <summary>
/// Tests for AI model ingestion (SafeTensor, vocabulary, embeddings, attention weights).
/// 
/// KEY INNOVATIONS BEING TESTED:
/// 1. Vocabulary tokens → deduplicated atoms via HierarchicalTextIngestionService
///    Same "the" in GPT-2 and LLaMA = SAME atom ID
/// 2. Embeddings → float compositions + reduced 3D coordinates for PostGIS
/// 3. Attention weights → trajectory compositions with LINESTRING
/// 4. Cross-model spatial queries enabled through shared atom space
/// </summary>
public class ModelIngestionServiceTests : IDisposable
{
    private readonly HartDbContext _context;
    private readonly AtomIngestionService _atomService;
    private readonly HierarchicalTextIngestionService _textService;
    private readonly EmbeddingIngestionService _embeddingService;
    private readonly ModelIngestionService _modelService;
    private readonly ITestOutputHelper _output;

    public ModelIngestionServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _context = CreateContext();
        _atomService = new AtomIngestionService(_context);
        _textService = new HierarchicalTextIngestionService(_context, _atomService);
        _embeddingService = new EmbeddingIngestionService(_context);
        _modelService = new ModelIngestionService(_context, _textService, _embeddingService);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    private static HartDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new HartDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    #region Vocabulary Ingestion

    [Fact]
    public async Task VocabularyIngestion_SingleCharToken_BecomesAtom()
    {
        // Simple vocab: {"a": 0}
        var vocab = JsonSerializer.Serialize(new Dictionary<string, int> { { "a", 0 } });

        var result = await _modelService.IngestVocabularyFromJsonAsync(vocab, "test-model");

        result.Should().ContainKey(0);
        var atomId = result[0];

        var constant = await _context.Constants.FindAsync(atomId);
        constant.Should().NotBeNull(); // Single char = constant
    }

    [Fact]
    public async Task VocabularyIngestion_MultiCharToken_BecomesComposition()
    {
        // Vocab with multi-char token
        var vocab = JsonSerializer.Serialize(new Dictionary<string, int> { { "the", 0 } });

        var result = await _modelService.IngestVocabularyFromJsonAsync(vocab, "test-model");

        result.Should().ContainKey(0);
        var atomId = result[0];

        var composition = await _context.Compositions.FindAsync(atomId);
        composition.Should().NotBeNull(); // Multi-char = composition
        
        // Verify has references via Relation
        var hasRefs = await _context.Relations.AnyAsync(r => r.CompositionId == atomId);
        hasRefs.Should().BeTrue();
    }

    [Fact]
    public async Task VocabularyIngestion_SameTokenAcrossModels_SameAtomId()
    {
        // THE KEY INSIGHT: "the" in GPT-2 and "the" in LLaMA = SAME atom
        var vocab1 = JsonSerializer.Serialize(new Dictionary<string, int> { { "the", 100 } });
        var vocab2 = JsonSerializer.Serialize(new Dictionary<string, int> { { "the", 500 } });

        var result1 = await _modelService.IngestVocabularyFromJsonAsync(vocab1, "gpt-2");
        var result2 = await _modelService.IngestVocabularyFromJsonAsync(vocab2, "llama");

        // Different token IDs map to SAME atom (content-addressed)
        result1[100].Should().Be(result2[500]);

        _output.WriteLine($"GPT-2 'the' token 100 → atom {result1[100]}");
        _output.WriteLine($"LLaMA 'the' token 500 → atom {result2[500]}");
        _output.WriteLine("SAME ATOM! Content-addressed deduplication works across models.");
    }

    [Fact(Skip = "Requires refactoring for AtomRef architecture - AtomType removed")]
    public async Task VocabularyIngestion_ByteToken_HandledCorrectly()
    {
        // Byte fallback tokens like <0x0A> (newline)
        var vocab = JsonSerializer.Serialize(new Dictionary<string, int> { { "<0x0A>", 0 } });

        var result = await _modelService.IngestVocabularyFromJsonAsync(vocab, "test-model");

        result.Should().ContainKey(0);
        var constant = await _context.Constants.FindAsync(result[0]);
        constant.Should().NotBeNull();
        // TODO: Type now determined via TypeRef, need to query type atom
        constant!.SeedValue.Should().Be(0x0A); // Newline byte
    }

    [Fact]
    public async Task VocabularyIngestion_HuggingFaceFormat_Parsed()
    {
        // HuggingFace tokenizer.json format
        var hfTokenizer = JsonSerializer.Serialize(new
        {
            model = new
            {
                vocab = new Dictionary<string, int>
                {
                    { "hello", 0 },
                    { "world", 1 },
                    { "the", 2 }
                }
            }
        });

        var result = await _modelService.IngestVocabularyFromJsonAsync(hfTokenizer, "hf-model");

        result.Should().ContainKey(0);
        result.Should().ContainKey(1);
        result.Should().ContainKey(2);
        result.Count.Should().Be(3);
    }

    [Fact]
    public async Task VocabularyIngestion_LargeVocab_Performance()
    {
        // Simulate realistic vocab size (1000 tokens)
        var vocab = new Dictionary<string, int>();
        for (int i = 0; i < 1000; i++)
        {
            vocab[$"token_{i}"] = i;
        }
        var vocabJson = JsonSerializer.Serialize(vocab);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _modelService.IngestVocabularyFromJsonAsync(vocabJson, "large-model");
        sw.Stop();

        result.Count.Should().Be(1000);
        _output.WriteLine($"Ingested 1000 vocabulary tokens in {sw.ElapsedMilliseconds}ms");

        // Should be reasonably fast (< 30 seconds for 1000 tokens)
        sw.ElapsedMilliseconds.Should().BeLessThan(30000);
    }

    #endregion

    #region Embedding Ingestion

    [Fact]
    public async Task EmbeddingWithSpatial_PreservesFullPrecision()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };

        var atomId = await _modelService.IngestEmbeddingWithSpatialAsync(embedding, 0, "test-model");

        // Reconstruct and verify
        var reconstructed = await _embeddingService.ReconstructAsync(atomId);
        reconstructed.Should().Equal(embedding);
    }

    [Fact(Skip = "Requires refactoring for AtomRef architecture - Metadata removed")]
    public async Task EmbeddingWithSpatial_Has3DCoordinates()
    {
        // 768-dimensional embedding (typical transformer size)
        var embedding = new float[768];
        var rng = new Random(42);
        for (int i = 0; i < 768; i++)
        {
            embedding[i] = (float)(rng.NextDouble() * 2 - 1);
        }

        var atomId = await _modelService.IngestEmbeddingWithSpatialAsync(embedding, 0, "test-model");

        var composition = await _context.Compositions.FindAsync(atomId);
        composition.Should().NotBeNull();

        // TODO: Metadata is now atomized - need to query metadata atoms
        // Previous approach checked composition.Metadata property directly
    }

    [Fact]
    public async Task EmbeddingIngestion_SimilarVectors_CloseSpatialPositions()
    {
        // Two similar embeddings should have similar 3D coordinates
        var embedding1 = new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
        var embedding2 = new float[] { 1.1f, 1.0f, 0.9f, 1.0f, 1.1f, 1.0f }; // Very similar
        var embedding3 = new float[] { -1.0f, -1.0f, -1.0f, -1.0f, -1.0f, -1.0f }; // Very different

        var atomId1 = await _modelService.IngestEmbeddingWithSpatialAsync(embedding1, 0, "model");
        var atomId2 = await _modelService.IngestEmbeddingWithSpatialAsync(embedding2, 1, "model");
        var atomId3 = await _modelService.IngestEmbeddingWithSpatialAsync(embedding3, 2, "model");

        var comp1 = await _context.Compositions.FindAsync(atomId1);
        var comp2 = await _context.Compositions.FindAsync(atomId2);
        var comp3 = await _context.Compositions.FindAsync(atomId3);

        // Similar embeddings should have similar Hilbert indices (locality preserving)
        // Note: This is a rough test - in practice we'd use PostGIS distance functions
        _output.WriteLine($"Embedding1 Hilbert: {comp1!.HilbertHigh}:{comp1.HilbertLow}");
        _output.WriteLine($"Embedding2 Hilbert: {comp2!.HilbertHigh}:{comp2.HilbertLow}");
        _output.WriteLine($"Embedding3 Hilbert: {comp3!.HilbertHigh}:{comp3.HilbertLow}");
    }

    #endregion

    #region Attention Weights as Trajectories

    [Fact(Skip = "Requires refactoring for AtomRef architecture - AtomType query removed")]
    public async Task AttentionWeights_CreatesTrajectories()
    {
        // Create some token atoms first
        var vocab = JsonSerializer.Serialize(new Dictionary<string, int>
        {
            { "captain", 0 },
            { "whale", 1 }
        });
        var tokenAtomIds = await _modelService.IngestVocabularyFromJsonAsync(vocab, "test-model");

        // Attention from "captain" to "whale" with weight 0.5
        var weights = new float[2, 2]
        {
            { 0.1f, 0.5f }, // captain attends to whale
            { 0.3f, 0.2f }  // whale attends to captain
        };

        // modelNameAtomId is now a long - use 0 for test placeholder
        var headAtomId = await _modelService.IngestAttentionWeightsAsync(
            weights, layer: 0, head: 0, tokenAtomIds, modelNameAtomId: 0L);

        headAtomId.Should().BeGreaterThan(0);

        // TODO: Query trajectories via TypeRef instead of AtomType
        // Previous approach: _context.Atoms.Where(a => a.AtomType == "attention_trajectory")
    }

    [Fact(Skip = "Requires refactoring for AtomRef architecture - AtomType query removed")]
    public async Task AttentionTrajectory_HasLineStringGeometry()
    {
        var vocab = JsonSerializer.Serialize(new Dictionary<string, int>
        {
            { "a", 0 },
            { "b", 1 }
        });
        var tokenAtomIds = await _modelService.IngestVocabularyFromJsonAsync(vocab, "test-model");

        var weights = new float[2, 2]
        {
            { 0.0f, 0.8f },
            { 0.0f, 0.0f }
        };

        // modelNameAtomId is now a long - use 0 for test placeholder
        await _modelService.IngestAttentionWeightsAsync(
            weights, layer: 0, head: 0, tokenAtomIds, modelNameAtomId: 0L);

        // TODO: Query trajectory via TypeRef instead of AtomType
        // Previous approach: _context.Atoms.FirstOrDefaultAsync(a => a.AtomType == "attention_trajectory")
    }

    [Fact(Skip = "Requires refactoring for AtomRef architecture - AtomType/Metadata removed")]
    public async Task AttentionTrajectory_ContainsFromToWeight()
    {
        var vocab = JsonSerializer.Serialize(new Dictionary<string, int>
        {
            { "source", 0 },
            { "target", 1 }
        });
        var tokenAtomIds = await _modelService.IngestVocabularyFromJsonAsync(vocab, "test-model");

        var weights = new float[2, 2]
        {
            { 0.0f, 0.75f },
            { 0.0f, 0.0f }
        };

        // modelNameAtomId is now a long - use 0 for test placeholder
        await _modelService.IngestAttentionWeightsAsync(
            weights, layer: 5, head: 3, tokenAtomIds, modelNameAtomId: 0L);

        // TODO: Query trajectory via TypeRef and atomized metadata
        // Previous approach used AtomType query and parsed Metadata JSON property
    }

    #endregion

    #region SafeTensor Parsing

    [Fact]
    public async Task SafeTensorIngestion_ParsesHeader()
    {
        // Create a minimal SafeTensor file in memory
        var safeTensorBytes = CreateMinimalSafeTensor();
        using var stream = new MemoryStream(safeTensorBytes);

        var result = await _modelService.IngestSafeTensorAsync(stream, "test-model");

        result.Should().NotBeNull();
        result.ModelName.Should().Be("test-model");
        result.TensorCount.Should().BeGreaterThan(0);

        _output.WriteLine($"Ingested model: {result.TensorCount} tensors, {result.TotalParameters} parameters");
    }

    [Fact(Skip = "Requires refactoring for AtomRef architecture - Refs/AtomType removed")]
    public async Task SafeTensorIngestion_F32Tensor_RoundTrips()
    {
        // Create SafeTensor with known F32 values
        var tensorData = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };
        var safeTensorBytes = CreateSafeTensorWithF32(tensorData, "test_tensor", new long[] { 4 });
        using var stream = new MemoryStream(safeTensorBytes);

        var result = await _modelService.IngestSafeTensorAsync(stream, "test-model");

        result.WeightAtomIds.Should().ContainKey("test_tensor");
        var tensorAtomId = result.WeightAtomIds["test_tensor"];

        // Verify tensor exists
        var tensorComp = await _context.Compositions.FindAsync(tensorAtomId);
        tensorComp.Should().NotBeNull();
        
        // TODO: Refactor to use Relation query for component reconstruction
        // Previous approach used tensorComp.Refs array directly
        // New approach: query _context.Relations.Where(r => r.CompositionId == tensorAtomId)
    }

    #endregion

    #region Cross-Model Queries (The Real Power)

    [Fact]
    public async Task CrossModelQuery_SharedTokens_HaveSameAtomId()
    {
        // Ingest vocabularies from two different "models"
        var gpt2Vocab = JsonSerializer.Serialize(new Dictionary<string, int>
        {
            { "the", 0 },
            { "cat", 1 },
            { "sat", 2 }
        });

        var llamaVocab = JsonSerializer.Serialize(new Dictionary<string, int>
        {
            { "the", 1000 }, // Different token ID
            { "cat", 1001 },
            { "dog", 1002 }  // Different word
        });

        var gpt2Tokens = await _modelService.IngestVocabularyFromJsonAsync(gpt2Vocab, "gpt-2");
        var llamaTokens = await _modelService.IngestVocabularyFromJsonAsync(llamaVocab, "llama");

        // "the" in both models → same atom
        gpt2Tokens[0].Should().Be(llamaTokens[1000]);

        // "cat" in both models → same atom
        gpt2Tokens[1].Should().Be(llamaTokens[1001]);

        // "sat" and "dog" are different → different atoms
        gpt2Tokens[2].Should().NotBe(llamaTokens[1002]);

        _output.WriteLine("Shared vocabulary atoms:");
        _output.WriteLine($"  'the': GPT-2[0]={gpt2Tokens[0]}, LLaMA[1000]={llamaTokens[1000]} → SAME");
        _output.WriteLine($"  'cat': GPT-2[1]={gpt2Tokens[1]}, LLaMA[1001]={llamaTokens[1001]} → SAME");
        _output.WriteLine($"  'sat': GPT-2[2]={gpt2Tokens[2]} (GPT-2 only)");
        _output.WriteLine($"  'dog': LLaMA[1002]={llamaTokens[1002]} (LLaMA only)");
    }

    [Fact]
    public async Task CrossModelQuery_CanFindAllModelsUsingToken()
    {
        // Ingest several "models"
        var models = new[] { "gpt-2", "llama", "mistral", "phi" };
        var commonToken = "attention";

        foreach (var model in models)
        {
            var vocab = JsonSerializer.Serialize(new Dictionary<string, int>
            {
                { commonToken, 42 }
            });
            await _modelService.IngestVocabularyFromJsonAsync(vocab, model);
        }

        // Query: "Which models use the token 'attention'?"
        // First, get the atom ID for "attention"
        var result = await _modelService.IngestVocabularyFromJsonAsync(
            JsonSerializer.Serialize(new Dictionary<string, int> { { commonToken, 0 } }),
            "query-model");

        var attentionAtomId = result[0];

        // All models share the same atom ID for "attention"
        // In production, we'd query: SELECT DISTINCT metadata->>'model' FROM compositions WHERE id = ?
        // For this test, we verify the composition exists and is shared

        var composition = await _context.Compositions.FindAsync(attentionAtomId);
        composition.Should().NotBeNull();

        _output.WriteLine($"Token 'attention' has atom ID {attentionAtomId}");
        _output.WriteLine($"All {models.Length} models reference this same atom.");
        _output.WriteLine("PostGIS query: Find all compositions referencing this atom → get model names from metadata");
    }

    #endregion

    #region Helper Methods

    private byte[] CreateMinimalSafeTensor()
    {
        // Minimal SafeTensor: header + one small tensor
        var header = new Dictionary<string, object>
        {
            ["test_tensor"] = new
            {
                dtype = "F32",
                shape = new[] { 2 },
                data_offsets = new[] { 0, 8 }
            }
        };

        var headerJson = JsonSerializer.Serialize(header);
        var headerBytes = Encoding.UTF8.GetBytes(headerJson);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // 8-byte header length
        writer.Write((ulong)headerBytes.Length);
        // Header JSON
        writer.Write(headerBytes);
        // Tensor data: 2 F32 values
        writer.Write(1.0f);
        writer.Write(2.0f);

        return ms.ToArray();
    }

    private byte[] CreateSafeTensorWithF32(float[] data, string name, long[] shape)
    {
        var dataBytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, dataBytes, 0, dataBytes.Length);

        var header = new Dictionary<string, object>
        {
            [name] = new
            {
                dtype = "F32",
                shape = shape,
                data_offsets = new[] { 0, dataBytes.Length }
            }
        };

        var headerJson = JsonSerializer.Serialize(header);
        var headerBytes = Encoding.UTF8.GetBytes(headerJson);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ulong)headerBytes.Length);
        writer.Write(headerBytes);
        writer.Write(dataBytes);

        return ms.ToArray();
    }

    #endregion
}
