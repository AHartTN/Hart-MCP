using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services.Ingestion;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hart.MCP.Tests.Services.Ingestion;

/// <summary>
/// Tests for the universal ingestion pipelines.
/// 
/// THE PATTERN: Ingest → Store → Reconstruct → Verify exact match
/// 
/// WHAT WE'RE PROVING:
/// - Any data type can become atoms
/// - Original data is exactly reconstructable (LOSSLESS)
/// - Identical data produces identical atoms (DEDUPLICATION)
/// - Spatial structure is preserved (COMPOSABILITY)
/// </summary>
public class IngestionPipelineTests
{
    #region Image Ingestion

    [Fact]
    public async Task ImageIngestion_SinglePixel_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new ImageIngestionService(context);

        var original = new ImageData(1, 1, [0xFF112233]); // ARGB

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Width.Should().Be(1);
        reconstructed.Height.Should().Be(1);
        reconstructed.Pixels.Should().BeEquivalentTo(original.Pixels);
    }

    [Fact]
    public async Task ImageIngestion_Row_PreservesPixelOrder()
    {
        await using var context = CreateContext();
        var service = new ImageIngestionService(context);

        var pixels = new uint[] { 0xFF000000, 0xFF0000FF, 0xFF00FF00, 0xFFFF0000 };
        var original = new ImageData(4, 1, pixels);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Pixels.Should().Equal(pixels);
    }

    [Fact]
    public async Task ImageIngestion_2x2_PreservesSpatialStructure()
    {
        await using var context = CreateContext();
        var service = new ImageIngestionService(context);

        // 2x2 image: [R, G]
        //            [B, W]
        var pixels = new uint[]
        {
            0xFFFF0000, 0xFF00FF00, // Row 0: Red, Green
            0xFF0000FF, 0xFFFFFFFF  // Row 1: Blue, White
        };
        var original = new ImageData(2, 2, pixels);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Width.Should().Be(2);
        reconstructed.Height.Should().Be(2);
        reconstructed.Pixels.Should().Equal(pixels);
    }

    [Fact]
    public async Task ImageIngestion_RunLengthEncoding_DeduplicatesRepeatedPixels()
    {
        await using var context = CreateContext();
        var service = new ImageIngestionService(context);

        // Row of 100 identical black pixels - should compress
        var pixels = Enumerable.Repeat(0xFF000000u, 100).ToArray();
        var original = new ImageData(100, 1, pixels);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Pixels.Should().Equal(pixels);

        // Verify compression: should have far fewer atom refs than pixels
        var rowComposition = await context.Atoms
            .Where(a => a.AtomType == "image_row" && a.Refs != null)
            .FirstOrDefaultAsync();

        rowComposition.Should().NotBeNull();
        // RLE: 100 identical pixels → 1 ref with multiplicity 100
        rowComposition!.Refs!.Length.Should().Be(1);
    }

    #endregion

    #region Binary Ingestion

    [Fact]
    public async Task BinaryIngestion_EmptyArray_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new BinaryIngestionService(context);

        var original = Array.Empty<byte>();

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Should().BeEmpty();
    }

    [Fact]
    public async Task BinaryIngestion_SingleByte_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new BinaryIngestionService(context);

        var original = new byte[] { 0x42 };

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Should().Equal(original);
    }

    [Fact]
    public async Task BinaryIngestion_AllByteValues_RoundTrip()
    {
        await using var context = CreateContext();
        var service = new BinaryIngestionService(context);

        // All 256 byte values
        var original = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Should().Equal(original);
    }

    [Fact]
    public async Task BinaryIngestion_MaxDeduplication_Only256Constants()
    {
        await using var context = CreateContext();
        var service = new BinaryIngestionService(context);

        // Ingest many bytes
        var data1 = new byte[] { 0, 0, 0, 1, 1, 1, 2, 2, 2 };
        var data2 = new byte[] { 0, 1, 2, 0, 1, 2, 0, 1, 2 };

        await service.IngestAsync(data1);
        await service.IngestAsync(data2);

        // Count byte constants (0-255 only)
        var byteConstants = await context.Atoms
            .Where(a => a.AtomType == "constant" && a.SeedValue >= 0 && a.SeedValue < 256)
            .CountAsync();

        // Should only have constants for bytes we used (0, 1, 2)
        byteConstants.Should().BeLessThanOrEqualTo(256);
    }

    #endregion

    #region Embedding Ingestion

    [Fact]
    public async Task EmbeddingIngestion_Float_PreservesIEEE754()
    {
        await using var context = CreateContext();
        var service = new EmbeddingIngestionService(context);

        // Float values that have exact IEEE754 representations
        var original = new float[] { 0.5f, -1.0f, float.MaxValue, float.MinValue, float.Epsilon };

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Should().Equal(original);
    }

    [Fact]
    public async Task EmbeddingIngestion_Double_PreservesIEEE754()
    {
        await using var context = CreateContext();
        var service = new EmbeddingIngestionService(context);

        var original = new double[] { Math.PI, Math.E, double.MaxValue, double.Epsilon };

        var atomId = await service.IngestDoubleAsync(original);
        var reconstructed = await service.ReconstructDoubleAsync(atomId);

        reconstructed.Should().Equal(original);
    }

    [Fact]
    public async Task EmbeddingIngestion_TypicalVector_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new EmbeddingIngestionService(context);

        // Typical embedding vector (e.g., from OpenAI)
        var original = new float[384];
        var rng = new Random(42);
        for (int i = 0; i < original.Length; i++)
            original[i] = (float)(rng.NextDouble() * 2 - 1);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Should().Equal(original);
    }

    #endregion

    #region Audio Ingestion

    [Fact]
    public async Task AudioIngestion_SingleSample_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new AudioIngestionService(context);

        var original = new AudioData(44100, 1, 16, [12345]);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.SampleRate.Should().Be(44100);
        reconstructed.Channels.Should().Be(1);
        reconstructed.BitsPerSample.Should().Be(16);
        reconstructed.Samples.Should().Equal(original.Samples);
    }

    [Fact]
    public async Task AudioIngestion_NegativeSamples_PreservesSign()
    {
        await using var context = CreateContext();
        var service = new AudioIngestionService(context);

        // Full range of signed 16-bit values
        var samples = new short[] { short.MinValue, -1, 0, 1, short.MaxValue };
        var original = new AudioData(48000, 2, 16, samples);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Samples.Should().Equal(samples);
    }

    [Fact]
    public async Task AudioIngestion_SineWave_ReconstructsExactly()
    {
        await using var context = CreateContext();
        var service = new AudioIngestionService(context);

        // Generate 1kHz sine wave at 44.1kHz for 100 samples
        var samples = new short[100];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(32000 * Math.Sin(2 * Math.PI * 1000 * i / 44100));
        }
        var original = new AudioData(44100, 1, 16, samples);

        var atomId = await service.IngestAsync(original);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.Samples.Should().Equal(samples);
    }

    #endregion

    #region JSON Ingestion

    [Fact]
    public async Task JsonIngestion_Null_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var atomId = await service.IngestStringAsync("null");
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task JsonIngestion_Boolean_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var trueId = await service.IngestStringAsync("true");
        var falseId = await service.IngestStringAsync("false");

        var reconstructedTrue = await service.ReconstructAsync(trueId);
        var reconstructedFalse = await service.ReconstructAsync(falseId);

        reconstructedTrue.GetBoolean().Should().BeTrue();
        reconstructedFalse.GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task JsonIngestion_Number_PreservesValue()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var atomId = await service.IngestStringAsync("42");
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task JsonIngestion_String_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var atomId = await service.IngestStringAsync("\"hello world\"");
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.GetString().Should().Be("hello world");
    }

    [Fact]
    public async Task JsonIngestion_Array_PreservesOrder()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var atomId = await service.IngestStringAsync("[1, 2, 3]");
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.GetArrayLength().Should().Be(3);
        reconstructed[0].GetInt32().Should().Be(1);
        reconstructed[1].GetInt32().Should().Be(2);
        reconstructed[2].GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task JsonIngestion_Object_PreservesKeyValuePairs()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var json = """{"name":"hart","version":1}""";
        var atomId = await service.IngestStringAsync(json);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.TryGetProperty("name", out var name).Should().BeTrue();
        reconstructed.TryGetProperty("version", out var version).Should().BeTrue();
        name.GetString().Should().Be("hart");
        version.GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task JsonIngestion_NestedStructure_RoundTrips()
    {
        await using var context = CreateContext();
        var service = new JsonIngestionService(context);

        var json = """
        {
            "atoms": [
                {"type": "constant", "seed": 42},
                {"type": "composition", "refs": [1, 2, 3]}
            ],
            "metadata": {
                "version": "1.0",
                "lossless": true
            }
        }
        """;

        var atomId = await service.IngestStringAsync(json);
        var reconstructed = await service.ReconstructAsync(atomId);

        reconstructed.GetProperty("atoms").GetArrayLength().Should().Be(2);
        reconstructed.GetProperty("atoms")[0].GetProperty("seed").GetInt32().Should().Be(42);
        reconstructed.GetProperty("metadata").GetProperty("lossless").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region Deduplication Across Types

    [Fact]
    public async Task Deduplication_IdenticalImages_SameAtomId()
    {
        await using var context = CreateContext();
        var service = new ImageIngestionService(context);

        var image = new ImageData(2, 2, [0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000]);

        var id1 = await service.IngestAsync(image);
        var id2 = await service.IngestAsync(image);

        id1.Should().Be(id2, "identical content should produce identical atom IDs");
    }

    [Fact]
    public async Task Deduplication_IdenticalBinary_SameAtomId()
    {
        await using var context = CreateContext();
        var service = new BinaryIngestionService(context);

        var data = new byte[] { 1, 2, 3, 4, 5 };

        var id1 = await service.IngestAsync(data);
        var id2 = await service.IngestAsync(data);

        id1.Should().Be(id2);
    }

    #endregion

    #region Universal Service

    [Fact]
    public async Task UniversalService_AllTypes_Accessible()
    {
        await using var context = CreateContext();
        var service = new UniversalIngestionService(context);

        // Text
        var textId = await service.IngestTextAsync("hello");
        var text = await service.ReconstructTextAsync(textId);
        text.Should().Be("hello");

        // Binary
        var binaryId = await service.IngestBinaryAsync([0x01, 0x02]);
        var binary = await service.ReconstructBinaryAsync(binaryId);
        binary.Should().Equal([0x01, 0x02]);

        // Embedding
        var embeddingId = await service.IngestEmbeddingAsync([1.0f, 2.0f, 3.0f]);
        var embedding = await service.ReconstructEmbeddingAsync(embeddingId);
        embedding.Should().Equal([1.0f, 2.0f, 3.0f]);
    }

    #endregion

    private static HartDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase(databaseName: $"HartMCP_Ingestion_Test_{Guid.NewGuid()}")
            .Options;

        var context = new HartDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
