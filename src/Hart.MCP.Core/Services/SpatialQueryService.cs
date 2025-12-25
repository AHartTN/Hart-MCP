using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services;

/// <summary>
/// Spatial query service - GIS-style queries on 4D hypersphere
/// Exploits PostGIS for geometric knowledge exploration
/// </summary>
public class SpatialQueryService
{
    private readonly HartDbContext _context;
    private readonly ILogger<SpatialQueryService>? _logger;

    public SpatialQueryService(HartDbContext context, ILogger<SpatialQueryService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;
    }

    /// <summary>
    /// Find nearest neighbors in 4D hypersphere space
    /// Returns atoms spatially close to the given seed
    /// </summary>
    public async Task<List<Atom>> FindNearestNeighborsAsync(
        uint seed, 
        int limit = 10, 
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000");

        var point = NativeLibrary.project_seed_to_hypersphere(seed);
        var factory = new GeometryFactory(new PrecisionModel(), 0);
        var geom = factory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

        _logger?.LogDebug("Finding {Limit} nearest neighbors to seed {Seed}", limit, seed);

        var results = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.IsConstant)
            .OrderBy(a => a.Geom.Distance(geom))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find atoms within Hilbert range (locality-preserving range query)
    /// </summary>
    public async Task<List<Atom>> FindInHilbertRangeAsync(
        long hilbertHighStart, long hilbertLowStart,
        long hilbertHighEnd, long hilbertLowEnd,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        _logger?.LogDebug("Finding atoms in Hilbert range [{StartH}:{StartL}] to [{EndH}:{EndL}]",
            hilbertHighStart, hilbertLowStart, hilbertHighEnd, hilbertLowEnd);

        var results = await _context.Atoms
            .AsNoTracking()
            .Where(a =>
                (a.HilbertHigh > hilbertHighStart ||
                 (a.HilbertHigh == hilbertHighStart && a.HilbertLow >= hilbertLowStart)) &&
                (a.HilbertHigh < hilbertHighEnd ||
                 (a.HilbertHigh == hilbertHighEnd && a.HilbertLow <= hilbertLowEnd)))
            .OrderBy(a => a.HilbertHigh)
            .ThenBy(a => a.HilbertLow)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find atoms within geometric bounding box
    /// </summary>
    public async Task<List<Atom>> FindInBoundingBoxAsync(
        double xMin, double yMin, double zMin, double mMin,
        double xMax, double yMax, double zMax, double mMax,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        var factory = new GeometryFactory(new PrecisionModel(), 0);
        var envelope = new Envelope(xMin, xMax, yMin, yMax);
        var bbox = factory.ToGeometry(envelope);

        _logger?.LogDebug("Finding atoms in bounding box ({XMin},{YMin}) to ({XMax},{YMax})",
            xMin, yMin, xMax, yMax);

        var results = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.Geom.Intersects(bbox))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find atoms that reference a specific atom (backlink traversal)
    /// </summary>
    public async Task<List<Atom>> FindReferencingAtomsAsync(
        long atomId, 
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (atomId <= 0)
            throw new ArgumentOutOfRangeException(nameof(atomId), "Atom ID must be positive");
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        _logger?.LogDebug("Finding atoms referencing atom {AtomId}", atomId);

        var results = await _context.Atoms
            .AsNoTracking()
            .Where(a => !a.IsConstant && a.Refs != null && a.Refs.Contains(atomId))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find similar compositions using geometric similarity
    /// </summary>
    public async Task<List<Atom>> FindSimilarCompositionsAsync(
        long compositionId, 
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (compositionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(compositionId), "Composition ID must be positive");
        if (limit <= 0 || limit > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000");

        var target = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.Id == compositionId && !a.IsConstant)
            .FirstOrDefaultAsync(cancellationToken);

        if (target == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        _logger?.LogDebug("Finding compositions similar to {CompositionId}", compositionId);

        var results = await _context.Atoms
            .AsNoTracking()
            .Where(a => !a.IsConstant && a.Id != compositionId)
            .OrderBy(a => a.Geom.Distance(target.Geom))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Compute difference between two compositions
    /// </summary>
    public async Task<CompositionDifference> ComputeCompositionDifferenceAsync(
        long compositionA, 
        long compositionB,
        CancellationToken cancellationToken = default)
    {
        if (compositionA <= 0 || compositionB <= 0)
            throw new ArgumentOutOfRangeException("Composition IDs must be positive");

        var atomA = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == compositionA, cancellationToken);
        var atomB = await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == compositionB, cancellationToken);

        if (atomA == null)
            throw new InvalidOperationException($"Composition {compositionA} not found");
        if (atomB == null)
            throw new InvalidOperationException($"Composition {compositionB} not found");
        if (atomA.Refs == null || atomB.Refs == null)
            throw new InvalidOperationException("Both compositions must have refs");

        var refsA = atomA.Refs.ToHashSet();
        var refsB = atomB.Refs.ToHashSet();

        return new CompositionDifference
        {
            OnlyInA = refsA.Except(refsB).ToList(),
            OnlyInB = refsB.Except(refsA).ToList(),
            Shared = refsA.Intersect(refsB).ToList()
        };
    }

    /// <summary>
    /// Get composition statistics
    /// </summary>
    public async Task<CompositionStats> GetCompositionStatsAsync(
        long compositionId,
        CancellationToken cancellationToken = default)
    {
        if (compositionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(compositionId), "Composition ID must be positive");

        var composition = await _context.Atoms
            .AsNoTracking()
            .Where(a => a.Id == compositionId && !a.IsConstant)
            .FirstOrDefaultAsync(cancellationToken);

        if (composition == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        if (composition.Refs == null)
            throw new InvalidOperationException($"Composition {compositionId} has no refs");

        var refStats = await _context.Atoms
            .AsNoTracking()
            .Where(a => composition.Refs.Contains(a.Id))
            .GroupBy(a => a.IsConstant)
            .Select(g => new { IsConstant = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var centroid = composition.Geom.Centroid;

        return new CompositionStats
        {
            Id = compositionId,
            RefCount = composition.Refs.Length,
            UniqueRefs = composition.Refs.Distinct().Count(),
            TotalMultiplicity = composition.Multiplicities?.Sum() ?? 0,
            GeometryType = composition.Geom.GeometryType,
            GeometryLength = composition.Geom.Length,
            CentroidX = centroid.X,
            CentroidY = centroid.Y,
            CentroidZ = centroid.Coordinate.Z,
            CentroidM = centroid.Coordinate.M,
            ConstantsReferenced = refStats.FirstOrDefault(r => r.IsConstant)?.Count ?? 0,
            CompositionsReferenced = refStats.FirstOrDefault(r => !r.IsConstant)?.Count ?? 0
        };
    }

    /// <summary>
    /// Search atoms by type
    /// </summary>
    public async Task<List<Atom>> FindByTypeAsync(
        string atomType,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(atomType))
            throw new ArgumentException("Atom type cannot be empty", nameof(atomType));
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        return await _context.Atoms
            .AsNoTracking()
            .Where(a => a.AtomType == atomType)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get total atom counts
    /// </summary>
    public async Task<AtomCounts> GetAtomCountsAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _context.Atoms
            .AsNoTracking()
            .GroupBy(a => a.IsConstant)
            .Select(g => new { IsConstant = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return new AtomCounts
        {
            TotalConstants = counts.FirstOrDefault(c => c.IsConstant)?.Count ?? 0,
            TotalCompositions = counts.FirstOrDefault(c => !c.IsConstant)?.Count ?? 0
        };
    }
}

// Query result types
public class CompositionDifference
{
    public List<long> OnlyInA { get; set; } = new();
    public List<long> OnlyInB { get; set; } = new();
    public List<long> Shared { get; set; } = new();
}

public class CompositionStats
{
    public long Id { get; set; }
    public int RefCount { get; set; }
    public int UniqueRefs { get; set; }
    public int TotalMultiplicity { get; set; }
    public string GeometryType { get; set; } = "";
    public double GeometryLength { get; set; }
    public double CentroidX { get; set; }
    public double CentroidY { get; set; }
    public double CentroidZ { get; set; }
    public double CentroidM { get; set; }
    public int ConstantsReferenced { get; set; }
    public int CompositionsReferenced { get; set; }
}

public class AtomCounts
{
    public int TotalConstants { get; set; }
    public int TotalCompositions { get; set; }
    public int Total => TotalConstants + TotalCompositions;
}
