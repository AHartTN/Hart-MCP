using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

// Alias to avoid ambiguity with System.Runtime.InteropServices.NativeLibrary
using HartNative = Hart.MCP.Core.Native.NativeLibrary;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Streaming model ingestion for large models (supports 1TB+ files).
/// 
/// ARCHITECTURE:
/// - Stream tensors from disk - never load full model in memory
/// - Process floats in chunks (default 1M floats per chunk)
/// - SPARSE ENCODING: Skip near-zero weights (configurable threshold)
/// - Batch DB writes (flush every N unique values)
/// - LRU cache for float→constantId lookups
/// 
/// SPARSE ENCODING INSIGHT:
/// Most weights are near-zero (no meaningful relationship).
/// - 60% sparsity = 60% storage savings, zero functional loss
/// - 80-90% sparsity = possible with some model types
/// - Only store weights where |value| > threshold
/// 
/// NEGATIVE WEIGHTS:
/// Negative = anti-relationship (repulsion). Still stored.
/// A repels B = valid edge with negative weight.
/// Only ~0 means "no relationship" = don't store.
/// 
/// PERFORMANCE TARGET:
/// - 22M parameter model: < 10 seconds (with 60% sparsity)
/// - 7B parameter model: < 5 minutes (storing only 2.8B values)
/// - 70B+ parameter model: streaming, tractable
/// </summary>
public class StreamingModelIngestionService : IngestionServiceBase
{
    private readonly HierarchicalTextIngestionService _textIngestionService;
    
    // Configuration
    private const int CHUNK_SIZE = 1_000_000;          // Process N floats at a time
    private const int CACHE_SIZE = 500_000;            // LRU cache size for float→constantId
    
    // Sparse encoding defaults
    public const float DEFAULT_SPARSITY_THRESHOLD = 1e-6f;  // |weight| < this = don't store
    public const float AGGRESSIVE_SPARSITY_THRESHOLD = 0.01f; // For 60%+ sparsity

    // Shared across all tensors in a model for deduplication
    private readonly Dictionary<uint, long> _floatCache = new(CACHE_SIZE);
    
    // Sparse encoding stats
    private long _totalValues;
    private long _storedValues;
    private long _skippedValues;

    public StreamingModelIngestionService(
        HartDbContext context,
        HierarchicalTextIngestionService textIngestionService,
        ILogger<StreamingModelIngestionService>? logger = null)
        : base(context, logger)
    {
        _textIngestionService = textIngestionService ?? throw new ArgumentNullException(nameof(textIngestionService));
    }

    /// <summary>
    /// Ingest a SafeTensor file with streaming and sparse encoding.
    /// </summary>
    /// <param name="filePath">Path to SafeTensor file</param>
    /// <param name="modelName">Name for this model</param>
    /// <param name="sparsityThreshold">Skip weights where |value| &lt; threshold. Default 1e-6. Use 0.01f for aggressive 60%+ sparsity.</param>
    /// <param name="progress">Optional progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<SparseModelIngestionResult> IngestSafeTensorStreamingAsync(
        string filePath,
        string modelName,
        float sparsityThreshold = DEFAULT_SPARSITY_THRESHOLD,
        IProgress<ModelIngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"SafeTensor file not found: {filePath}");

        var fileInfo = new FileInfo(filePath);
        Logger?.LogInformation("Streaming SafeTensor ingestion: {Model}, {Size:N0} bytes, sparsity threshold: {Threshold}", 
            modelName, fileInfo.Length, sparsityThreshold);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, 
            FileShare.Read, bufferSize: 4 * 1024 * 1024, useAsync: true); // 4MB buffer
        
        return await IngestSafeTensorStreamingAsync(stream, modelName, fileInfo.Length, sparsityThreshold, progress, ct);
    }

    /// <summary>
    /// Streaming ingestion from any stream with sparse encoding.
    /// 
    /// ARCHITECTURE - Three phases, minimal DB round-trips:
    /// 1. SCAN: Stream all tensors, collect unique float bits (CPU only)
    /// 2. BULK CREATE: One batch DB operation for all unique floats
    /// 3. COMPOSE: Create tensor compositions (one batch)
    /// </summary>
    public async Task<SparseModelIngestionResult> IngestSafeTensorStreamingAsync(
        Stream stream,
        string modelName,
        long totalBytes,
        float sparsityThreshold = DEFAULT_SPARSITY_THRESHOLD,
        IProgress<ModelIngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new SparseModelIngestionResult { ModelName = modelName };

        // Clear caches and stats for fresh model
        _floatCache.Clear();
        _totalValues = 0;
        _storedValues = 0;
        _skippedValues = 0;

        // ============================================
        // PHASE 1: Parse header
        // ============================================
        var metadata = await ParseSafeTensorHeaderAsync(stream, ct);
        var dataStartOffset = stream.Position;

        Logger?.LogInformation("Parsed {Count} tensors, sparsity threshold: {Threshold}", 
            metadata.Tensors.Count, sparsityThreshold);

        progress?.Report(new ModelIngestionProgress
        {
            Phase = "Parsing",
            TensorsTotal = metadata.Tensors.Count,
            TensorsProcessed = 0,
            BytesProcessed = stream.Position,
            BytesTotal = totalBytes
        });

        // ============================================
        // PHASE 2: SCAN - Collect all unique floats from ALL tensors (CPU only, no DB)
        // ============================================
        var allUniqueBits = new HashSet<uint>();
        var tensorData = new Dictionary<string, List<(int index, uint bits)>>();
        var tensorIndex = 0;
        var scanSw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var (tensorName, tensorInfo) in metadata.Tensors)
        {
            if (tensorName == "__metadata__") continue;
            if (!IsFloatDtype(tensorInfo.DType))
            {
                tensorIndex++;
                continue;
            }

            ct.ThrowIfCancellationRequested();

            // Scan tensor, collect significant values (NO DB access)
            var significantValues = new List<(int index, uint bits)>();
            await ScanTensorAsync(stream, dataStartOffset, tensorInfo, sparsityThreshold, 
                significantValues, allUniqueBits, ct);

            tensorData[tensorName] = significantValues;
            result.TotalParameters += tensorInfo.TotalElements;
            tensorIndex++;

            progress?.Report(new ModelIngestionProgress
            {
                Phase = "Scanning",
                TensorsTotal = metadata.Tensors.Count,
                TensorsProcessed = tensorIndex,
                CurrentTensor = tensorName,
                BytesProcessed = dataStartOffset + tensorInfo.DataOffset + tensorInfo.DataLength,
                BytesTotal = totalBytes,
                SparsityPercent = _totalValues > 0 ? 100.0 * _skippedValues / _totalValues : 0
            });
        }
        scanSw.Stop();

        Logger?.LogInformation(
            "Scan complete: {Unique:N0} unique floats, {Stored:N0} significant values in {Time}ms",
            allUniqueBits.Count, _storedValues, scanSw.ElapsedMilliseconds);

        // ============================================
        // PHASE 3: BULK CREATE - One DB operation for ALL unique floats
        // ============================================
        var createSw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new ModelIngestionProgress
        {
            Phase = "Creating constants",
            TensorsTotal = metadata.Tensors.Count,
            TensorsProcessed = tensorIndex,
            SparsityPercent = _totalValues > 0 ? 100.0 * _skippedValues / _totalValues : 0
        });

        await BulkCreateFloatConstantsAsync(allUniqueBits.ToArray(), ct);
        createSw.Stop();

        Logger?.LogInformation(
            "Float constants created: {Count:N0} constants in {Time}ms ({Rate:N0}/sec)",
            allUniqueBits.Count, createSw.ElapsedMilliseconds,
            allUniqueBits.Count * 1000.0 / Math.Max(1, createSw.ElapsedMilliseconds));

        // ============================================
        // PHASE 4: COMPOSE - Create tensor compositions (batch)
        // ============================================
        var composeSw = System.Diagnostics.Stopwatch.StartNew();
        var tensorDataList = new List<SparseTensorData>();

        foreach (var (tensorName, tensorInfo) in metadata.Tensors)
        {
            if (!tensorData.TryGetValue(tensorName, out var significantValues))
                continue;

            ct.ThrowIfCancellationRequested();

            // Resolve bits → constantId from cache (no DB)
            var refs = significantValues.Select(sv => _floatCache[sv.bits]).ToArray();
            var indices = significantValues.Select(sv => sv.index).ToArray();

            // Create composition (returns SparseTensorData with refs for Relation creation)
            var sparseData = CreateSparseTensorComposition(refs, indices, tensorInfo, modelName);
            tensorDataList.Add(sparseData);

            if (IsEmbeddingTensor(tensorName))
                result.EmbeddingCompositionIds[tensorName] = 0; // Will update after save
            else
                result.WeightCompositionIds[tensorName] = 0;
        }

        // Bulk save all tensor compositions
        Context.Compositions.AddRange(tensorDataList.Select(td => td.Composition));
        await Context.SaveChangesAsync(ct);

        // Create Relation entries for each tensor's references (to Constants)
        foreach (var sparseData in tensorDataList)
        {
            for (int i = 0; i < sparseData.Refs.Length; i++)
            {
                Context.Relations.Add(new Relation
                {
                    CompositionId = sparseData.Composition.Id,
                    ChildConstantId = sparseData.Refs[i],
                    Position = sparseData.Indices[i], // Original tensor index for sparse reconstruction
                    Multiplicity = sparseData.Multiplicities[i]
                });
            }
        }
        await Context.SaveChangesAsync(ct);

        // Update result with actual IDs
        int compositionIdx = 0;
        foreach (var (tensorName, _) in metadata.Tensors)
        {
            if (!tensorData.ContainsKey(tensorName)) continue;
            
            var compositionId = tensorDataList[compositionIdx].Composition.Id;
            if (result.EmbeddingCompositionIds.ContainsKey(tensorName))
                result.EmbeddingCompositionIds[tensorName] = compositionId;
            else if (result.WeightCompositionIds.ContainsKey(tensorName))
                result.WeightCompositionIds[tensorName] = compositionId;
            compositionIdx++;
        }

        composeSw.Stop();
        Logger?.LogInformation("Compositions created in {Time}ms", composeSw.ElapsedMilliseconds);

        result.TensorCount = tensorData.Count;
        result.TotalValues = _totalValues;
        result.StoredValues = _storedValues;
        result.SkippedValues = _skippedValues;
        result.SparsityPercent = _totalValues > 0 ? 100.0 * _skippedValues / _totalValues : 0;

        // ============================================
        // PHASE 5: Create root model composition
        // ============================================
        var allTensorCompositionIds = result.EmbeddingCompositionIds.Values
            .Concat(result.WeightCompositionIds.Values)
            .ToArray();

        if (allTensorCompositionIds.Length > 0)
        {
            result.RootCompositionId = await CreateModelCompositionAsync(
                allTensorCompositionIds, modelName, metadata, ct);
        }

        stopwatch.Stop();
        result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        var rate = result.TotalParameters / (stopwatch.ElapsedMilliseconds / 1000.0);
        Logger?.LogInformation(
            "COMPLETE: {Tensors} tensors, {Params:N0} params, {Sparsity:F1}% sparse, {Time}ms ({Rate:N0} params/sec)",
            result.TensorCount, result.TotalParameters, result.SparsityPercent, result.ProcessingTimeMs, rate);

        return result;
    }

    /// <summary>
    /// Scan a tensor and collect significant values. NO DATABASE ACCESS.
    /// </summary>
    private async Task ScanTensorAsync(
        Stream stream,
        long dataStartOffset,
        TensorInfo tensorInfo,
        float sparsityThreshold,
        List<(int index, uint bits)> significantValues,
        HashSet<uint> allUniqueBits,
        CancellationToken ct)
    {
        stream.Seek(dataStartOffset + tensorInfo.DataOffset, SeekOrigin.Begin);

        var bytesPerElement = GetBytesPerElement(tensorInfo.DType);
        var totalElements = tensorInfo.TotalElements;
        var chunkBuffer = new byte[CHUNK_SIZE * bytesPerElement];
        long elementsProcessed = 0;

        while (elementsProcessed < totalElements)
        {
            ct.ThrowIfCancellationRequested();

            var elementsToRead = (int)Math.Min(CHUNK_SIZE, totalElements - elementsProcessed);
            var bytesToRead = elementsToRead * bytesPerElement;

            var bytesRead = await stream.ReadAsync(chunkBuffer.AsMemory(0, bytesToRead), ct);
            if (bytesRead < bytesToRead)
                throw new InvalidDataException($"Unexpected end of tensor data for {tensorInfo.Name}");

            // CPU-only: collect significant values
            CollectSignificantValues(
                chunkBuffer.AsSpan(0, bytesToRead),
                tensorInfo.DType,
                (int)elementsProcessed,
                sparsityThreshold,
                significantValues,
                allUniqueBits);

            elementsProcessed += elementsToRead;
        }
    }

    /// <summary>
    /// Bulk create all float constants in ONE database operation.
    /// </summary>
    private async Task BulkCreateFloatConstantsAsync(uint[] allBits, CancellationToken ct)
    {
        if (allBits.Length == 0) return;

        // Generate all constants in parallel (CPU-bound)
        var constants = new Constant[allBits.Length];
        Parallel.For(0, allBits.Length, i =>
        {
            var bits = allBits[i];
            var baseHash = HartNative.ComputeSeedHash(bits);
            var contentHash = ComputeTypedContentHash(baseHash, SEED_TYPE_FLOAT_BITS);
            var point = HartNative.project_seed_to_hypersphere(bits);
            var hilbert = HartNative.point_to_hilbert(point);
            var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

            constants[i] = new Constant
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                SeedValue = bits,
                SeedType = SEED_TYPE_FLOAT_BITS,
                ContentHash = contentHash
            };
        });

        // Bulk insert
        Context.Constants.AddRange(constants);
        await Context.SaveChangesAsync(ct);

        // Update cache with IDs
        for (int i = 0; i < allBits.Length; i++)
        {
            _floatCache[allBits[i]] = constants[i].Id;
        }
    }

    /// <summary>
    /// Create a sparse tensor composition (no DB save - caller batches).
    /// </summary>
    private SparseTensorData CreateSparseTensorComposition(
        long[] refs,
        int[] indices,
        TensorInfo tensorInfo,
        string modelName)
    {
        var multiplicities = new int[refs.Length];
        Array.Fill(multiplicities, 1);

        var contentHash = HartNative.ComputeCompositionHash(refs, multiplicities);

        // Simple centroid for geometry
        double avgX = 0, avgY = 0, avgZ = 0, avgM = 0;
        var sampleSize = Math.Min(100, refs.Length);
        for (int i = 0; i < sampleSize; i++)
        {
            var idx = i * refs.Length / sampleSize;
            if (_floatCache.Values.Contains(refs[idx]))
            {
                // Just use hash-based position for speed
                var bits = _floatCache.First(kv => kv.Value == refs[idx]).Key;
                var pt = HartNative.project_seed_to_hypersphere(bits);
                avgX += pt.X; avgY += pt.Y; avgZ += pt.Z; avgM += pt.M;
            }
        }
        if (sampleSize > 0)
        {
            avgX /= sampleSize; avgY /= sampleSize; avgZ /= sampleSize; avgM /= sampleSize;
        }

        var geom = GeometryFactory.CreatePoint(new CoordinateZM(avgX, avgY, avgZ, avgM));
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM { X = avgX, Y = avgY, Z = avgZ, M = avgM });

        // Store tensor info as composition references instead of JSONB metadata
        // The sparse indices are encoded in the position field of Relation entries
        return new SparseTensorData
        {
            Composition = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = contentHash
            },
            Refs = refs,
            Multiplicities = multiplicities,
            Indices = indices
        };
    }

    // Helper class to carry sparse tensor data until we persist Relations
    private class SparseTensorData
    {
        public Composition Composition { get; set; } = null!;
        public long[] Refs { get; set; } = Array.Empty<long>();
        public int[] Multiplicities { get; set; } = Array.Empty<int>();
        public int[] Indices { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// Stream a single tensor with sparse encoding using proper batching:
    /// 1. Stream entire tensor, collect significant (index, bits) pairs
    /// 2. Batch lookup/create all unique floats at once
    /// 3. Resolve bits→constantId in memory
    /// 4. Create composition
    /// </summary>
    private async Task<long> IngestTensorStreamingAsync(
        Stream stream,
        long dataStartOffset,
        TensorInfo tensorInfo,
        string modelName,
        float sparsityThreshold,
        CancellationToken ct)
    {
        stream.Seek(dataStartOffset + tensorInfo.DataOffset, SeekOrigin.Begin);

        var bytesPerElement = GetBytesPerElement(tensorInfo.DType);
        var totalElements = tensorInfo.TotalElements;
        
        // PHASE 1: Stream tensor, collect significant (index, bits) pairs
        // Store bits (not constantIds yet) - resolve in batch later
        var significantValues = new List<(int index, uint bits)>();
        var uniqueBits = new HashSet<uint>();
        
        var chunkBuffer = new byte[CHUNK_SIZE * bytesPerElement];
        long elementsProcessed = 0;

        while (elementsProcessed < totalElements)
        {
            ct.ThrowIfCancellationRequested();

            var elementsToRead = (int)Math.Min(CHUNK_SIZE, totalElements - elementsProcessed);
            var bytesToRead = elementsToRead * bytesPerElement;

            var bytesRead = await stream.ReadAsync(chunkBuffer.AsMemory(0, bytesToRead), ct);
            if (bytesRead < bytesToRead)
                throw new InvalidDataException($"Unexpected end of tensor data for {tensorInfo.Name}");

            // Collect significant values (CPU-bound, no await needed)
            CollectSignificantValues(
                chunkBuffer.AsSpan(0, bytesToRead),
                tensorInfo.DType,
                (int)elementsProcessed,
                sparsityThreshold,
                significantValues,
                uniqueBits);

            elementsProcessed += elementsToRead;
        }

        Logger?.LogDebug(
            "Tensor {Name}: {Significant:N0} significant values, {Unique:N0} unique floats",
            tensorInfo.Name, significantValues.Count, uniqueBits.Count);

        // PHASE 2: Batch lookup/create all unique float constants at once
        await EnsureFloatConstantsBatchAsync(uniqueBits.ToArray(), ct);

        // PHASE 3: Resolve bits→constantId for all refs (from cache, no DB)
        var sparseRefs = new List<(int index, long constantId)>(significantValues.Count);
        foreach (var (index, bits) in significantValues)
        {
            sparseRefs.Add((index, _floatCache[bits]));
        }

        // PHASE 4: Create SPARSE tensor composition
        var tensorCompositionId = await CreateSparseTensorCompositionAsync(
            sparseRefs, tensorInfo, modelName, ct);

        return tensorCompositionId;
    }

    /// <summary>
    /// CPU-bound: Extract significant (non-sparse) values from a chunk.
    /// No DB access - just pure computation.
    /// </summary>
    private void CollectSignificantValues(
        ReadOnlySpan<byte> data,
        string dtype,
        int startIndex,
        float sparsityThreshold,
        List<(int index, uint bits)> significantValues,
        HashSet<uint> uniqueBits)
    {
        var floatCount = GetFloatCount(data.Length, dtype);

        for (int i = 0; i < floatCount; i++)
        {
            uint bits = ExtractFloatBits(data, i, dtype);
            float value = BitConverter.UInt32BitsToSingle(bits);
            
            _totalValues++;
            
            // SPARSE: Skip if |value| < threshold (no relationship worth recording)
            if (MathF.Abs(value) < sparsityThreshold)
            {
                _skippedValues++;
                continue;
            }
            
            _storedValues++;
            significantValues.Add((startIndex + i, bits));
            uniqueBits.Add(bits);
        }
    }

    /// <summary>
    /// Batch lookup/create all unique floats in one efficient operation.
    /// </summary>
    private async Task EnsureFloatConstantsBatchAsync(uint[] allUniqueBits, CancellationToken ct)
    {
        if (allUniqueBits.Length == 0) return;

        // Filter to only bits not already in cache
        var uncachedBits = allUniqueBits.Where(b => !_floatCache.ContainsKey(b)).ToArray();
        if (uncachedBits.Length == 0) return;

        Logger?.LogDebug("Batch lookup/create: {Count:N0} unique floats", uncachedBits.Length);

        // BATCH 1: Lookup existing in DB
        var bitsAsLong = uncachedBits.Select(b => (long)b).ToList();
        
        var existing = await Context.Constants
            .Where(c => bitsAsLong.Contains(c.SeedValue) &&
                        c.SeedType == SEED_TYPE_FLOAT_BITS)
            .Select(c => new { c.Id, c.SeedValue })
            .ToListAsync(ct);

        foreach (var constant in existing)
        {
            var bits = (uint)constant.SeedValue;
            _floatCache[bits] = constant.Id;
        }

        // Find missing (not in DB)
        var existingBits = new HashSet<uint>(existing.Select(c => (uint)c.SeedValue));
        var missing = uncachedBits.Where(b => !existingBits.Contains(b) && !_floatCache.ContainsKey(b)).ToList();

        if (missing.Count == 0) return;

        Logger?.LogDebug("Creating {Count:N0} new float constants", missing.Count);

        // BATCH 2: Create all missing constants
        var newConstants = new List<Constant>(missing.Count);
        foreach (var bits in missing)
        {
            var baseHash = HartNative.ComputeSeedHash(bits);
            var contentHash = ComputeTypedContentHash(baseHash, SEED_TYPE_FLOAT_BITS);
            var point = HartNative.project_seed_to_hypersphere(bits);
            var hilbert = HartNative.point_to_hilbert(point);
            var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

            newConstants.Add(new Constant
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                SeedValue = bits,
                SeedType = SEED_TYPE_FLOAT_BITS,
                ContentHash = contentHash
            });
        }

        // Bulk insert
        Context.Constants.AddRange(newConstants);
        await Context.SaveChangesAsync(ct);

        // Update cache with new IDs
        for (int i = 0; i < missing.Count; i++)
        {
            _floatCache[missing[i]] = newConstants[i].Id;
        }

        // Trim cache if too large
        if (_floatCache.Count > CACHE_SIZE * 2)
        {
            var toRemove = _floatCache.Keys.Take(_floatCache.Count - CACHE_SIZE).ToList();
            foreach (var key in toRemove)
            {
                _floatCache.Remove(key);
            }
        }
    }

    /// <summary>
    /// Create tensor composition with batch-created refs.
    /// </summary>
    private async Task<long> CreateTensorCompositionAsync(
        long[] refs,
        TensorInfo tensorInfo,
        string modelName,
        CancellationToken ct)
    {
        // For large tensors, we don't use multiplicities (RLE unlikely to help for weights)
        var multiplicities = new int[refs.Length];
        Array.Fill(multiplicities, 1);

        var contentHash = HartNative.ComputeCompositionHash(refs, multiplicities);

        // Check for existing
        var existing = await Context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // For large compositions, use centroid of sample points for geometry
        var sampleSize = Math.Min(100, refs.Length);
        var sampleIndices = Enumerable.Range(0, refs.Length)
            .Where((_, i) => i % (refs.Length / sampleSize + 1) == 0)
            .Take(sampleSize)
            .ToList();

        var sampleConstantIds = sampleIndices.Select(i => refs[i]).Distinct().ToArray();
        var sampleConstants = await Context.Constants
            .Where(c => sampleConstantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        double avgX = 0, avgY = 0, avgZ = 0, avgM = 0;
        int count = 0;
        foreach (var constantId in sampleConstantIds)
        {
            if (sampleConstants.TryGetValue(constantId, out var constant) && constant.Geom != null)
            {
                var coord = constant.Geom.Centroid.Coordinate;
                avgX += coord.X;
                avgY += coord.Y;
                avgZ += double.IsNaN(coord.Z) ? 0 : coord.Z;
                avgM += double.IsNaN(coord.M) ? 0 : coord.M;
                count++;
            }
        }

        if (count > 0)
        {
            avgX /= count;
            avgY /= count;
            avgZ /= count;
            avgM /= count;
        }

        var geom = GeometryFactory.CreatePoint(new CoordinateZM(avgX, avgY, avgZ, avgM));
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = avgX, Y = avgY, Z = avgZ, M = avgM
        });

        // Note: For model tensors, we store metadata as composition key-value pairs
        // The model/tensor info is referenced via TypeId to a composition that encodes the metadata
        // For now, we skip the metadata and just store the weights
        
        var tensorComposition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash
        };

        Context.Compositions.Add(tensorComposition);
        await Context.SaveChangesAsync(ct);

        // Create Relation entries for the tensor composition (linking to Constants)
        for (int i = 0; i < refs.Length; i++)
        {
            Context.Relations.Add(new Relation
            {
                CompositionId = tensorComposition.Id,
                ChildConstantId = refs[i],
                Position = i,
                Multiplicity = multiplicities[i]
            });
        }
        await Context.SaveChangesAsync(ct);

        return tensorComposition.Id;
    }

    /// <summary>
    /// Create SPARSE tensor composition - only stores significant weights.
    /// Uses COO-style (index, constantId) pairs instead of dense array.
    /// </summary>
    private async Task<long> CreateSparseTensorCompositionAsync(
        List<(int index, long constantId)> sparseRefs,
        TensorInfo tensorInfo,
        string modelName,
        CancellationToken ct)
    {
        // SPARSE: Store only the constant IDs (positions are metadata)
        // The indices tell us WHERE in the original tensor each weight lives
        var constantIds = sparseRefs.Select(r => r.constantId).ToArray();
        var indices = sparseRefs.Select(r => r.index).ToArray();
        
        // Multiplicities = 1 for each (no RLE, sparse storage does the compression)
        var multiplicities = new int[constantIds.Length];
        Array.Fill(multiplicities, 1);

        var contentHash = HartNative.ComputeCompositionHash(constantIds, multiplicities);

        // Check for existing
        var existing = await Context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Compute geometry from sample of stored weights
        var sampleSize = Math.Min(100, constantIds.Length);
        var sampleConstantIds = constantIds.Take(sampleSize).Distinct().ToArray();
        var sampleConstants = await Context.Constants
            .Where(c => sampleConstantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        double avgX = 0, avgY = 0, avgZ = 0, avgM = 0;
        int count = 0;
        foreach (var constantId in sampleConstantIds)
        {
            if (sampleConstants.TryGetValue(constantId, out var constant) && constant.Geom != null)
            {
                var coord = constant.Geom.Centroid.Coordinate;
                avgX += coord.X;
                avgY += coord.Y;
                avgZ += double.IsNaN(coord.Z) ? 0 : coord.Z;
                avgM += double.IsNaN(coord.M) ? 0 : coord.M;
                count++;
            }
        }

        if (count > 0)
        {
            avgX /= count;
            avgY /= count;
            avgZ /= count;
            avgM /= count;
        }

        var geom = GeometryFactory.CreatePoint(new CoordinateZM(avgX, avgY, avgZ, avgM));
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = avgX, Y = avgY, Z = avgZ, M = avgM
        });

        // SPARSE: For sparse tensors, the Position in Relation encodes the original tensor index
        // This allows reconstruction of the full tensor with zeros in non-stored positions
        
        var tensorComposition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash
        };

        Context.Compositions.Add(tensorComposition);
        await Context.SaveChangesAsync(ct);

        // Create Relation entries - Position stores the original tensor index
        for (int i = 0; i < constantIds.Length; i++)
        {
            Context.Relations.Add(new Relation
            {
                CompositionId = tensorComposition.Id,
                ChildConstantId = constantIds[i],
                Position = indices[i], // Original tensor index for sparse reconstruction
                Multiplicity = multiplicities[i]
            });
        }
        await Context.SaveChangesAsync(ct);

        Logger?.LogDebug(
            "Sparse tensor {Name}: {Stored:N0}/{Total:N0} values ({Sparsity:F1}% sparse)",
            tensorInfo.Name, constantIds.Length, tensorInfo.TotalElements,
            100.0 * (tensorInfo.TotalElements - constantIds.Length) / tensorInfo.TotalElements);

        return tensorComposition.Id;
    }

    private async Task<long> CreateModelCompositionAsync(
        long[] tensorCompositionIds,
        string modelName,
        SafeTensorMetadata metadata,
        CancellationToken ct)
    {
        var multiplicities = Enumerable.Repeat(1, tensorCompositionIds.Length).ToArray();
        var contentHash = HartNative.ComputeCompositionHash(tensorCompositionIds, multiplicities);

        var existing = await Context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Sample geometry from child compositions
        var sampleCompositions = await Context.Compositions
            .Where(c => tensorCompositionIds.Take(10).Contains(c.Id))
            .ToListAsync(ct);

        double avgX = 0, avgY = 0, avgZ = 0;
        foreach (var composition in sampleCompositions.Where(c => c.Geom != null))
        {
            var coord = composition.Geom!.Centroid.Coordinate;
            avgX += coord.X;
            avgY += coord.Y;
            avgZ += double.IsNaN(coord.Z) ? 0 : coord.Z;
        }
        if (sampleCompositions.Count > 0)
        {
            avgX /= sampleCompositions.Count;
            avgY /= sampleCompositions.Count;
            avgZ /= sampleCompositions.Count;
        }

        var geom = GeometryFactory.CreatePoint(new CoordinateZM(avgX, avgY, avgZ, 0));
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = avgX, Y = avgY, Z = avgZ, M = 0
        });

        // Model composition - a composition of tensor compositions
        // Model metadata can be stored as composition key-value pairs if needed
        var modelComposition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash
        };

        Context.Compositions.Add(modelComposition);
        await Context.SaveChangesAsync(ct);

        // Create Relation entries for tensor compositions (linking to child Compositions)
        for (int i = 0; i < tensorCompositionIds.Length; i++)
        {
            Context.Relations.Add(new Relation
            {
                CompositionId = modelComposition.Id,
                ChildCompositionId = tensorCompositionIds[i],
                Position = i,
                Multiplicity = multiplicities[i]
            });
        }
        await Context.SaveChangesAsync(ct);

        return modelComposition.Id;
    }

    // ============================================
    // HELPER METHODS
    // ============================================

    private async Task<SafeTensorMetadata> ParseSafeTensorHeaderAsync(Stream stream, CancellationToken ct)
    {
        var headerLenBytes = new byte[8];
        await stream.ReadExactlyAsync(headerLenBytes, ct);
        var headerLen = BinaryPrimitives.ReadUInt64LittleEndian(headerLenBytes);

        if (headerLen > 100_000_000)
            throw new InvalidDataException($"SafeTensor header too large: {headerLen} bytes");

        var headerBytes = new byte[headerLen];
        await stream.ReadExactlyAsync(headerBytes, ct);
        var headerJson = Encoding.UTF8.GetString(headerBytes);

        using var doc = JsonDocument.Parse(headerJson);
        var metadata = new SafeTensorMetadata();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "__metadata__")
            {
                foreach (var meta in prop.Value.EnumerateObject())
                {
                    metadata.Metadata[meta.Name] = meta.Value.GetString() ?? "";
                }
            }
            else
            {
                var tensorInfo = new TensorInfo
                {
                    Name = prop.Name,
                    DType = prop.Value.GetProperty("dtype").GetString() ?? "F32",
                    Shape = prop.Value.GetProperty("shape").EnumerateArray()
                        .Select(e => e.GetInt64()).ToArray(),
                };

                var offsets = prop.Value.GetProperty("data_offsets").EnumerateArray()
                    .Select(e => e.GetInt64()).ToArray();
                tensorInfo.DataOffset = offsets[0];
                tensorInfo.DataLength = offsets[1] - offsets[0];

                metadata.Tensors[prop.Name] = tensorInfo;
            }
        }

        return metadata;
    }

    private static int GetBytesPerElement(string dtype) => dtype switch
    {
        "F32" => 4,
        "F16" => 2,
        "BF16" => 2,
        "F64" => 8,
        "I8" => 1,
        "I16" => 2,
        "I32" => 4,
        "I64" => 8,
        _ => throw new NotSupportedException($"Unsupported dtype: {dtype}")
    };

    private static int GetFloatCount(int byteCount, string dtype) => dtype switch
    {
        "F32" => byteCount / 4,
        "F16" => byteCount / 2,
        "BF16" => byteCount / 2,
        "F64" => byteCount / 8,
        _ => throw new NotSupportedException($"Unsupported dtype: {dtype}")
    };

    private static uint ExtractFloatBits(ReadOnlySpan<byte> data, int index, string dtype)
    {
        return dtype switch
        {
            "F32" => BitConverter.ToUInt32(data.Slice(index * 4, 4)),
            "F16" => ConvertF16ToF32Bits(BitConverter.ToUInt16(data.Slice(index * 2, 2))),
            "BF16" => (uint)BitConverter.ToUInt16(data.Slice(index * 2, 2)) << 16,
            "F64" => BitConverter.SingleToUInt32Bits((float)BitConverter.ToDouble(data.Slice(index * 8, 8))),
            _ => throw new NotSupportedException($"Unsupported dtype: {dtype}")
        };
    }

    private static uint ConvertF16ToF32Bits(ushort f16)
    {
        var half = BitConverter.UInt16BitsToHalf(f16);
        return BitConverter.SingleToUInt32Bits((float)half);
    }

    private static bool IsEmbeddingTensor(string name) =>
        name.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("wte", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token_embedding", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if dtype is a floating-point type we can sparse-encode.
    /// Integer types (I64, I32, etc.) are not sparse-encoded.
    /// </summary>
    private static bool IsFloatDtype(string dtype) => dtype switch
    {
        "F32" => true,
        "F16" => true,
        "BF16" => true,
        "F64" => true,
        _ => false
    };
}

/// <summary>
/// Progress reporting for model ingestion.
/// </summary>
public class ModelIngestionProgress
{
    public string Phase { get; set; } = "";
    public int TensorsTotal { get; set; }
    public int TensorsProcessed { get; set; }
    public string? CurrentTensor { get; set; }
    public long BytesProcessed { get; set; }
    public long BytesTotal { get; set; }
    public double PercentComplete => BytesTotal > 0 ? 100.0 * BytesProcessed / BytesTotal : 0;
    
    /// <summary>
    /// Percentage of values skipped due to sparse encoding.
    /// </summary>
    public double SparsityPercent { get; set; }
}

/// <summary>
/// Result of sparse model ingestion with storage statistics.
/// </summary>
public class SparseModelIngestionResult
{
    public string ModelName { get; set; } = "";
    public long? RootCompositionId { get; set; }
    public int TensorCount { get; set; }
    public long TotalParameters { get; set; }
    public long ProcessingTimeMs { get; set; }
    
    /// <summary>
    /// Total float values encountered in the model.
    /// </summary>
    public long TotalValues { get; set; }
    
    /// <summary>
    /// Values actually stored (passed sparsity threshold).
    /// </summary>
    public long StoredValues { get; set; }
    
    /// <summary>
    /// Values skipped due to sparse encoding (|value| &lt; threshold).
    /// </summary>
    public long SkippedValues { get; set; }
    
    /// <summary>
    /// Percentage of values skipped. Higher = more space savings.
    /// 60% = typical, 80-90% = aggressive, possible with some models.
    /// </summary>
    public double SparsityPercent { get; set; }
    
    /// <summary>
    /// Embedding tensor compositions (for spatial queries).
    /// </summary>
    public Dictionary<string, long> EmbeddingCompositionIds { get; } = new();
    
    /// <summary>
    /// Weight tensor compositions (for relationship traversal).
    /// </summary>
    public Dictionary<string, long> WeightCompositionIds { get; } = new();
}
