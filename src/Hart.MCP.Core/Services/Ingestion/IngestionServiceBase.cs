using System.Security.Cryptography;
using System.Text;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Base class for all ingestion services.
/// Provides common atom creation and deduplication logic.
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

    protected IngestionServiceBase(HartDbContext context, ILogger? logger = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        GeometryFactory = new GeometryFactory(new PrecisionModel(), 0);
        Logger = logger;
    }

    /// <summary>
    /// Compute content hash that includes both seed hash and atom type.
    /// This ensures semantic differentiation (e.g., 1 as bool vs 1 as int).
    /// </summary>
    protected static byte[] ComputeTypedContentHash(byte[] seedHash, string atomType)
    {
        using var sha256 = SHA256.Create();
        var typeBytes = Encoding.UTF8.GetBytes(atomType);
        var combined = new byte[seedHash.Length + typeBytes.Length];
        seedHash.CopyTo(combined, 0);
        typeBytes.CopyTo(combined, seedHash.Length);
        return sha256.ComputeHash(combined);
    }

    /// <summary>
    /// Get or create a constant atom for a seed value.
    /// Automatically deduplicates via content hash.
    /// Content hash includes atomType because same value can have different semantics
    /// (e.g., 1 as json_bool means "true", 1 as json_int means number 1)
    /// </summary>
    protected async Task<long> GetOrCreateConstantAsync(
        uint seedValue,
        int seedType,
        string atomType,
        CancellationToken ct = default)
    {
        // Content hash includes atom type for semantic differentiation
        var baseHash = NativeLibrary.ComputeSeedHash(seedValue);
        var contentHash = ComputeTypedContentHash(baseHash, atomType);

        var existing = await Context.Atoms
            .Where(a => a.ContentHash == contentHash && a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        var point = NativeLibrary.project_seed_to_hypersphere(seedValue);
        var hilbert = NativeLibrary.point_to_hilbert(point);
        var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

        var atom = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = geom,
            IsConstant = true,
            SeedValue = seedValue,
            SeedType = seedType,
            Refs = null,
            Multiplicities = null,
            ContentHash = contentHash,
            AtomType = atomType
        };

        Context.Atoms.Add(atom);

        try
        {
            await Context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            Context.Entry(atom).State = EntityState.Detached;
            existing = await Context.Atoms
                .Where(a => a.ContentHash == contentHash && a.IsConstant)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != 0) return existing;
            throw;
        }

        return atom.Id;
    }

    /// <summary>
    /// Get or create a constant atom from a 64-bit integer seed.
    /// Uses both high and low 32-bit parts for full precision.
    /// </summary>
    protected async Task<long> GetOrCreateIntegerConstantAsync(
        long value,
        string atomType,
        CancellationToken ct = default)
    {
        // Use the low 32 bits as seed, store full value in SeedValue
        uint seedLow = (uint)(value & 0xFFFFFFFF);
        var baseHash = NativeLibrary.ComputeSeedHash(seedLow);
        
        // For full 64-bit, we need to incorporate high bits into hash
        var highSeed = (uint)((value >> 32) & 0xFFFFFFFF);
        if (highSeed != 0 && value >= 0 || value < 0)
        {
            // For values that don't fit in 32 bits, create unique hash
            baseHash = NativeLibrary.ComputeCompositionHash(
                new long[] { seedLow, highSeed },
                new int[] { 1, 1 }
            );
        }

        // Include atom type in content hash for semantic differentiation
        var contentHash = ComputeTypedContentHash(baseHash, atomType);

        var existing = await Context.Atoms
            .Where(a => a.ContentHash == contentHash && a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        var point = NativeLibrary.project_seed_to_hypersphere(seedLow);
        var hilbert = NativeLibrary.point_to_hilbert(point);
        var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

        var atom = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = geom,
            IsConstant = true,
            SeedValue = value,
            SeedType = SEED_TYPE_INTEGER,
            Refs = null,
            Multiplicities = null,
            ContentHash = contentHash,
            AtomType = atomType
        };

        Context.Atoms.Add(atom);

        try
        {
            await Context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            Context.Entry(atom).State = EntityState.Detached;
            existing = await Context.Atoms
                .Where(a => a.ContentHash == contentHash && a.IsConstant)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != 0) return existing;
            throw;
        }

        return atom.Id;
    }

    /// <summary>
    /// Create a composition atom from child references.
    /// Automatically deduplicates via content hash.
    /// </summary>
    protected async Task<long> CreateCompositionAsync(
        long[] refs,
        int[] multiplicities,
        string atomType,
        CancellationToken ct = default)
    {
        if (refs == null || refs.Length == 0)
            throw new ArgumentException("Refs cannot be empty", nameof(refs));
        if (multiplicities == null || multiplicities.Length != refs.Length)
            throw new ArgumentException("Multiplicities must match refs", nameof(multiplicities));

        var contentHash = NativeLibrary.ComputeCompositionHash(refs, multiplicities);

        var existing = await Context.Atoms
            .Where(a => a.ContentHash == contentHash && !a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        var children = await Context.Atoms
            .Where(a => refs.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var coordinates = new List<CoordinateZM>();
        foreach (var refId in refs)
        {
            if (!children.TryGetValue(refId, out var child))
                throw new InvalidOperationException($"Referenced atom {refId} not found");
            coordinates.Add(ExtractCoordinate(child.Geom));
        }

        Geometry geom = coordinates.Count == 1
            ? GeometryFactory.CreatePoint(coordinates[0])
            : GeometryFactory.CreateLineString(coordinates.ToArray());

        var centroid = geom.Centroid.Coordinate;
        var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
        {
            X = centroid.X,
            Y = centroid.Y,
            Z = double.IsNaN(centroid.Z) ? 0 : centroid.Z,
            M = double.IsNaN(centroid.M) ? 0 : centroid.M
        });

        var composition = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = geom,
            IsConstant = false,
            SeedValue = null,
            SeedType = null,
            Refs = refs,
            Multiplicities = multiplicities,
            ContentHash = contentHash,
            AtomType = atomType
        };

        Context.Atoms.Add(composition);

        try
        {
            await Context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            Context.Entry(composition).State = EntityState.Detached;
            existing = await Context.Atoms
                .Where(a => a.ContentHash == contentHash && !a.IsConstant)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (existing != 0) return existing;
            throw;
        }

        return composition.Id;
    }

    /// <summary>
    /// BULK get or create constants - queries DB ONCE for all existing, 
    /// then batch inserts all missing in ONE transaction.
    /// Returns dictionary mapping seed value to atom ID.
    /// </summary>
    protected async Task<Dictionary<uint, long>> BulkGetOrCreateConstantsAsync(
        uint[] seedValues,
        int seedType,
        string atomType,
        CancellationToken ct = default)
    {
        var result = new Dictionary<uint, long>(seedValues.Length);
        
        // Get unique seeds
        var uniqueSeeds = seedValues.Distinct().ToArray();
        
        // Convert to long array for query (SeedValue is stored as long?)
        var seedsAsLong = uniqueSeeds.Select(s => (long)s).ToList();

        // SINGLE QUERY: Get all existing constants by seed value and type
        // Using SeedValue instead of ContentHash avoids byte[] comparison issues
        var existing = await Context.Atoms
            .Where(a => a.IsConstant && a.AtomType == atomType && a.SeedValue.HasValue && seedsAsLong.Contains(a.SeedValue.Value))
            .Select(a => new { a.Id, a.SeedValue })
            .ToListAsync(ct);

        // Map existing to result
        foreach (var atom in existing)
        {
            if (atom.SeedValue.HasValue)
            {
                var seed = (uint)atom.SeedValue.Value;
                result[seed] = atom.Id;
            }
        }

        // Find missing seeds
        var missingSeeds = uniqueSeeds.Where(s => !result.ContainsKey(s)).ToList();
        
        if (missingSeeds.Count > 0)
        {
            // BATCH INSERT: Create all missing atoms
            var newAtoms = new List<(uint Seed, Atom Atom)>();
            
            foreach (var seed in missingSeeds)
            {
                var baseHash = NativeLibrary.ComputeSeedHash(seed);
                var hash = ComputeTypedContentHash(baseHash, atomType);
                var point = NativeLibrary.project_seed_to_hypersphere(seed);
                var hilbert = NativeLibrary.point_to_hilbert(point);
                var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

                var atom = new Atom
                {
                    HilbertHigh = hilbert.High,
                    HilbertLow = hilbert.Low,
                    Geom = geom,
                    IsConstant = true,
                    SeedValue = seed,
                    SeedType = seedType,
                    ContentHash = hash,
                    AtomType = atomType
                };

                Context.Atoms.Add(atom);
                newAtoms.Add((seed, atom));
            }

            // ONE SaveChanges for all new atoms
            await Context.SaveChangesAsync(ct);

            // Map new atoms to result
            foreach (var (seed, atom) in newAtoms)
            {
                result[seed] = atom.Id;
            }
        }

        return result;
    }

    /// <summary>
    /// Batch create constants for efficiency (legacy - uses per-item DB queries).
    /// Use BulkGetOrCreateConstantsAsync for better performance.
    /// </summary>
    protected async Task<long[]> BatchGetOrCreateConstantsAsync(
        uint[] seedValues,
        int seedType,
        string atomType,
        CancellationToken ct = default)
    {
        // Use the bulk method and map back to array
        var dict = await BulkGetOrCreateConstantsAsync(seedValues, seedType, atomType, ct);
        var results = new long[seedValues.Length];
        for (int i = 0; i < seedValues.Length; i++)
        {
            results[i] = dict[seedValues[i]];
        }
        return results;
    }

    private static CoordinateZM ExtractCoordinate(Geometry geom)
    {
        var coord = geom is Point p ? p.Coordinate : geom.Centroid.Coordinate;
        return new CoordinateZM(
            coord.X,
            coord.Y,
            double.IsNaN(coord.Z) ? 0 : coord.Z,
            double.IsNaN(coord.M) ? 0 : coord.M
        );
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("duplicate key") == true;
    }
}

/// <summary>
/// Comparer for using byte arrays as dictionary keys.

