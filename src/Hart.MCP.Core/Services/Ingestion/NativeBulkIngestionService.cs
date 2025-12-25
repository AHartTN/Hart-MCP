using System.Runtime.InteropServices;
using Hart.MCP.Core.Native;
using Microsoft.Extensions.Logging;

// Alias to avoid conflict with System.Runtime.InteropServices.NativeLibrary
using HartNative = Hart.MCP.Core.Native.NativeLibrary;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// High-performance bulk ingestion using native C++ and PostgreSQL COPY.
/// 
/// This COMPLETELY BYPASSES EF Core for bulk operations.
/// Target performance: 1M+ atoms/second.
/// 
/// Use this for:
/// - SafeTensor model ingestion (80MB model in <10 seconds)
/// - Unicode seeding (1.1M codepoints in <5 seconds)
/// - Vocabulary ingestion
/// 
/// The native layer uses:
/// - PostgreSQL COPY binary protocol (bypasses SQL parsing)
/// - SIMD-accelerated hash/geometry computation
/// - Parallel processing with OpenMP
/// - Streaming I/O (no full file in memory)
/// </summary>
public class NativeBulkIngestionService : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<NativeBulkIngestionService>? _logger;
    private IntPtr _nativeConn;
    private bool _disposed;

    // Pin the callback delegate to prevent GC collection during native call
    private HartNative.IngestionProgressCallback? _pinnedCallback;
    private GCHandle _callbackHandle;

    public NativeBulkIngestionService(
        string connectionString,
        ILogger<NativeBulkIngestionService>? logger = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
        _nativeConn = IntPtr.Zero;
    }

    /// <summary>
    /// Connect to PostgreSQL (lazy initialization)
    /// </summary>
    private IntPtr GetConnection()
    {
        if (_nativeConn == IntPtr.Zero)
        {
            _logger?.LogInformation("Connecting to PostgreSQL via native library...");
            _nativeConn = HartNative.hart_db_connect(_connectionString);
            
            if (_nativeConn == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to connect to PostgreSQL via native library");
            }
            
            _logger?.LogInformation("Native PostgreSQL connection established");
        }
        return _nativeConn;
    }

    /// <summary>
    /// Ingest a SafeTensor file with maximum performance.
    /// Uses native COPY protocol - bypasses EF Core entirely.
    /// </summary>
    /// <param name="filePath">Path to .safetensors file</param>
    /// <param name="modelName">Name for this model</param>
    /// <param name="targetSparsityPercent">Target sparsity (e.g., 25 for 25% sparse). 0 = use threshold.</param>
    /// <param name="sparsityThreshold">Manual threshold if targetSparsityPercent is 0</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<SafeTensorIngestionResult> IngestSafeTensorAsync(
        string filePath,
        string modelName,
        float targetSparsityPercent = 25.0f,
        float sparsityThreshold = 0.01f,
        IProgress<ModelIngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("SafeTensor file not found", filePath);

        var fileInfo = new FileInfo(filePath);
        _logger?.LogInformation(
            "Starting native SafeTensor ingestion: {Model}, {Size:N0} bytes, target {Sparsity}% sparse",
            modelName, fileInfo.Length, targetSparsityPercent);

        var conn = GetConnection();

        // Create managed progress callback that forwards to IProgress
        HartNative.IngestionProgressCallback? nativeCallback = null;
        
        if (progress != null)
        {
            nativeCallback = (phase, tensorsProcessed, tensorsTotal, valuesProcessed, sparsity, userData) =>
            {
                progress.Report(new ModelIngestionProgress
                {
                    Phase = phase,
                    TensorsProcessed = tensorsProcessed,
                    TensorsTotal = tensorsTotal,
                    CurrentTensor = phase,
                    SparsityPercent = sparsity
                });
            };
            
            // Pin the delegate to prevent GC
            _pinnedCallback = nativeCallback;
            _callbackHandle = GCHandle.Alloc(_pinnedCallback);
        }

        try
        {
            // Run native ingestion on thread pool to not block
            var result = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                
                var hr = HartNative.hart_ingest_safetensor(
                    conn,
                    filePath,
                    modelName,
                    sparsityThreshold,
                    targetSparsityPercent,
                    nativeCallback,
                    IntPtr.Zero,
                    out var nativeResult);

                if (hr != 0)
                {
                    throw new InvalidOperationException(
                        $"Native SafeTensor ingestion failed: {nativeResult.ErrorMessage}");
                }

                return nativeResult;
            }, ct);

            _logger?.LogInformation(
                "Native ingestion complete: {Tensors} tensors, {Params:N0} params, {Sparsity:F1}% sparse, {Time}ms",
                result.TensorCount, result.TotalParameters, result.SparsityPercent, result.ProcessingTimeMs);

            return new SafeTensorIngestionResult
            {
                RootAtomId = result.RootAtomId,
                TensorCount = result.TensorCount,
                TotalParameters = result.TotalParameters,
                TotalValues = result.TotalValues,
                StoredValues = result.StoredValues,
                SkippedValues = result.SkippedValues,
                SparsityPercent = result.SparsityPercent,
                ProcessingTimeMs = result.ProcessingTimeMs
            };
        }
        finally
        {
            // Unpin the callback
            if (_callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
            _pinnedCallback = null;
        }
    }

    /// <summary>
    /// Seed Unicode codepoints (BMP or full Unicode).
    /// Uses native COPY protocol for maximum speed.
    /// </summary>
    /// <param name="fullUnicode">true = full Unicode (1.1M), false = BMP only (65K)</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<long> SeedUnicodeAsync(
        bool fullUnicode = false,
        IProgress<UnicodeSeedProgress>? progress = null,
        CancellationToken ct = default)
    {
        var endCodepoint = fullUnicode ? 0x10FFFFu : 0xFFFFu;
        var total = (int)(endCodepoint + 1);
        
        _logger?.LogInformation("Starting native Unicode seeding: 0 to {End:X} ({Count:N0} codepoints)", 
            endCodepoint, total);

        var conn = GetConnection();

        // Create managed progress callback
        HartNative.IngestionProgressCallback? nativeCallback = null;
        
        if (progress != null)
        {
            nativeCallback = (phase, processed, totalCount, values, sparsity, userData) =>
            {
                progress.Report(new UnicodeSeedProgress
                {
                    CodepointsSeeded = (int)values,
                    TotalCodepoints = totalCount
                });
            };
            
            _pinnedCallback = nativeCallback;
            _callbackHandle = GCHandle.Alloc(_pinnedCallback);
        }

        try
        {
            var count = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                
                return HartNative.hart_seed_unicode(
                    conn,
                    0,
                    endCodepoint,
                    nativeCallback,
                    IntPtr.Zero);
            }, ct);

            if (count < 0)
            {
                throw new InvalidOperationException($"Native Unicode seeding failed with code {count}");
            }

            _logger?.LogInformation("Native Unicode seeding complete: {Count:N0} codepoints", count);
            return count;
        }
        finally
        {
            if (_callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
            _pinnedCallback = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }

        if (_nativeConn != IntPtr.Zero)
        {
            HartNative.hart_db_disconnect(_nativeConn);
            _nativeConn = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~NativeBulkIngestionService()
    {
        Dispose();
    }
}

/// <summary>
/// Result from SafeTensor ingestion (managed version)
/// </summary>
public class SafeTensorIngestionResult
{
    public long RootAtomId { get; set; }
    public int TensorCount { get; set; }
    public long TotalParameters { get; set; }
    public long TotalValues { get; set; }
    public long StoredValues { get; set; }
    public long SkippedValues { get; set; }
    public double SparsityPercent { get; set; }
    public long ProcessingTimeMs { get; set; }
}
