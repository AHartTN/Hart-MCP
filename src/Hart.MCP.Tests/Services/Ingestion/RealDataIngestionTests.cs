using FluentAssertions;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Services;
using Hart.MCP.Core.Services.Ingestion;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Hart.MCP.Tests.Services.Ingestion;

/// <summary>
/// Integration tests using REAL data files from test-data/
/// 
/// These tests prove the ingestion pipelines work on actual files:
/// - Real PNG images with real pixel data
/// - Real WAV audio with real PCM samples
/// - Real text (Moby Dick) with real Unicode
/// 
/// No synthetic data. No shortcuts. Real files, real round-trips.
/// </summary>
public class RealDataIngestionTests : IDisposable
{
    private const string TEST_DATA_PATH = @"D:\Repositories\Hart-MCP\test-data";
    private readonly HartDbContext _context;

    public RealDataIngestionTests()
    {
        var options = new DbContextOptionsBuilder<HartDbContext>()
            .UseInMemoryDatabase($"RealDataTest_{Guid.NewGuid()}")
            .Options;
        _context = new HartDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region Real Image Tests

    [Theory]
    [InlineData("test_64x64.png")]
    [InlineData("test_128x128.png")]
    [InlineData("test_256x256.png")]
    [InlineData("test_pattern.png")]
    [InlineData("mass_checkerboard_64x64.png")]
    [InlineData("mass_gradient_64x64.png")]
    [InlineData("mass_noise_64x64.png")]
    [InlineData("mass_radial_64x64.png")]
    public async Task Image_RealPNG_LosslessRoundTrip(string filename)
    {
        var path = Path.Combine(TEST_DATA_PATH, filename);
        if (!File.Exists(path))
        {
            // Skip if file doesn't exist (CI environment)
            return;
        }

        // Load real PNG using ImageSharp
        using var image = await Image.LoadAsync<Rgba32>(path);
        var width = image.Width;
        var height = image.Height;

        // Extract actual pixel data
        var pixels = new uint[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    var p = row[x];
                    pixels[y * width + x] = ((uint)p.A << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                }
            }
        });

        var original = new ImageData(width, height, pixels);

        // Ingest
        var service = new ImageIngestionService(_context);
        var atomId = await service.IngestAsync(original);

        // Reconstruct
        var reconstructed = await service.ReconstructAsync(atomId);

        // Verify EXACT match
        reconstructed.Width.Should().Be(width, $"Width mismatch for {filename}");
        reconstructed.Height.Should().Be(height, $"Height mismatch for {filename}");
        reconstructed.Pixels.Should().Equal(pixels, $"Pixel data mismatch for {filename}");
    }

    [Fact]
    public async Task Image_AllTestImages_Deduplication()
    {
        var service = new ImageIngestionService(_context);
        var imagePaths = Directory.GetFiles(TEST_DATA_PATH, "*.png")
            .Where(p => !p.Contains("recon_") && !p.Contains("parallel_"))
            .Take(5) // Limit for speed
            .ToList();

        if (imagePaths.Count == 0) return;

        var atomIds = new List<long>();
        foreach (var path in imagePaths)
        {
            using var image = await Image.LoadAsync<Rgba32>(path);
            var pixels = ExtractPixels(image);
            var data = new ImageData(image.Width, image.Height, pixels);
            var atomId = await service.IngestAsync(data);
            atomIds.Add(atomId);
        }

        // Ingest same images again
        var atomIds2 = new List<long>();
        foreach (var path in imagePaths)
        {
            using var image = await Image.LoadAsync<Rgba32>(path);
            var pixels = ExtractPixels(image);
            var data = new ImageData(image.Width, image.Height, pixels);
            var atomId = await service.IngestAsync(data);
            atomIds2.Add(atomId);
        }

        // Should get IDENTICAL atom IDs (deduplication)
        atomIds.Should().Equal(atomIds2, "Identical images must produce identical atom IDs");
    }

    #endregion

    #region Real Audio Tests

    [Fact]
    public async Task Audio_RealWAV_LosslessRoundTrip()
    {
        var path = Path.Combine(TEST_DATA_PATH, "test_tone.wav");
        if (!File.Exists(path)) return;

        var wavBytes = await File.ReadAllBytesAsync(path);

        // Parse WAV header
        if (wavBytes.Length < 44) return;
        if (wavBytes[0] != 'R' || wavBytes[1] != 'I' || wavBytes[2] != 'F' || wavBytes[3] != 'F') return;

        var channels = BitConverter.ToInt16(wavBytes, 22);
        var sampleRate = BitConverter.ToInt32(wavBytes, 24);
        var bitsPerSample = BitConverter.ToInt16(wavBytes, 34);

        // Find data chunk
        int dataOffset = 44;
        int dataSize = wavBytes.Length - 44;

        // Parse 16-bit PCM samples
        var samples = new short[dataSize / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(wavBytes, dataOffset + i * 2);
        }

        var original = new AudioData(sampleRate, channels, bitsPerSample, samples);

        // Ingest
        var service = new AudioIngestionService(_context);
        var atomId = await service.IngestAsync(original);

        // Reconstruct
        var reconstructed = await service.ReconstructAsync(atomId);

        // Verify EXACT match
        reconstructed.SampleRate.Should().Be(sampleRate);
        reconstructed.Channels.Should().Be(channels);
        reconstructed.BitsPerSample.Should().Be(bitsPerSample);
        reconstructed.Samples.Should().Equal(samples, "Audio samples must match exactly");
    }

    #endregion

    #region Real Text Tests

    [Fact]
    public async Task Text_MobyDick_LosslessRoundTrip()
    {
        var path = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (!File.Exists(path)) return;

        var original = await File.ReadAllTextAsync(path);

        // Ingest
        var service = new AtomIngestionService(_context);
        var atomId = await service.IngestTextAsync(original);

        // Reconstruct
        var reconstructed = await service.ReconstructTextAsync(atomId);

        // Verify EXACT match - every character
        reconstructed.Should().Be(original, "Text must match exactly, character for character");
    }

    [Fact]
    public async Task Text_MobyDick_FirstChapter_Deduplication()
    {
        var path = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (!File.Exists(path)) return;

        var fullText = await File.ReadAllTextAsync(path);
        
        // Find first chapter
        var ch1Start = fullText.IndexOf("CHAPTER 1.");
        var ch2Start = fullText.IndexOf("CHAPTER 2.");
        if (ch1Start < 0 || ch2Start < 0) return;

        var chapter1 = fullText.Substring(ch1Start, ch2Start - ch1Start);

        var service = new AtomIngestionService(_context);

        // Ingest chapter twice
        var atomId1 = await service.IngestTextAsync(chapter1);
        var atomId2 = await service.IngestTextAsync(chapter1);

        // Must be identical
        atomId1.Should().Be(atomId2, "Identical text must produce identical atom ID");
    }

    #endregion

    #region Binary Tests

    [Theory]
    [InlineData("test_64x64.png")]
    [InlineData("test_tone.wav")]
    public async Task Binary_RealFiles_LosslessRoundTrip(string filename)
    {
        var path = Path.Combine(TEST_DATA_PATH, filename);
        if (!File.Exists(path)) return;

        var original = await File.ReadAllBytesAsync(path);

        // Ingest as binary
        var service = new BinaryIngestionService(_context);
        var atomId = await service.IngestAsync(original);

        // Reconstruct
        var reconstructed = await service.ReconstructAsync(atomId);

        // Verify EXACT byte-for-byte match
        reconstructed.Should().Equal(original, $"Binary data for {filename} must match exactly");
    }

    #endregion

    #region Universal Service Tests

    [Fact]
    public async Task Universal_IngestFile_DetectsTypes()
    {
        var service = new UniversalIngestionService(_context);

        // Text file
        var txtPath = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (File.Exists(txtPath))
        {
            var atomId = await service.IngestFileAsync(txtPath);
            atomId.Should().BeGreaterThan(0);

            var reconstructed = await service.ReconstructTextAsync(atomId);
            var original = await File.ReadAllTextAsync(txtPath);
            reconstructed.Should().Be(original);
        }

        // WAV file
        var wavPath = Path.Combine(TEST_DATA_PATH, "test_tone.wav");
        if (File.Exists(wavPath))
        {
            var atomId = await service.IngestFileAsync(wavPath);
            atomId.Should().BeGreaterThan(0);

            var reconstructed = await service.ReconstructAudioAsync(atomId);
            reconstructed.Should().NotBeNull();
            reconstructed.Samples.Length.Should().BeGreaterThan(0);
        }
    }

    #endregion

    #region Hierarchical Text Ingestion Performance Tests

    /// <summary>
    /// Moby Dick hierarchical ingestion MUST complete in under 15 seconds.
    /// Uses Sequitur-style grammar induction with O(1) linked list operations.
    /// </summary>
    [Fact]
    public async Task Hierarchical_MobyDick_PerformanceUnder15Seconds()
    {
        var path = Path.Combine(TEST_DATA_PATH, "moby_dick.txt");
        if (!File.Exists(path)) return;

        var text = await File.ReadAllTextAsync(path);
        
        var atomService = new AtomIngestionService(_context);
        var service = new HierarchicalTextIngestionService(_context, atomService);
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.IngestTextHierarchicallyAsync(text);
        stopwatch.Stop();

        // CRITICAL: Must complete in under 15 seconds
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(15000,
            $"Moby Dick ingestion took {stopwatch.ElapsedMilliseconds}ms, must be <15000ms");

        // Verify lossless reconstruction
        var reconstructed = await service.ReconstructTextAsync(result.RootAtomId);
        reconstructed.Should().Be(text, "Hierarchical ingestion must be lossless");

        // Log stats
        result.TotalPatternsDiscovered.Should().BeGreaterThan(0);
        result.CompressionRatio.Should().BeGreaterThan(1.0,
            "Hierarchical compression should reduce sequence length");
    }

    /// <summary>
    /// Hierarchical ingestion of short patterns should be very fast.
    /// </summary>
    [Theory]
    [InlineData("the cat in the hat", 500)]
    [InlineData("abcabcabcabc", 500)]
    [InlineData("Mississippi River", 500)]
    public async Task Hierarchical_ShortPatterns_Fast(string text, int maxMs)
    {
        var atomService = new AtomIngestionService(_context);
        var service = new HierarchicalTextIngestionService(_context, atomService);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.IngestTextHierarchicallyAsync(text);
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(maxMs);

        // Verify lossless
        var reconstructed = await service.ReconstructTextAsync(result.RootAtomId);
        reconstructed.Should().Be(text);
    }

    #endregion

    #region Helpers

    private static uint[] ExtractPixels(Image<Rgba32> image)
    {
        var pixels = new uint[image.Width * image.Height];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < image.Width; x++)
                {
                    var p = row[x];
                    pixels[y * image.Width + x] = ((uint)p.A << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                }
            }
        });
        return pixels;
    }

    #endregion
}
