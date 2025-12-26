using System.Collections.Concurrent;
using System.Diagnostics;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services.Optimized;

/// <summary>
/// High-performance ingestion service with parallel processing,
/// batching, and pipelining for maximum throughput.
/// 
/// Performance characteristics:
/// - Batch inserts (100-1000 constants per transaction)
/// - Parallel hash computation
/// - Connection pooling (100 connections)
/// - Lock-free deduplication cache
/// </summary>
public sealed class ParallelIngestionService : IAsyncDisposable
{
    private readonly IDbContextFactory<HartDbContext> _contextFactory;
    private readonly GeometryFactory _geometryFactory;
    private readonly ILogger<ParallelIngestionService>? _logger;
    
    // Configuration
    private readonly int _batchSize;
    private readonly int _maxParallelism;
    
    // Performance infrastructure
    private readonly ConcurrentDictionary<string, long> _hashCache;
    private readonly AsyncSemaphore _dbSemaphore;
    private readonly ObjectPool<List<Constant>> _constantListPool;

    private const int SEED_TYPE_UNICODE = 0;
    private const int SEED_TYPE_INTEGER = 1;
    private const int SEED_TYPE_FLOAT_BITS = 2;
    private const int DEFAULT_BATCH_SIZE = 500;
    private const int DEFAULT_MAX_PARALLELISM = 8;
    private const int MAX_CACHE_SIZE = 100_000;

    public ParallelIngestionService(
        IDbContextFactory<HartDbContext> contextFactory,
        ILogger<ParallelIngestionService>? logger = null,
        int batchSize = DEFAULT_BATCH_SIZE,
        int maxParallelism = DEFAULT_MAX_PARALLELISM)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger;
        _batchSize = batchSize;
        _maxParallelism = maxParallelism;
        
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
        _hashCache = new ConcurrentDictionary<string, long>();
        _dbSemaphore = new AsyncSemaphore(_maxParallelism, _maxParallelism);
        _constantListPool = new ObjectPool<List<Constant>>(
            () => new List<Constant>(_batchSize),
            list => list.Clear(),
            maxSize: _maxParallelism * 2);
    }

    /// <summary>
    /// Ingest large text with parallel processing
    /// Returns composition ID
    /// </summary>
    public async Task<long> IngestTextParallelAsync(
        string text,
        string compositionType = "text",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));

        var sw = Stopwatch.StartNew();
        _logger?.LogInformation("Starting parallel text ingestion of {Length} characters", text.Length);

        // Phase 1: Extract codepoints (parallel)
        var codepoints = ExtractCodepointsParallel(text);
        _logger?.LogDebug("Extracted {Count} codepoints in {Ms}ms", codepoints.Count, sw.ElapsedMilliseconds);

        // Phase 2: RLE compression
        var (uniqueCodepoints, constantIds, multiplicities) = await CompressAndMapCodepointsAsync(
            codepoints, cancellationToken);
        _logger?.LogDebug("RLE compression: {Original} -> {Compressed} in {Ms}ms", 
            codepoints.Count, constantIds.Count, sw.ElapsedMilliseconds);

        // Phase 3: Ensure constants exist (parallel batched)
        await EnsureConstantsExistParallelAsync(uniqueCodepoints, cancellationToken);
        _logger?.LogDebug("Constants ensured in {Ms}ms", sw.ElapsedMilliseconds);

        // Phase 4: Resolve IDs
        var resolvedIds = await ResolveConstantIdsAsync(constantIds, cancellationToken);
        
        // Phase 5: Create composition
        var compositionId = await CreateCompositionAsync(
            resolvedIds,
            multiplicities.ToArray(),
            cancellationToken);

        sw.Stop();
        _logger?.LogInformation(
            "Ingested {Chars} chars as composition {Id} with {Refs} refs in {Ms}ms ({Rate:F0} chars/sec)",
            text.Length, compositionId, resolvedIds.Length, sw.ElapsedMilliseconds,
            text.Length * 1000.0 / sw.ElapsedMilliseconds);

        return compositionId;
    }

    /// <summary>
    /// Batch ingest multiple texts in parallel
    /// </summary>
    public async Task<long[]> IngestTextsParallelAsync(
        string[] texts,
        string compositionType = "text",
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        _logger?.LogInformation("Starting batch ingestion of {Count} texts", texts.Length);

        // Process all texts in parallel with semaphore limiting
        var tasks = new Task<long>[texts.Length];
        
        await Parallel.ForEachAsync(
            Enumerable.Range(0, texts.Length),
            new ParallelOptions 
            { 
                MaxDegreeOfParallelism = _maxParallelism,
                CancellationToken = cancellationToken 
            },
            async (i, ct) =>
            {
                tasks[i] = IngestTextParallelAsync(texts[i], compositionType, ct);
                await tasks[i];
            });

        var results = await Task.WhenAll(tasks);
        
        sw.Stop();
        _logger?.LogInformation(
            "Batch ingested {Count} texts in {Ms}ms ({Rate:F1} texts/sec)",
            texts.Length, sw.ElapsedMilliseconds,
            texts.Length * 1000.0 / sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Extract codepoints using parallel processing for large strings
    /// </summary>
    private static List<uint> ExtractCodepointsParallel(string text)
    {
        if (text.Length < 10000)
        {
            // Small text - use simple sequential
            return ExtractCodepointsSequential(text);
        }

        // Large text - parallel extraction
        int chunkSize = Math.Max(1000, text.Length / Environment.ProcessorCount);
        var chunks = new List<(int Start, int End)>();
        
        int pos = 0;
        while (pos < text.Length)
        {
            int end = Math.Min(pos + chunkSize, text.Length);
            
            // Ensure we don't split surrogate pairs
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
                end++;
            
            chunks.Add((pos, end));
            pos = end;
        }

        var results = new List<uint>[chunks.Count];
        
        Parallel.For(0, chunks.Count, i =>
        {
            var (start, end) = chunks[i];
            results[i] = ExtractCodepointsSequential(text.AsSpan(start, end - start));
        });

        // Combine results
        var total = results.Sum(r => r.Count);
        var combined = new List<uint>(total);
        foreach (var chunk in results)
            combined.AddRange(chunk);
        
        return combined;
    }

    private static List<uint> ExtractCodepointsSequential(ReadOnlySpan<char> text)
    {
        var codepoints = new List<uint>(text.Length);
        
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            
            if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                char low = text[i + 1];
                if (char.IsLowSurrogate(low))
                {
                    int codepoint = char.ConvertToUtf32(c, low);
                    codepoints.Add((uint)codepoint);
                    i++;
                    continue;
                }
            }
            
            codepoints.Add(c);
        }

        return codepoints;
    }

    /// <summary>
    /// RLE compression and codepoint->hash mapping
    /// </summary>
    private async Task<(HashSet<uint> UniqueCodepoints, List<string> Hashes, List<int> Multiplicities)> 
        CompressAndMapCodepointsAsync(List<uint> codepoints, CancellationToken cancellationToken)
    {
        var unique = new HashSet<uint>();
        var hashes = new List<string>(codepoints.Count / 2);
        var multiplicities = new List<int>(codepoints.Count / 2);
        
        int i = 0;
        while (i < codepoints.Count)
        {
            uint cp = codepoints[i];
            unique.Add(cp);
            
            // Count consecutive identical codepoints
            int count = 1;
            while (i + count < codepoints.Count && codepoints[i + count] == cp)
                count++;
            
            var hash = Convert.ToHexString(HartNative.ComputeSeedHash(cp));
            hashes.Add(hash);
            multiplicities.Add(count);
            
            i += count;
        }

        return (unique, hashes, multiplicities);
    }

    /// <summary>
    /// Ensure all constants exist in database (parallel batched)
    /// </summary>
    private async Task EnsureConstantsExistParallelAsync(
        HashSet<uint> codepoints,
        CancellationToken cancellationToken)
    {
        // Check cache first
        var uncached = codepoints
            .Where(cp => !_hashCache.ContainsKey(Convert.ToHexString(HartNative.ComputeSeedHash(cp))))
            .ToList();

        if (uncached.Count == 0)
            return;

        // Batch insert missing constants
        var batches = uncached
            .Chunk(_batchSize)
            .ToList();

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions 
            { 
                MaxDegreeOfParallelism = _maxParallelism,
                CancellationToken = cancellationToken 
            },
            async (batch, ct) =>
            {
                await InsertConstantBatchAsync(batch, ct);
            });
    }

    private async Task InsertConstantBatchAsync(uint[] codepoints, CancellationToken cancellationToken)
    {
        using var semLock = await _dbSemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Compute hashes for all codepoints in batch
        var hashToCodepoint = new Dictionary<string, uint>();
        foreach (var cp in codepoints)
        {
            var hash = Convert.ToHexString(HartNative.ComputeSeedHash(cp));
            hashToCodepoint[hash] = cp;
        }

        // Check which already exist
        var hashBytes = hashToCodepoint.Keys
            .Select(h => Convert.FromHexString(h))
            .ToList();

        var existing = await context.Constants
            .AsNoTracking()
            .Where(c => hashBytes.Contains(c.ContentHash))
            .Select(c => new { c.Id, Hash = Convert.ToHexString(c.ContentHash) })
            .ToListAsync(cancellationToken);

        // Cache existing
        foreach (var e in existing)
        {
            TryAddToCache(e.Hash, e.Id);
            hashToCodepoint.Remove(e.Hash);
        }

        if (hashToCodepoint.Count == 0)
            return;

        // Create new constants for missing codepoints
        var constantList = _constantListPool.Rent();
        try
        {
            foreach (var (hash, cp) in hashToCodepoint)
            {
                var point = HartNative.project_seed_to_hypersphere(cp);
                var hilbert = HartNative.point_to_hilbert(point);
                var geom = _geometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

                constantList.Add(new Constant
                {
                    HilbertHigh = (ulong)hilbert.High,
                    HilbertLow = (ulong)hilbert.Low,
                    Geom = geom,
                    SeedValue = cp,
                    SeedType = SEED_TYPE_UNICODE,
                    ContentHash = Convert.FromHexString(hash)
                });
            }

            context.Constants.AddRange(constantList);
            
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                
                // Cache newly created constants
                foreach (var constant in constantList)
                {
                    TryAddToCache(Convert.ToHexString(constant.ContentHash), constant.Id);
                }
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                // Race condition - re-fetch from database
                _logger?.LogDebug("Duplicate key on batch insert - fetching existing");
                
                context.ChangeTracker.Clear();
                
                var refetch = await context.Constants
                    .AsNoTracking()
                    .Where(c => hashBytes.Contains(c.ContentHash))
                    .Select(c => new { c.Id, Hash = Convert.ToHexString(c.ContentHash) })
                    .ToListAsync(cancellationToken);
                
                foreach (var e in refetch)
                    TryAddToCache(e.Hash, e.Id);
            }
        }
        finally
        {
            _constantListPool.Return(constantList);
        }
    }

    private async Task<long[]> ResolveConstantIdsAsync(
        List<string> hashes,
        CancellationToken cancellationToken)
    {
        var ids = new long[hashes.Count];
        var unresolvedIndices = new List<int>();
        var unresolvedHashes = new List<byte[]>();

        // Check cache first
        for (int i = 0; i < hashes.Count; i++)
        {
            if (_hashCache.TryGetValue(hashes[i], out var id))
            {
                ids[i] = id;
            }
            else
            {
                unresolvedIndices.Add(i);
                unresolvedHashes.Add(Convert.FromHexString(hashes[i]));
            }
        }

        if (unresolvedIndices.Count == 0)
            return ids;

        // Fetch unresolved from database
        using var semLock = await _dbSemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var fetched = await context.Constants
            .AsNoTracking()
            .Where(c => unresolvedHashes.Contains(c.ContentHash))
            .Select(c => new { c.Id, Hash = Convert.ToHexString(c.ContentHash) })
            .ToListAsync(cancellationToken);

        var hashToId = fetched.ToDictionary(f => f.Hash, f => f.Id);

        for (int i = 0; i < unresolvedIndices.Count; i++)
        {
            var idx = unresolvedIndices[i];
            var hash = hashes[idx];
            
            if (hashToId.TryGetValue(hash, out var id))
            {
                ids[idx] = id;
                TryAddToCache(hash, id);
            }
            else
            {
                throw new InvalidOperationException($"Failed to resolve constant hash: {hash}");
            }
        }

        return ids;
    }

    private async Task<long> CreateCompositionAsync(
        long[] childConstantIds,
        int[] multiplicities,
        CancellationToken cancellationToken)
    {
        var contentHash = HartNative.ComputeCompositionHash(childConstantIds, multiplicities);
        var hashHex = Convert.ToHexString(contentHash);

        // Check cache
        if (_hashCache.TryGetValue(hashHex, out var cachedId))
            return cachedId;

        using var semLock = await _dbSemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Check database
        var existing = await context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
        {
            TryAddToCache(hashHex, existing);
            return existing;
        }

        // Load child geometries from Constants table
        var children = await context.Constants
            .AsNoTracking()
            .Where(c => childConstantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Build geometry
        var coords = childConstantIds
            .Select(id => children[id].Geom!.Coordinate)
            .Select(c => new CoordinateZM(c.X, c.Y, double.IsNaN(c.Z) ? 0 : c.Z, double.IsNaN(c.M) ? 0 : c.M))
            .ToArray();

        Geometry geom = coords.Length == 1
            ? _geometryFactory.CreatePoint(coords[0])
            : _geometryFactory.CreateLineString(coords);

        var centroid = geom.Centroid.Coordinate;
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = centroid.X,
            Y = centroid.Y,
            Z = double.IsNaN(centroid.Z) ? 0 : centroid.Z,
            M = double.IsNaN(centroid.M) ? 0 : centroid.M
        });

        var composition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash
        };

        context.Compositions.Add(composition);

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            // Create Relation entries for composition edges
            for (int i = 0; i < childConstantIds.Length; i++)
            {
                context.Relations.Add(new Relation
                {
                    CompositionId = composition.Id,
                    ChildConstantId = childConstantIds[i],
                    ChildCompositionId = null,
                    Position = i,
                    Multiplicity = multiplicities[i]
                });
            }
            await context.SaveChangesAsync(cancellationToken);

            TryAddToCache(hashHex, composition.Id);
            return composition.Id;
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            context.ChangeTracker.Clear();
            
            existing = await context.Compositions
                .Where(c => c.ContentHash == contentHash)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(cancellationToken);
            
            if (existing != 0)
            {
                TryAddToCache(hashHex, existing);
                return existing;
            }
            
            throw;
        }
    }

    private void TryAddToCache(string hash, long id)
    {
        // Evict oldest entries if cache is full
        if (_hashCache.Count >= MAX_CACHE_SIZE)
        {
            // Simple eviction: remove random 10%
            var toRemove = _hashCache.Keys.Take(MAX_CACHE_SIZE / 10).ToList();
            foreach (var key in toRemove)
                _hashCache.TryRemove(key, out _);
        }
        
        _hashCache.TryAdd(hash, id);
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("duplicate key") == true;
    }

    public int CacheSize => _hashCache.Count;

    public void ClearCache() => _hashCache.Clear();

    public async ValueTask DisposeAsync()
    {
        _dbSemaphore.Dispose();
    }
}
