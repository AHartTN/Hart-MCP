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

    /// <summary>
    /// Exposes the database context for advanced queries
    /// </summary>
    public HartDbContext Context => _context;

    public SpatialQueryService(HartDbContext context, ILogger<SpatialQueryService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;
    }

    /// <summary>
    /// Find nearest neighbor constants in 4D hypersphere space
    /// Returns constants spatially close to the given seed
    /// </summary>
    public async Task<List<Constant>> FindNearestNeighborsAsync(
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

        var results = await _context.Constants
            .AsNoTracking()
            .Where(c => c.Geom != null)
            .OrderBy(c => c.Geom!.Distance(geom))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find constants within Hilbert range (locality-preserving range query)
    /// </summary>
    public async Task<List<Constant>> FindConstantsInHilbertRangeAsync(
        ulong hilbertHighStart, ulong hilbertLowStart,
        ulong hilbertHighEnd, ulong hilbertLowEnd,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        _logger?.LogDebug("Finding constants in Hilbert range [{StartH}:{StartL}] to [{EndH}:{EndL}]",
            hilbertHighStart, hilbertLowStart, hilbertHighEnd, hilbertLowEnd);

        var results = await _context.Constants
            .AsNoTracking()
            .Where(c =>
                (c.HilbertHigh > hilbertHighStart ||
                 (c.HilbertHigh == hilbertHighStart && c.HilbertLow >= hilbertLowStart)) &&
                (c.HilbertHigh < hilbertHighEnd ||
                 (c.HilbertHigh == hilbertHighEnd && c.HilbertLow <= hilbertLowEnd)))
            .OrderBy(c => c.HilbertHigh)
            .ThenBy(c => c.HilbertLow)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find compositions within Hilbert range (locality-preserving range query)
    /// </summary>
    public async Task<List<Composition>> FindCompositionsInHilbertRangeAsync(
        ulong hilbertHighStart, ulong hilbertLowStart,
        ulong hilbertHighEnd, ulong hilbertLowEnd,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        _logger?.LogDebug("Finding compositions in Hilbert range [{StartH}:{StartL}] to [{EndH}:{EndL}]",
            hilbertHighStart, hilbertLowStart, hilbertHighEnd, hilbertLowEnd);

        var results = await _context.Compositions
            .AsNoTracking()
            .Where(c =>
                (c.HilbertHigh > hilbertHighStart ||
                 (c.HilbertHigh == hilbertHighStart && c.HilbertLow >= hilbertLowStart)) &&
                (c.HilbertHigh < hilbertHighEnd ||
                 (c.HilbertHigh == hilbertHighEnd && c.HilbertLow <= hilbertLowEnd)))
            .OrderBy(c => c.HilbertHigh)
            .ThenBy(c => c.HilbertLow)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find constants within geometric bounding box
    /// </summary>
    public async Task<List<Constant>> FindConstantsInBoundingBoxAsync(
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

        _logger?.LogDebug("Finding constants in bounding box ({XMin},{YMin}) to ({XMax},{YMax})",
            xMin, yMin, xMax, yMax);

        var results = await _context.Constants
            .AsNoTracking()
            .Where(c => c.Geom != null && c.Geom.Intersects(bbox))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find compositions within geometric bounding box
    /// </summary>
    public async Task<List<Composition>> FindCompositionsInBoundingBoxAsync(
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

        _logger?.LogDebug("Finding compositions in bounding box ({XMin},{YMin}) to ({XMax},{YMax})",
            xMin, yMin, xMax, yMax);

        var results = await _context.Compositions
            .AsNoTracking()
            .Where(c => c.Geom != null && c.Geom.Intersects(bbox))
            .Take(limit)
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find compositions that reference a specific constant (backlink traversal)
    /// </summary>
    public async Task<List<Composition>> FindCompositionsReferencingConstantAsync(
        long constantId, 
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (constantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(constantId), "Constant ID must be positive");
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        _logger?.LogDebug("Finding compositions referencing constant {ConstantId}", constantId);

        var parentIds = await _context.Relations
            .Where(r => r.ChildConstantId == constantId)
            .Select(r => r.CompositionId)
            .Distinct()
            .Take(limit)
            .ToListAsync(cancellationToken);

        var results = await _context.Compositions
            .AsNoTracking()
            .Where(c => parentIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find compositions that reference a specific composition (backlink traversal)
    /// </summary>
    public async Task<List<Composition>> FindCompositionsReferencingCompositionAsync(
        long compositionId, 
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (compositionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(compositionId), "Composition ID must be positive");
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        _logger?.LogDebug("Finding compositions referencing composition {CompositionId}", compositionId);

        var parentIds = await _context.Relations
            .Where(r => r.ChildCompositionId == compositionId)
            .Select(r => r.CompositionId)
            .Distinct()
            .Take(limit)
            .ToListAsync(cancellationToken);

        var results = await _context.Compositions
            .AsNoTracking()
            .Where(c => parentIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        return results;
    }

    /// <summary>
    /// Find similar compositions using geometric similarity
    /// </summary>
    public async Task<List<Composition>> FindSimilarCompositionsAsync(
        long compositionId, 
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (compositionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(compositionId), "Composition ID must be positive");
        if (limit <= 0 || limit > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000");

        var target = await _context.Compositions
            .AsNoTracking()
            .Where(c => c.Id == compositionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (target == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        _logger?.LogDebug("Finding compositions similar to {CompositionId}", compositionId);

        var results = await _context.Compositions
            .AsNoTracking()
            .Where(c => c.Id != compositionId && c.Geom != null && target.Geom != null)
            .OrderBy(c => c.Geom!.Distance(target.Geom!))
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

        var compA = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionA, cancellationToken);
        var compB = await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionB, cancellationToken);

        if (compA == null)
            throw new InvalidOperationException($"Composition {compositionA} not found");
        if (compB == null)
            throw new InvalidOperationException($"Composition {compositionB} not found");

        // Get child IDs from Relation table (both constant and composition children)
        var refsA = await _context.Relations
            .Where(r => r.CompositionId == compositionA)
            .Select(r => r.ChildConstantId ?? r.ChildCompositionId ?? 0)
            .Where(id => id != 0)
            .ToListAsync(cancellationToken);
        var refsB = await _context.Relations
            .Where(r => r.CompositionId == compositionB)
            .Select(r => r.ChildConstantId ?? r.ChildCompositionId ?? 0)
            .Where(id => id != 0)
            .ToListAsync(cancellationToken);

        if (refsA.Count == 0 || refsB.Count == 0)
            throw new InvalidOperationException("Both compositions must have relations");

        var setA = refsA.ToHashSet();
        var setB = refsB.ToHashSet();

        return new CompositionDifference
        {
            OnlyInA = setA.Except(setB).ToList(),
            OnlyInB = setB.Except(setA).ToList(),
            Shared = setA.Intersect(setB).ToList()
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

        var composition = await _context.Compositions
            .AsNoTracking()
            .Where(c => c.Id == compositionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (composition == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        // Get relations from Relation table
        var relations = await _context.Relations
            .Where(r => r.CompositionId == compositionId)
            .ToListAsync(cancellationToken);

        if (relations.Count == 0)
            throw new InvalidOperationException($"Composition {compositionId} has no relations");

        var constantChildCount = relations.Count(r => r.ChildConstantId != null);
        var compositionChildCount = relations.Count(r => r.ChildCompositionId != null);
        var uniqueConstants = relations.Where(r => r.ChildConstantId != null).Select(r => r.ChildConstantId).Distinct().Count();
        var uniqueCompositions = relations.Where(r => r.ChildCompositionId != null).Select(r => r.ChildCompositionId).Distinct().Count();

        var centroid = composition.Geom?.Centroid;

        return new CompositionStats
        {
            Id = compositionId,
            RefCount = relations.Count,
            UniqueRefs = uniqueConstants + uniqueCompositions,
            TotalMultiplicity = relations.Sum(r => r.Multiplicity),
            GeometryType = composition.Geom?.GeometryType ?? "None",
            GeometryLength = composition.Geom?.Length ?? 0,
            CentroidX = centroid?.X ?? 0,
            CentroidY = centroid?.Y ?? 0,
            CentroidZ = centroid?.Coordinate.Z ?? 0,
            CentroidM = centroid?.Coordinate.M ?? 0,
            ConstantsReferenced = uniqueConstants,
            CompositionsReferenced = uniqueCompositions
        };
    }

    /// <summary>
    /// Search compositions by type ID
    /// </summary>
    public async Task<List<Composition>> FindByTypeIdAsync(
        long typeId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (typeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(typeId), "Type ID must be positive");
        if (limit <= 0 || limit > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 10000");

        return await _context.Compositions
            .AsNoTracking()
            .Where(c => c.TypeId == typeId)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get total counts for constants and compositions
    /// </summary>
    public async Task<NodeCounts> GetNodeCountsAsync(CancellationToken cancellationToken = default)
    {
        var constantCount = await _context.Constants
            .AsNoTracking()
            .CountAsync(cancellationToken);

        var compositionCount = await _context.Compositions
            .AsNoTracking()
            .CountAsync(cancellationToken);

        return new NodeCounts
        {
            TotalConstants = constantCount,
            TotalCompositions = compositionCount
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

public class NodeCounts
{
    public int TotalConstants { get; set; }
    public int TotalCompositions { get; set; }
    public int Total => TotalConstants + TotalCompositions;
}
