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
/// High-performance spatial query service using SIMD acceleration,
/// parallel processing, and optimized database access patterns.
/// 
/// Performance characteristics:
/// - SIMD distance calculations (AVX2/AVX-512)
/// - Parallel query execution
/// - Streaming results for large datasets
/// - Memory-efficient coordinate extraction
/// 
/// Works with the new schema:
/// - Constants: leaf nodes (irreducible values)
/// - Compositions: internal nodes (references to other nodes)
/// - Relations: edges linking compositions to children
/// </summary>
public sealed class ParallelSpatialQueryService
{
    private readonly IDbContextFactory<HartDbContext> _contextFactory;
    private readonly ILogger<ParallelSpatialQueryService>? _logger;
    private readonly AsyncSemaphore _querySemaphore;
    private readonly int _maxParallelism;

    private const int DEFAULT_MAX_PARALLELISM = 16;

    public ParallelSpatialQueryService(
        IDbContextFactory<HartDbContext> contextFactory,
        ILogger<ParallelSpatialQueryService>? logger = null,
        int maxParallelism = DEFAULT_MAX_PARALLELISM)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger;
        _maxParallelism = maxParallelism;
        _querySemaphore = new AsyncSemaphore(maxParallelism, maxParallelism);
    }

    /// <summary>
    /// Find k-nearest Constants using SIMD-accelerated distance calculation
    /// </summary>
    public async Task<List<(Constant Constant, double Distance)>> FindKNearestConstantsAsync(
        double x, double y, double z, double m,
        int k = 10,
        CancellationToken cancellationToken = default)
    {
        if (k <= 0 || k > 10000)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be between 1 and 10000");

        var sw = Stopwatch.StartNew();
        
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Use PostGIS for initial filtering (bounding box)
        var factory = new GeometryFactory(new PrecisionModel(), 0);
        var queryPoint = factory.CreatePoint(new CoordinateZM(x, y, z, m));

        // Use PostGIS distance ordering with limit
        // This uses the GIST index efficiently
        var candidates = await context.Constants
            .AsNoTracking()
            .Where(c => c.Geom != null)
            .OrderBy(c => c.Geom!.Distance(queryPoint))
            .Take(k * 2) // Get more for refinement
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new List<(Constant, double)>();

        // SIMD-accelerated exact distance calculation
        var xs = new double[candidates.Count];
        var ys = new double[candidates.Count];
        var zs = new double[candidates.Count];
        var ms = new double[candidates.Count];
        var distances = new double[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            var coord = candidates[i].Geom!.Coordinate;
            xs[i] = coord.X;
            ys[i] = coord.Y;
            zs[i] = double.IsNaN(coord.Z) ? 0 : coord.Z;
            ms[i] = double.IsNaN(coord.M) ? 0 : coord.M;
        }

        VectorMath.ComputeDistancesBatch(x, y, z, m, xs, ys, zs, ms, distances);

        // Sort by distance and take k
        var results = candidates
            .Select((constant, idx) => (Constant: constant, Distance: distances[idx]))
            .OrderBy(r => r.Distance)
            .Take(k)
            .ToList();

        sw.Stop();
        _logger?.LogDebug("KNN Constants query returned {Count} results in {Ms}ms", results.Count, sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Find k-nearest Compositions using SIMD-accelerated distance calculation
    /// </summary>
    public async Task<List<(Composition Composition, double Distance)>> FindKNearestCompositionsAsync(
        double x, double y, double z, double m,
        int k = 10,
        long? typeId = null,
        CancellationToken cancellationToken = default)
    {
        if (k <= 0 || k > 10000)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be between 1 and 10000");

        var sw = Stopwatch.StartNew();
        
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Use PostGIS for initial filtering (bounding box)
        var factory = new GeometryFactory(new PrecisionModel(), 0);
        var queryPoint = factory.CreatePoint(new CoordinateZM(x, y, z, m));

        IQueryable<Composition> query = context.Compositions
            .AsNoTracking()
            .Where(c => c.Geom != null);
        
        if (typeId.HasValue)
            query = query.Where(c => c.TypeId == typeId);

        // Use PostGIS distance ordering with limit
        // This uses the GIST index efficiently
        var candidates = await query
            .OrderBy(c => c.Geom!.Distance(queryPoint))
            .Take(k * 2) // Get more for refinement
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new List<(Composition, double)>();

        // SIMD-accelerated exact distance calculation
        var xs = new double[candidates.Count];
        var ys = new double[candidates.Count];
        var zs = new double[candidates.Count];
        var ms = new double[candidates.Count];
        var distances = new double[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            var coord = candidates[i].Geom!.Coordinate;
            xs[i] = coord.X;
            ys[i] = coord.Y;
            zs[i] = double.IsNaN(coord.Z) ? 0 : coord.Z;
            ms[i] = double.IsNaN(coord.M) ? 0 : coord.M;
        }

        VectorMath.ComputeDistancesBatch(x, y, z, m, xs, ys, zs, ms, distances);

        // Sort by distance and take k
        var results = candidates
            .Select((composition, idx) => (Composition: composition, Distance: distances[idx]))
            .OrderBy(r => r.Distance)
            .Take(k)
            .ToList();

        sw.Stop();
        _logger?.LogDebug("KNN Compositions query returned {Count} results in {Ms}ms", results.Count, sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Find all Constants within radius using parallel chunked processing
    /// </summary>
    public async Task<List<(Constant Constant, double Distance)>> FindConstantsWithinRadiusAsync(
        double x, double y, double z, double m,
        double radius,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be positive");
        if (limit <= 0 || limit > 100000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100000");

        var sw = Stopwatch.StartNew();
        
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var factory = new GeometryFactory(new PrecisionModel(), 0);
        var queryPoint = factory.CreatePoint(new CoordinateZM(x, y, z, m));

        // Use PostGIS ST_DWithin for efficient filtering
        var candidates = await context.Constants
            .AsNoTracking()
            .Where(c => c.Geom != null && c.Geom.IsWithinDistance(queryPoint, radius))
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new List<(Constant, double)>();

        // SIMD-accelerated exact distance calculation
        var distances = new double[candidates.Count];
        Parallel.For(0, candidates.Count, i =>
        {
            var coord = candidates[i].Geom!.Coordinate;
            distances[i] = VectorMath.Distance4D(
                x, y, z, m,
                coord.X, coord.Y,
                double.IsNaN(coord.Z) ? 0 : coord.Z,
                double.IsNaN(coord.M) ? 0 : coord.M);
        });

        var results = candidates
            .Select((constant, idx) => (Constant: constant, Distance: distances[idx]))
            .Where(r => r.Distance <= radius) // Filter by exact distance
            .OrderBy(r => r.Distance)
            .ToList();

        sw.Stop();
        _logger?.LogDebug("Radius Constants query returned {Count} results in {Ms}ms", results.Count, sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Find all Compositions within radius using parallel chunked processing
    /// </summary>
    public async Task<List<(Composition Composition, double Distance)>> FindCompositionsWithinRadiusAsync(
        double x, double y, double z, double m,
        double radius,
        long? typeId = null,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be positive");
        if (limit <= 0 || limit > 100000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100000");

        var sw = Stopwatch.StartNew();
        
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var factory = new GeometryFactory(new PrecisionModel(), 0);
        var queryPoint = factory.CreatePoint(new CoordinateZM(x, y, z, m));

        IQueryable<Composition> query = context.Compositions
            .AsNoTracking()
            .Where(c => c.Geom != null && c.Geom.IsWithinDistance(queryPoint, radius));
        
        if (typeId.HasValue)
            query = query.Where(c => c.TypeId == typeId);

        // Use PostGIS ST_DWithin for efficient filtering
        var candidates = await query
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new List<(Composition, double)>();

        // SIMD-accelerated exact distance calculation
        var distances = new double[candidates.Count];
        Parallel.For(0, candidates.Count, i =>
        {
            var coord = candidates[i].Geom!.Coordinate;
            distances[i] = VectorMath.Distance4D(
                x, y, z, m,
                coord.X, coord.Y,
                double.IsNaN(coord.Z) ? 0 : coord.Z,
                double.IsNaN(coord.M) ? 0 : coord.M);
        });

        var results = candidates
            .Select((composition, idx) => (Composition: composition, Distance: distances[idx]))
            .Where(r => r.Distance <= radius) // Filter by exact distance
            .OrderBy(r => r.Distance)
            .ToList();

        sw.Stop();
        _logger?.LogDebug("Radius Compositions query returned {Count} results in {Ms}ms", results.Count, sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Batch nearest neighbor query - find k-nearest Constants for multiple query points
    /// </summary>
    public async Task<List<List<(Constant Constant, double Distance)>>> FindKNearestConstantsBatchAsync(
        (double X, double Y, double Z, double M)[] queryPoints,
        int k = 10,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        
        var results = new List<(Constant, double)>[queryPoints.Length];
        
        // Process queries in parallel
        await Parallel.ForEachAsync(
            Enumerable.Range(0, queryPoints.Length),
            new ParallelOptions 
            { 
                MaxDegreeOfParallelism = _maxParallelism,
                CancellationToken = cancellationToken 
            },
            async (i, ct) =>
            {
                var (x, y, z, m) = queryPoints[i];
                results[i] = await FindKNearestConstantsAsync(x, y, z, m, k, ct);
            });

        sw.Stop();
        _logger?.LogDebug("Batch KNN Constants query ({Count} points) completed in {Ms}ms", 
            queryPoints.Length, sw.ElapsedMilliseconds);

        return results.ToList();
    }

    /// <summary>
    /// Batch nearest neighbor query - find k-nearest Compositions for multiple query points
    /// </summary>
    public async Task<List<List<(Composition Composition, double Distance)>>> FindKNearestCompositionsBatchAsync(
        (double X, double Y, double Z, double M)[] queryPoints,
        int k = 10,
        long? typeId = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        
        var results = new List<(Composition, double)>[queryPoints.Length];
        
        // Process queries in parallel
        await Parallel.ForEachAsync(
            Enumerable.Range(0, queryPoints.Length),
            new ParallelOptions 
            { 
                MaxDegreeOfParallelism = _maxParallelism,
                CancellationToken = cancellationToken 
            },
            async (i, ct) =>
            {
                var (x, y, z, m) = queryPoints[i];
                results[i] = await FindKNearestCompositionsAsync(x, y, z, m, k, typeId, ct);
            });

        sw.Stop();
        _logger?.LogDebug("Batch KNN Compositions query ({Count} points) completed in {Ms}ms", 
            queryPoints.Length, sw.ElapsedMilliseconds);

        return results.ToList();
    }

    /// <summary>
    /// Stream Constants in Hilbert order - efficient for spatial traversal
    /// </summary>
    public async IAsyncEnumerable<Constant> StreamConstantsByHilbertRangeAsync(
        ulong hilbertHighStart, ulong hilbertLowStart,
        ulong hilbertHighEnd, ulong hilbertLowEnd,
        int batchSize = 1000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        ulong lastHigh = hilbertHighStart;
        ulong lastLow = hilbertLowStart > 0 ? hilbertLowStart - 1 : 0;
        int totalStreamed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var batch = await context.Constants
                .AsNoTracking()
                .Where(c =>
                    (c.HilbertHigh > lastHigh ||
                     (c.HilbertHigh == lastHigh && c.HilbertLow > lastLow)) &&
                    (c.HilbertHigh < hilbertHighEnd ||
                     (c.HilbertHigh == hilbertHighEnd && c.HilbertLow <= hilbertLowEnd)))
                .OrderBy(c => c.HilbertHigh)
                .ThenBy(c => c.HilbertLow)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            foreach (var constant in batch)
            {
                yield return constant;
                totalStreamed++;
            }

            var last = batch[^1];
            lastHigh = last.HilbertHigh;
            lastLow = last.HilbertLow;
        }

        sw.Stop();
        _logger?.LogDebug("Streamed {Count} Constants in Hilbert range in {Ms}ms", 
            totalStreamed, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Stream Compositions in Hilbert order - efficient for spatial traversal
    /// </summary>
    public async IAsyncEnumerable<Composition> StreamCompositionsByHilbertRangeAsync(
        ulong hilbertHighStart, ulong hilbertLowStart,
        ulong hilbertHighEnd, ulong hilbertLowEnd,
        int batchSize = 1000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        ulong lastHigh = hilbertHighStart;
        ulong lastLow = hilbertLowStart > 0 ? hilbertLowStart - 1 : 0;
        int totalStreamed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var batch = await context.Compositions
                .AsNoTracking()
                .Where(c =>
                    (c.HilbertHigh > lastHigh ||
                     (c.HilbertHigh == lastHigh && c.HilbertLow > lastLow)) &&
                    (c.HilbertHigh < hilbertHighEnd ||
                     (c.HilbertHigh == hilbertHighEnd && c.HilbertLow <= hilbertLowEnd)))
                .OrderBy(c => c.HilbertHigh)
                .ThenBy(c => c.HilbertLow)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            foreach (var composition in batch)
            {
                yield return composition;
                totalStreamed++;
            }

            var last = batch[^1];
            lastHigh = last.HilbertHigh;
            lastLow = last.HilbertLow;
        }

        sw.Stop();
        _logger?.LogDebug("Streamed {Count} Compositions in Hilbert range in {Ms}ms", 
            totalStreamed, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Compute attention scores between query Composition and multiple key Compositions using SIMD
    /// </summary>
    public async Task<List<(long CompositionId, double Score)>> ComputeCompositionAttentionScoresAsync(
        long queryCompositionId,
        long[] keyCompositionIds,
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var allIds = keyCompositionIds.Prepend(queryCompositionId).ToArray();
        
        var compositions = await context.Compositions
            .AsNoTracking()
            .Where(c => allIds.Contains(c.Id) && c.Geom != null)
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (!compositions.TryGetValue(queryCompositionId, out var queryComposition))
            throw new InvalidOperationException($"Query composition {queryCompositionId} not found");

        var qc = queryComposition.Geom!.Coordinate;
        var qx = qc.X;
        var qy = qc.Y;
        var qz = double.IsNaN(qc.Z) ? 0 : qc.Z;
        var qm = double.IsNaN(qc.M) ? 0 : qc.M;

        // Extract coordinates for SIMD processing
        var validKeys = keyCompositionIds.Where(id => compositions.ContainsKey(id)).ToArray();
        var xs = new double[validKeys.Length];
        var ys = new double[validKeys.Length];
        var zs = new double[validKeys.Length];
        var ms = new double[validKeys.Length];
        var distances = new double[validKeys.Length];
        var weights = new double[validKeys.Length];

        for (int i = 0; i < validKeys.Length; i++)
        {
            var c = compositions[validKeys[i]].Geom!.Coordinate;
            xs[i] = c.X;
            ys[i] = c.Y;
            zs[i] = double.IsNaN(c.Z) ? 0 : c.Z;
            ms[i] = double.IsNaN(c.M) ? 0 : c.M;
        }

        // SIMD-accelerated distance and attention computation
        VectorMath.ComputeDistancesBatch(qx, qy, qz, qm, xs, ys, zs, ms, distances);
        VectorMath.ComputeAttentionWeights(distances, weights);

        return validKeys
            .Select((id, idx) => (id, weights[idx]))
            .OrderByDescending(r => r.Item2)
            .ToList();
    }

    /// <summary>
    /// Compute attention scores between query Constant and multiple key Constants using SIMD
    /// </summary>
    public async Task<List<(long ConstantId, double Score)>> ComputeConstantAttentionScoresAsync(
        long queryConstantId,
        long[] keyConstantIds,
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var allIds = keyConstantIds.Prepend(queryConstantId).ToArray();
        
        var constants = await context.Constants
            .AsNoTracking()
            .Where(c => allIds.Contains(c.Id) && c.Geom != null)
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (!constants.TryGetValue(queryConstantId, out var queryConstant))
            throw new InvalidOperationException($"Query constant {queryConstantId} not found");

        var qc = queryConstant.Geom!.Coordinate;
        var qx = qc.X;
        var qy = qc.Y;
        var qz = double.IsNaN(qc.Z) ? 0 : qc.Z;
        var qm = double.IsNaN(qc.M) ? 0 : qc.M;

        // Extract coordinates for SIMD processing
        var validKeys = keyConstantIds.Where(id => constants.ContainsKey(id)).ToArray();
        var xs = new double[validKeys.Length];
        var ys = new double[validKeys.Length];
        var zs = new double[validKeys.Length];
        var ms = new double[validKeys.Length];
        var distances = new double[validKeys.Length];
        var weights = new double[validKeys.Length];

        for (int i = 0; i < validKeys.Length; i++)
        {
            var c = constants[validKeys[i]].Geom!.Coordinate;
            xs[i] = c.X;
            ys[i] = c.Y;
            zs[i] = double.IsNaN(c.Z) ? 0 : c.Z;
            ms[i] = double.IsNaN(c.M) ? 0 : c.M;
        }

        // SIMD-accelerated distance and attention computation
        VectorMath.ComputeDistancesBatch(qx, qy, qz, qm, xs, ys, zs, ms, distances);
        VectorMath.ComputeAttentionWeights(distances, weights);

        return validKeys
            .Select((id, idx) => (id, weights[idx]))
            .OrderByDescending(r => r.Item2)
            .ToList();
    }

    /// <summary>
    /// Find Compositions by reference containment (backlinks via Relations)
    /// </summary>
    public async Task<List<Composition>> FindReferencingCompositionsAsync(
        long[] targetConstantIds,
        long[] targetCompositionIds,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Use Relations table for efficient backlink lookup
        var referencingCompositionIds = await context.Relations
            .Where(r => 
                (targetConstantIds.Length > 0 && targetConstantIds.Contains(r.ChildConstantId ?? 0)) ||
                (targetCompositionIds.Length > 0 && targetCompositionIds.Contains(r.ChildCompositionId ?? 0)))
            .Select(r => r.CompositionId)
            .Distinct()
            .Take(limit)
            .ToListAsync(cancellationToken);

        var results = await context.Compositions
            .AsNoTracking()
            .Where(c => referencingCompositionIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Aggregate Compositions by TypeId with spatial centroid
    /// </summary>
    public async Task<List<CompositionTypeAggregate>> AggregateCompositionsByTypeAsync(
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var groups = await context.Compositions
            .AsNoTracking()
            .Where(c => c.Geom != null)
            .GroupBy(c => c.TypeId)
            .Select(g => new 
            {
                TypeId = g.Key,
                Count = g.Count(),
                AvgX = g.Average(c => c.Geom!.Centroid.X),
                AvgY = g.Average(c => c.Geom!.Centroid.Y)
            })
            .ToListAsync(cancellationToken);

        return groups.Select(g => new CompositionTypeAggregate
        {
            TypeId = g.TypeId,
            TotalCount = g.Count,
            CentroidX = g.AvgX,
            CentroidY = g.AvgY
        }).ToList();
    }

    /// <summary>
    /// Get aggregate statistics for Constants
    /// </summary>
    public async Task<ConstantAggregate> GetConstantAggregateAsync(
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var stats = await context.Constants
            .AsNoTracking()
            .Where(c => c.Geom != null)
            .GroupBy(_ => 1)
            .Select(g => new 
            {
                TotalCount = g.Count(),
                AvgX = g.Average(c => c.Geom!.Centroid.X),
                AvgY = g.Average(c => c.Geom!.Centroid.Y)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new ConstantAggregate
        {
            TotalCount = stats?.TotalCount ?? 0,
            CentroidX = stats?.AvgX ?? 0,
            CentroidY = stats?.AvgY ?? 0
        };
    }
}

public class CompositionTypeAggregate
{
    public long? TypeId { get; set; }
    public int TotalCount { get; set; }
    public double CentroidX { get; set; }
    public double CentroidY { get; set; }
}

public class ConstantAggregate
{
    public int TotalCount { get; set; }
    public double CentroidX { get; set; }
    public double CentroidY { get; set; }
}
