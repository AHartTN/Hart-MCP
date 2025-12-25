using Hart.MCP.Core.Data;
using Microsoft.Extensions.Logging;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Unified ingestion facade providing access to all ingestion pipelines.
/// 
/// THE VISION:
/// Every piece of data - text, images, audio, video, embeddings, JSON,
/// binary, code, models - becomes atoms in the spatial knowledge substrate.
/// 
/// UNIVERSAL PROPERTIES:
/// 1. LOSSLESS: Original data exactly reconstructable
/// 2. CONTENT-ADDRESSED: Identical data → identical atoms (deduplication)
/// 3. DETERMINISTIC: Same input always produces same output
/// 4. SPATIAL: Similar content → similar hypersphere positions
/// 5. COMPOSABLE: Everything is atoms referencing atoms
/// </summary>
public class UniversalIngestionService
{
    private readonly HartDbContext _context;
    private readonly ILogger<UniversalIngestionService>? _logger;

    // Lazy-initialized services
    private readonly Lazy<AtomIngestionService> _text;
    private readonly Lazy<ImageIngestionService> _image;
    private readonly Lazy<AudioIngestionService> _audio;
    private readonly Lazy<VideoIngestionService> _video;
    private readonly Lazy<BinaryIngestionService> _binary;
    private readonly Lazy<EmbeddingIngestionService> _embedding;
    private readonly Lazy<JsonIngestionService> _json;

    public UniversalIngestionService(
        HartDbContext context,
        ILoggerFactory? loggerFactory = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = loggerFactory?.CreateLogger<UniversalIngestionService>();

        _text = new Lazy<AtomIngestionService>(() =>
            new AtomIngestionService(context, loggerFactory?.CreateLogger<AtomIngestionService>()));
        
        _image = new Lazy<ImageIngestionService>(() =>
            new ImageIngestionService(context, loggerFactory?.CreateLogger<ImageIngestionService>()));
        
        _audio = new Lazy<AudioIngestionService>(() =>
            new AudioIngestionService(context, loggerFactory?.CreateLogger<AudioIngestionService>()));
        
        _video = new Lazy<VideoIngestionService>(() =>
            new VideoIngestionService(context, loggerFactory?.CreateLogger<VideoIngestionService>()));
        
        _binary = new Lazy<BinaryIngestionService>(() =>
            new BinaryIngestionService(context, loggerFactory?.CreateLogger<BinaryIngestionService>()));
        
        _embedding = new Lazy<EmbeddingIngestionService>(() =>
            new EmbeddingIngestionService(context, loggerFactory?.CreateLogger<EmbeddingIngestionService>()));
        
        _json = new Lazy<JsonIngestionService>(() =>
            new JsonIngestionService(context, loggerFactory?.CreateLogger<JsonIngestionService>()));
    }

    #region Text

    /// <summary>Ingest UTF-8 text → atoms</summary>
    public Task<long> IngestTextAsync(string text, CancellationToken ct = default)
        => _text.Value.IngestTextAsync(text, ct);

    /// <summary>Reconstruct text from atoms</summary>
    public Task<string> ReconstructTextAsync(long atomId, CancellationToken ct = default)
        => _text.Value.ReconstructTextAsync(atomId, ct);

    #endregion

    #region Image

    /// <summary>Ingest ARGB image → atoms</summary>
    public Task<long> IngestImageAsync(int width, int height, uint[] pixels, CancellationToken ct = default)
        => _image.Value.IngestAsync(new ImageData(width, height, pixels), ct);

    /// <summary>Ingest ImageData → atoms</summary>
    public Task<long> IngestImageAsync(ImageData image, CancellationToken ct = default)
        => _image.Value.IngestAsync(image, ct);

    /// <summary>Reconstruct image from atoms</summary>
    public Task<ImageData> ReconstructImageAsync(long atomId, CancellationToken ct = default)
        => _image.Value.ReconstructAsync(atomId, ct);

    #endregion

    #region Audio

    /// <summary>Ingest PCM audio → atoms</summary>
    public Task<long> IngestAudioAsync(int sampleRate, int channels, int bitsPerSample, short[] samples, CancellationToken ct = default)
        => _audio.Value.IngestAsync(new AudioData(sampleRate, channels, bitsPerSample, samples), ct);

    /// <summary>Ingest AudioData → atoms</summary>
    public Task<long> IngestAudioAsync(AudioData audio, CancellationToken ct = default)
        => _audio.Value.IngestAsync(audio, ct);

    /// <summary>Reconstruct audio from atoms</summary>
    public Task<AudioData> ReconstructAudioAsync(long atomId, CancellationToken ct = default)
        => _audio.Value.ReconstructAsync(atomId, ct);

    #endregion

    #region Video

    /// <summary>Ingest video frames → atoms</summary>
    public Task<long> IngestVideoAsync(VideoData video, CancellationToken ct = default)
        => _video.Value.IngestAsync(video, ct);

    /// <summary>Reconstruct video from atoms</summary>
    public Task<VideoData> ReconstructVideoAsync(long atomId, CancellationToken ct = default)
        => _video.Value.ReconstructAsync(atomId, ct);

    #endregion

    #region Binary

    /// <summary>Ingest raw bytes → atoms</summary>
    public Task<long> IngestBinaryAsync(byte[] data, CancellationToken ct = default)
        => _binary.Value.IngestAsync(data, ct);

    /// <summary>Reconstruct bytes from atoms</summary>
    public Task<byte[]> ReconstructBinaryAsync(long atomId, CancellationToken ct = default)
        => _binary.Value.ReconstructAsync(atomId, ct);

    #endregion

    #region Embedding

    /// <summary>Ingest float embedding → atoms</summary>
    public Task<long> IngestEmbeddingAsync(float[] embedding, CancellationToken ct = default)
        => _embedding.Value.IngestAsync(embedding, ct);

    /// <summary>Ingest double embedding → atoms</summary>
    public Task<long> IngestEmbeddingAsync(double[] embedding, CancellationToken ct = default)
        => _embedding.Value.IngestDoubleAsync(embedding, ct);

    /// <summary>Reconstruct float embedding from atoms</summary>
    public Task<float[]> ReconstructEmbeddingAsync(long atomId, CancellationToken ct = default)
        => _embedding.Value.ReconstructAsync(atomId, ct);

    /// <summary>Reconstruct double embedding from atoms</summary>
    public Task<double[]> ReconstructEmbeddingDoubleAsync(long atomId, CancellationToken ct = default)
        => _embedding.Value.ReconstructDoubleAsync(atomId, ct);

    #endregion

    #region JSON

    /// <summary>Ingest JSON string → atoms</summary>
    public Task<long> IngestJsonAsync(string jsonString, CancellationToken ct = default)
        => _json.Value.IngestStringAsync(jsonString, ct);

    /// <summary>Ingest JsonElement → atoms</summary>
    public Task<long> IngestJsonAsync(System.Text.Json.JsonElement json, CancellationToken ct = default)
        => _json.Value.IngestAsync(json, ct);

    /// <summary>Reconstruct JSON from atoms</summary>
    public Task<System.Text.Json.JsonElement> ReconstructJsonAsync(long atomId, CancellationToken ct = default)
        => _json.Value.ReconstructAsync(atomId, ct);

    #endregion

    #region File Helpers

    /// <summary>Ingest file by detecting type from extension</summary>
    public async Task<long> IngestFileAsync(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var data = await File.ReadAllBytesAsync(path, ct);

        return ext switch
        {
            ".txt" or ".md" or ".cs" or ".py" or ".js" or ".ts" or ".html" or ".css" or ".xml" =>
                await IngestTextAsync(System.Text.Encoding.UTF8.GetString(data), ct),
            
            ".json" =>
                await IngestJsonAsync(System.Text.Encoding.UTF8.GetString(data), ct),
            
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" =>
                await IngestImageFromBytesAsync(data, ct),
            
            ".wav" =>
                await IngestWavFromBytesAsync(data, ct),
            
            _ => await IngestBinaryAsync(data, ct)
        };
    }

    private async Task<long> IngestImageFromBytesAsync(byte[] data, CancellationToken ct)
    {
        // Simple BMP/PNG header parsing for dimensions
        // In production, use System.Drawing or ImageSharp
        _logger?.LogWarning("Image ingestion from bytes requires external library - falling back to binary");
        return await IngestBinaryAsync(data, ct);
    }

    private async Task<long> IngestWavFromBytesAsync(byte[] data, CancellationToken ct)
    {
        // Parse WAV header
        if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
        {
            _logger?.LogWarning("Invalid WAV header - falling back to binary ingestion");
            return await IngestBinaryAsync(data, ct);
        }

        var channels = BitConverter.ToInt16(data, 22);
        var sampleRate = BitConverter.ToInt32(data, 24);
        var bitsPerSample = BitConverter.ToInt16(data, 34);

        // Find data chunk
        int dataOffset = 44;
        int dataSize = data.Length - 44;

        // Parse samples (16-bit PCM)
        var samples = new short[dataSize / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(data, dataOffset + i * 2);
        }

        return await IngestAudioAsync(sampleRate, channels, bitsPerSample, samples, ct);
    }

    #endregion
}
