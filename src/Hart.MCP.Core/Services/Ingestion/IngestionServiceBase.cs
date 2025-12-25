using System.Security.Cryptography;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Base class for all ingestion services.
/// Uses Constant/Composition/Relation tables (no arrays).
/// </summary>
public abstract class IngestionServiceBase
{
    protected readonly HartDbContext Context;
    protected readonly GeometryFactory GeometryFactory;
    protected readonly ILogger? Logger;

    // Seed type constants (matching C native library)
    protected const int SEED_TYPE_UNICODE = 0;
    protected const int SEED_TYPE_INTEGER = 1;
    protected const int SEED_TYPE_FLOAT_BITS = 2;
    protected const int SEED_TYPE_BYTE = 3;
    protected const int SEED_TYPE_BOOLEAN = 4;  // JSON booleans - distinct from integers
    protected const int SEED_TYPE_JSON_NULL = 5; // JSON null - distinct from other special values

    protected IngestionServiceBase(HartDbContext context, ILogger? logger = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        GeometryFactory = new GeometryFactory(new PrecisionModel(), 0);
        Logger = logger;
    }

    /// <summary>
    /// Compute content hash that includes seed hash and seed type.
    /// </summary>
    protected static byte[] ComputeTypedContentHash(byte[] seedHash, int seedType)
    {
        using var sha256 = SHA256.Create();
        var typeBytes = BitConverter.GetBytes(seedType);
        var combined = new byte[seedHash.Length + typeBytes.Length];
        seedHash.CopyTo(combined, 0);
        typeBytes.CopyTo(combined, seedHash.Length);
        return sha256.ComputeHash(combined);
    }

    /// <summary>
    /// Compute content hash for a composition, including child types.
    /// This prevents collisions between compositions with same IDs but different child types.
    /// </summary>
    protected static byte[] ComputeCompositionContentHash(
        (long id, bool isConstant)[] children,
        int[] multiplicities)
    {
        using var sha256 = SHA256.Create();
        
        // Get base hash from native library
        var childIds = children.Select(c => c.id).ToArray();
        var baseHash = NativeLibrary.ComputeCompositionHash(childIds, multiplicities);
        
        // Add child type indicators to make hash unique based on constant vs composition
        // Each child contributes 1 bit: 0 = composition, 1 = constant
        var typeBytes = new byte[(children.Length + 7) / 8];
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i].isConstant)
                typeBytes[i / 8] |= (byte)(1 << (i % 8));
        }
        
        // Combine base hash with type bytes
        var combined = new byte[baseHash.Length + typeBytes.Length];
        baseHash.CopyTo(combined, 0);
        typeBytes.CopyTo(combined, baseHash.Length);
        
        return sha256.ComputeHash(combined);
    }

    /// <summary>
    /// Get or create a constant for a seed value.
    /// Automatically deduplicates via content hash.
    /// </summary>
    protected async Task<long> GetOrCreateConstantAsync(
        long seedValue,
        int seedType,
        CancellationToken ct = default)
    {
        var baseHash = NativeLibrary.ComputeSeedHash((uint)seedValue);
        var contentHash = ComputeTypedContentHash(baseHash, seedType);

        var existing = await Context.Constants
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        var point = NativeLibrary.project_seed_to_hypersphere((uint)seedValue);
        var hilbert = NativeLibrary.point_to_hilbert(point);
        var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

        var constant = new Constant
        {
            SeedValue = seedValue,
            SeedType = seedType,
            ContentHash = contentHash,
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom
        };

        Context.Constants.Add(constant);

        try
        {
            await Context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            Context.Entry(constant).State = EntityState.Detached;
            existing = await Context.Constants
                .Where(c => c.ContentHash == contentHash)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != 0) return existing;
            throw;
        }

        return constant.Id;
    }

    /// <summary>
    /// Get or create a constant for a uint seed value.
    /// </summary>
    protected Task<long> GetOrCreateConstantAsync(
        uint seedValue,
        int seedType,
        CancellationToken ct = default)
        => GetOrCreateConstantAsync((long)seedValue, seedType, ct);

    /// <summary>
    /// Create a composition from an ordered list of children.
    /// Children can be constants or compositions.
    /// </summary>
    protected async Task<long> CreateCompositionAsync(
        (long id, bool isConstant)[] children,
        int[] multiplicities,
        long? typeId = null,
        CancellationToken ct = default)
    {
        if (children.Length == 0)
            throw new ArgumentException("Composition must have at least one child", nameof(children));

        if (children.Length != multiplicities.Length)
            throw new ArgumentException("Children and multiplicities must have same length");

        // Compute content hash from children
        // Include child type (constant vs composition) to prevent collisions
        var contentHash = ComputeCompositionContentHash(children, multiplicities);

        var existing = await Context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Compute geometry from children (centroid approach)
        var (hilbert, geom) = await ComputeCompositionGeometryAsync(children, ct);

        var composition = new Composition
        {
            ContentHash = contentHash,
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            TypeId = typeId
        };

        Context.Compositions.Add(composition);
        await Context.SaveChangesAsync(ct);

        // Create relations
        for (int i = 0; i < children.Length; i++)
        {
            var (childId, isConstant) = children[i];
            var relation = new Relation
            {
                CompositionId = composition.Id,
                Position = i,
                Multiplicity = multiplicities[i]
            };

            if (isConstant)
                relation.ChildConstantId = childId;
            else
                relation.ChildCompositionId = childId;

            Context.Relations.Add(relation);
        }

        await Context.SaveChangesAsync(ct);
        return composition.Id;
    }

    /// <summary>
    /// Create a composition from constant children only.
    /// </summary>
    protected Task<long> CreateCompositionFromConstantsAsync(
        long[] constantIds,
        int[] multiplicities,
        long? typeId = null,
        CancellationToken ct = default)
    {
        var children = constantIds.Select(id => (id, isConstant: true)).ToArray();
        return CreateCompositionAsync(children, multiplicities, typeId, ct);
    }

    /// <summary>
    /// Get the children of a composition in order.
    /// </summary>
    protected async Task<List<(long id, bool isConstant, int multiplicity)>> GetCompositionChildrenAsync(
        long compositionId,
        CancellationToken ct = default)
    {
        var relations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .Select(r => new { r.ChildConstantId, r.ChildCompositionId, r.Multiplicity })
            .ToListAsync(ct);

        return relations.Select(r =>
        {
            if (r.ChildConstantId.HasValue)
                return (r.ChildConstantId.Value, isConstant: true, r.Multiplicity);
            else
                return (r.ChildCompositionId!.Value, isConstant: false, r.Multiplicity);
        }).ToList();
    }

    /// <summary>
    /// Compute geometry for a composition from its children.
    /// </summary>
    private async Task<(NativeLibrary.HilbertIndex hilbert, Geometry geom)> ComputeCompositionGeometryAsync(
        (long id, bool isConstant)[] children,
        CancellationToken ct)
    {
        var constantIds = children.Where(c => c.isConstant).Select(c => c.id).ToArray();
        var compositionIds = children.Where(c => !c.isConstant).Select(c => c.id).ToArray();

        var constantGeoms = constantIds.Length > 0
            ? await Context.Constants
                .Where(c => constantIds.Contains(c.Id))
                .Select(c => c.Geom)
                .ToListAsync(ct)
            : new List<Geometry?>();

        var compositionGeoms = compositionIds.Length > 0
            ? await Context.Compositions
                .Where(c => compositionIds.Contains(c.Id))
                .Select(c => c.Geom)
                .ToListAsync(ct)
            : new List<Geometry?>();

        var allGeoms = constantGeoms.Concat(compositionGeoms)
            .Where(g => g != null)
            .ToList();

        if (allGeoms.Count == 0)
        {
            var defaultPoint = GeometryFactory.CreatePoint(new CoordinateZM(0, 0, 0, 0));
            var defaultHilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM { X = 0, Y = 0, Z = 0, M = 0 });
            return (defaultHilbert, defaultPoint);
        }

        // Compute centroid
        double avgX = 0, avgY = 0, avgZ = 0, avgM = 0;
        foreach (var g in allGeoms)
        {
            var coord = g!.Centroid.Coordinate;
            avgX += coord.X;
            avgY += coord.Y;
            avgZ += double.IsNaN(coord.Z) ? 0 : coord.Z;
            avgM += double.IsNaN(coord.M) ? 0 : coord.M;
        }

        avgX /= allGeoms.Count;
        avgY /= allGeoms.Count;
        avgZ /= allGeoms.Count;
        avgM /= allGeoms.Count;

        var centroid = GeometryFactory.CreatePoint(new CoordinateZM(avgX, avgY, avgZ, avgM));
        var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
        {
            X = avgX, Y = avgY, Z = avgZ, M = avgM
        });

        return (hilbert, centroid);
    }

    protected static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true;
    }
}
