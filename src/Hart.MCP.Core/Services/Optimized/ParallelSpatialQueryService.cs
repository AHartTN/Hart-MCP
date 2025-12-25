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
    /// Find k-nearest neighbors using SIMD-accelerated distance calculation
    /// </summary>
    public async Task<List<(Atom Atom, double Distance)>> FindKNearestAsync(
        double x, double y, double z, double m,
        int k = 10,
        string? atomType = null,
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

        IQueryable<Atom> query = context.Atoms.AsNoTracking();
        
        if (!string.IsNullOrEmpty(atomType))
            query = query.Where(a => a.AtomType == atomType);

        // Use PostGIS distance ordering with limit
        // This uses the GIST index efficiently
        var candidates = await query
            .OrderBy(a => a.Geom.Distance(queryPoint))
            .Take(k * 2) // Get more for refinement
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new List<(Atom, double)>();

        // SIMD-accelerated exact distance calculation
        var xs = new double[candidates.Count];
        var ys = new double[candidates.Count];
        var zs = new double[candidates.Count];
        var ms = new double[candidates.Count];
        var distances = new double[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            var coord = candidates[i].Geom.Coordinate;
            xs[i] = coord.X;
            ys[i] = coord.Y;
            zs[i] = double.IsNaN(coord.Z) ? 0 : coord.Z;
            ms[i] = double.IsNaN(coord.M) ? 0 : coord.M;
        }

        VectorMath.ComputeDistancesBatch(x, y, z, m, xs, ys, zs, ms, distances);

        // Sort by distance and take k
        var results = candidates
            .Select((atom, idx) => (Atom: atom, Distance: distances[idx]))
            .OrderBy(r => r.Distance)
            .Take(k)
            .ToList();

        sw.Stop();
        _logger?.LogDebug("KNN query returned {Count} results in {Ms}ms", results.Count, sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Find all atoms within radius using parallel chunked processing
    /// </summary>
    public async Task<List<(Atom Atom, double Distance)>> FindWithinRadiusAsync(
        double x, double y, double z, double m,
        double radius,
        string? atomType = null,
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

        IQueryable<Atom> query = context.Atoms.AsNoTracking();
        
        if (!string.IsNullOrEmpty(atomType))
            query = query.Where(a => a.AtomType == atomType);

        // Use PostGIS ST_DWithin for efficient filtering
        var candidates = await query
            .Where(a => a.Geom.IsWithinDistance(queryPoint, radius))
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return new List<(Atom, double)>();

        // SIMD-accelerated exact distance calculation
        var distances = new double[candidates.Count];
        Parallel.For(0, candidates.Count, i =>
        {
            var coord = candidates[i].Geom.Coordinate;
            distances[i] = VectorMath.Distance4D(
                x, y, z, m,
                coord.X, coord.Y,
                double.IsNaN(coord.Z) ? 0 : coord.Z,
                double.IsNaN(coord.M) ? 0 : coord.M);
        });

        var results = candidates
            .Select((atom, idx) => (Atom: atom, Distance: distances[idx]))
            .Where(r => r.Distance <= radius) // Filter by exact distance
            .OrderBy(r => r.Distance)
            .ToList();

        sw.Stop();
        _logger?.LogDebug("Radius query returned {Count} results in {Ms}ms", results.Count, sw.ElapsedMilliseconds);

        return results;
    }

    /// <summary>
    /// Batch nearest neighbor query - find k-nearest for multiple query points
    /// </summary>
    public async Task<List<List<(Atom Atom, double Distance)>>> FindKNearestBatchAsync(
        (double X, double Y, double Z, double M)[] queryPoints,
        int k = 10,
        string? atomType = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        
        var results = new List<(Atom, double)>[queryPoints.Length];
        
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
                results[i] = await FindKNearestAsync(x, y, z, m, k, atomType, ct);
            });

        sw.Stop();
        _logger?.LogDebug("Batch KNN query ({Count} points) completed in {Ms}ms", 
            queryPoints.Length, sw.ElapsedMilliseconds);

        return results.ToList();
    }

    /// <summary>
    /// Stream atoms in Hilbert order - efficient for spatial traversal
    /// </summary>
    public async IAsyncEnumerable<Atom> StreamByHilbertRangeAsync(
        long hilbertHighStart, long hilbertLowStart,
        long hilbertHighEnd, long hilbertLowEnd,
        int batchSize = 1000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        long lastHigh = hilbertHighStart;
        long lastLow = hilbertLowStart - 1;
        int totalStreamed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var batch = await context.Atoms
                .AsNoTracking()
                .Where(a =>
                    (a.HilbertHigh > lastHigh ||
                     (a.HilbertHigh == lastHigh && a.HilbertLow > lastLow)) &&
                    (a.HilbertHigh < hilbertHighEnd ||
                     (a.HilbertHigh == hilbertHighEnd && a.HilbertLow <= hilbertLowEnd)))
                .OrderBy(a => a.HilbertHigh)
                .ThenBy(a => a.HilbertLow)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            foreach (var atom in batch)
            {
                yield return atom;
                totalStreamed++;
            }

            var last = batch[^1];
            lastHigh = last.HilbertHigh;
            lastLow = last.HilbertLow;
        }

        sw.Stop();
        _logger?.LogDebug("Streamed {Count} atoms in Hilbert range in {Ms}ms", 
            totalStreamed, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Compute attention scores between query and multiple key atoms using SIMD
    /// </summary>
    public async Task<List<(long AtomId, double Score)>> ComputeAttentionScoresAsync(
        long queryAtomId,
        long[] keyAtomIds,
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var allIds = keyAtomIds.Prepend(queryAtomId).ToArray();
        
        var atoms = await context.Atoms
            .AsNoTracking()
            .Where(a => allIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        if (!atoms.TryGetValue(queryAtomId, out var queryAtom))
            throw new InvalidOperationException($"Query atom {queryAtomId} not found");

        var qc = queryAtom.Geom.Coordinate;
        var qx = qc.X;
        var qy = qc.Y;
        var qz = double.IsNaN(qc.Z) ? 0 : qc.Z;
        var qm = double.IsNaN(qc.M) ? 0 : qc.M;

        // Extract coordinates for SIMD processing
        var validKeys = keyAtomIds.Where(id => atoms.ContainsKey(id)).ToArray();
        var xs = new double[validKeys.Length];
        var ys = new double[validKeys.Length];
        var zs = new double[validKeys.Length];
        var ms = new double[validKeys.Length];
        var distances = new double[validKeys.Length];
        var weights = new double[validKeys.Length];

        for (int i = 0; i < validKeys.Length; i++)
        {
            var c = atoms[validKeys[i]].Geom.Coordinate;
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
    /// Find atoms by reference containment (backlinks)
    /// </summary>
    public async Task<List<Atom>> FindReferencingAtomsAsync(
        long[] targetAtomIds,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Use GIN index on refs array
        var results = await context.Atoms
            .AsNoTracking()
            .Where(a => !a.IsConstant && a.Refs != null)
            .Where(a => targetAtomIds.Any(id => a.Refs!.Contains(id)))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Aggregate atoms by type with spatial centroid
    /// </summary>
    public async Task<List<AtomTypeAggregate>> AggregateByTypeAsync(
        CancellationToken cancellationToken = default)
    {
        using var semLock = await _querySemaphore.WaitAsync(cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var groups = await context.Atoms
            .AsNoTracking()
            .GroupBy(a => a.AtomType ?? "null")
            .Select(g => new 
            {
                Type = g.Key,
                Count = g.Count(),
                ConstantCount = g.Count(a => a.IsConstant),
                CompositionCount = g.Count(a => !a.IsConstant),
                AvgX = g.Average(a => a.Geom.Centroid.X),
                AvgY = g.Average(a => a.Geom.Centroid.Y)
            })
            .ToListAsync(cancellationToken);

        return groups.Select(g => new AtomTypeAggregate
        {
            AtomType = g.Type,
            TotalCount = g.Count,
            ConstantCount = g.ConstantCount,
            CompositionCount = g.CompositionCount,
            CentroidX = g.AvgX,
            CentroidY = g.AvgY
        }).ToList();
    }
}

public class AtomTypeAggregate
{
    public string AtomType { get; set; } = "";
    public int TotalCount { get; set; }
    public int ConstantCount { get; set; }
    public int CompositionCount { get; set; }
    public double CentroidX { get; set; }
    public double CentroidY { get; set; }
}
