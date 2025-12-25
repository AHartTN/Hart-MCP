using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using System.Text;

namespace Hart.MCP.Core.Services;

/// <summary>
/// Atom ingestion service - converts any content into atoms and persists to database
/// Handles: Unicode text, bytes, images, models, everything
/// </summary>
public class AtomIngestionService
{
    private readonly HartDbContext _context;
    private readonly GeometryFactory _geometryFactory;
    private readonly ILogger<AtomIngestionService>? _logger;

    // Seed type constants (matching C native library)
    private const int SEED_TYPE_UNICODE = 0;
    private const int SEED_TYPE_INTEGER = 1;
    private const int SEED_TYPE_FLOAT_BITS = 2;

    public AtomIngestionService(HartDbContext context, ILogger<AtomIngestionService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0); // SRID 0
        _logger = logger;
    }

    /// <summary>
    /// Ingest UTF-8 text into atoms
    /// Each Unicode codepoint becomes a constant atom
    /// Returns root composition atom ID
    /// </summary>
    public async Task<long> IngestTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));

        _logger?.LogInformation("Ingesting text of length {Length}", text.Length);

        // Convert text to Unicode codepoints
        var codepoints = ExtractCodepoints(text);

        if (codepoints.Count == 0)
            throw new ArgumentException("Text contains no valid codepoints", nameof(text));

        // ============================================
        // PHASE 1: Extract ALL unique codepoints (memory only)
        // ============================================
        var uniqueCodepoints = new HashSet<uint>(codepoints);
        _logger?.LogDebug("Found {UniqueCount} unique codepoints in {Total} chars", 
            uniqueCodepoints.Count, codepoints.Count);

        // ============================================
        // PHASE 2: BULK create ALL codepoint constants (ONE DB round-trip)
        // ============================================
        var codepointLookup = await BulkGetOrCreateConstantsAsync(
            uniqueCodepoints.ToArray(), cancellationToken);

        // ============================================
        // PHASE 3: Build composition using lookup (no DB calls in loop)
        // ============================================
        var atomIds = new List<long>(codepoints.Count);
        var multiplicities = new List<int>(codepoints.Count);

        // Use RLE compression - consecutive identical codepoints get multiplicity > 1
        int i = 0;
        while (i < codepoints.Count)
        {
            uint currentCodepoint = codepoints[i];
            int count = 1;
            
            // Count consecutive identical codepoints
            while (i + count < codepoints.Count && codepoints[i + count] == currentCodepoint)
            {
                count++;
            }

            atomIds.Add(codepointLookup[currentCodepoint]); // O(1), NO DB
            multiplicities.Add(count);
            
            i += count;
        }

        // Create composition atom (root node for this text)
        var compositionId = await CreateCompositionAsync(
            atomIds.ToArray(),
            multiplicities.ToArray(),
            "text",
            cancellationToken
        );

        _logger?.LogInformation("Ingested text as composition atom {AtomId} with {RefCount} references", 
            compositionId, atomIds.Count);

        return compositionId;
    }

    /// <summary>
    /// BULK get or create constants for multiple codepoints.
    /// Queries DB ONCE for all existing, batch inserts all missing.
    /// </summary>
    private async Task<Dictionary<uint, long>> BulkGetOrCreateConstantsAsync(
        uint[] codepoints,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<uint, long>(codepoints.Length);
        
        // Get unique codepoints
        var uniqueCodepoints = codepoints.Distinct().ToArray();
        
        // Convert to long for query (SeedValue is stored as long?)
        var codepointsAsLong = uniqueCodepoints.Select(cp => (long)cp).ToList();

        // SINGLE QUERY: Get all existing constants by seed value and type
        // Using SeedValue instead of ContentHash avoids byte[] comparison issues in InMemory provider
        var existing = await _context.Atoms
            .Where(a => a.IsConstant && a.AtomType == "char" && a.SeedValue.HasValue && codepointsAsLong.Contains(a.SeedValue.Value))
            .Select(a => new { a.Id, a.SeedValue })
            .ToListAsync(cancellationToken);

        // Map existing to result
        foreach (var atom in existing)
        {
            if (atom.SeedValue.HasValue)
            {
                var cp = (uint)atom.SeedValue.Value;
                result[cp] = atom.Id;
            }
        }

        // Find missing codepoints
        var missingCodepoints = uniqueCodepoints.Where(cp => !result.ContainsKey(cp)).ToList();
        
        if (missingCodepoints.Count > 0)
        {
            // BATCH INSERT: Create all missing atoms
            var newAtoms = new List<(uint Codepoint, Atom Atom)>();
            
            foreach (var cp in missingCodepoints)
            {
                var contentHash = NativeLibrary.ComputeSeedHash(cp);
                var point = NativeLibrary.project_seed_to_hypersphere(cp);
                var hilbert = NativeLibrary.point_to_hilbert(point);
                var geom = _geometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

                var atom = new Atom
                {
                    HilbertHigh = hilbert.High,
                    HilbertLow = hilbert.Low,
                    Geom = geom,
                    IsConstant = true,
                    SeedValue = cp,
                    SeedType = SEED_TYPE_UNICODE,
                    ContentHash = contentHash,
                    AtomType = "char"
                };

                _context.Atoms.Add(atom);
                newAtoms.Add((cp, atom));
            }

            // ONE SaveChanges for all new atoms
            await _context.SaveChangesAsync(cancellationToken);

            // Map new atoms to result
            foreach (var (cp, atom) in newAtoms)
            {
                result[cp] = atom.Id;
            }
        }

        return result;
    }

    /// <summary>
    /// Extract Unicode codepoints from a string, handling surrogate pairs correctly
    /// </summary>
    private static List<uint> ExtractCodepoints(string text)
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
                    i++; // Skip low surrogate
                    continue;
                }
            }
            
            codepoints.Add(c);
        }

        return codepoints;
    }

    /// <summary>
    /// Get or create a constant atom for a Unicode codepoint
    /// Uses content hash for automatic deduplication
    /// </summary>
    public async Task<long> GetOrCreateConstantAsync(uint codepoint, CancellationToken cancellationToken = default)
    {
        // Compute deterministic hash
        var contentHash = NativeLibrary.ComputeSeedHash(codepoint);

        // Check if already exists (deduplication)
        var existing = await _context.Atoms
            .Where(a => a.ContentHash == contentHash && a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
            return existing;

        // Project to hypersphere
        var point = NativeLibrary.project_seed_to_hypersphere(codepoint);
        var hilbert = NativeLibrary.point_to_hilbert(point);

        // Create geometry (POINTZM)
        var geom = _geometryFactory.CreatePoint(
            new CoordinateZM(point.X, point.Y, point.Z, point.M)
        );

        // Insert new constant
        var atom = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = geom,
            IsConstant = true,
            SeedValue = codepoint,
            SeedType = SEED_TYPE_UNICODE,
            Refs = null,
            Multiplicities = null,
            ContentHash = contentHash,
            AtomType = "char"
        };

        _context.Atoms.Add(atom);
        
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Race condition: another process created this atom
            _context.Entry(atom).State = EntityState.Detached;
            
            existing = await _context.Atoms
                .Where(a => a.ContentHash == contentHash && a.IsConstant)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken);
                
            if (existing != 0)
                return existing;
                
            throw;
        }

        return atom.Id;
    }

    /// <summary>
    /// Create a composition atom from child atom IDs
    /// </summary>
    public async Task<long> CreateCompositionAsync(
        long[] refs, 
        int[] multiplicities,
        string atomType = "composition",
        CancellationToken cancellationToken = default)
    {
        if (refs == null || refs.Length == 0)
            throw new ArgumentException("Refs cannot be null or empty", nameof(refs));
        if (multiplicities == null || multiplicities.Length != refs.Length)
            throw new ArgumentException("Multiplicities must match refs length", nameof(multiplicities));

        // Compute deterministic hash
        var contentHash = NativeLibrary.ComputeCompositionHash(refs, multiplicities);

        // Check if already exists (deduplication)
        var existing = await _context.Atoms
            .Where(a => a.ContentHash == contentHash && !a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
            return existing;

        // Load child atoms to get their geometries
        var children = await _context.Atoms
            .Where(a => refs.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        // Build LINESTRING from child points (in order)
        var coordinates = new List<CoordinateZM>();
        foreach (var refId in refs)
        {
            if (!children.TryGetValue(refId, out var child))
                throw new InvalidOperationException($"Referenced atom {refId} not found");

            var coord = ExtractRepresentativeCoordinate(child.Geom);
            coordinates.Add(coord);
        }

        // Create geometry
        Geometry geom;
        if (coordinates.Count == 1)
        {
            geom = _geometryFactory.CreatePoint(coordinates[0]);
        }
        else
        {
            geom = _geometryFactory.CreateLineString(coordinates.ToArray());
        }

        // Compute Hilbert index from centroid
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

        _context.Atoms.Add(composition);
        
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            _context.Entry(composition).State = EntityState.Detached;
            
            existing = await _context.Atoms
                .Where(a => a.ContentHash == contentHash && !a.IsConstant)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken);
                
            if (existing != 0)
                return existing;
                
            throw;
        }

        return composition.Id;
    }

    private static CoordinateZM ExtractRepresentativeCoordinate(Geometry geom)
    {
        if (geom is Point p)
        {
            var coord = p.Coordinate;
            return new CoordinateZM(
                coord.X, 
                coord.Y, 
                double.IsNaN(coord.Z) ? 0 : coord.Z,
                double.IsNaN(coord.M) ? 0 : coord.M
            );
        }
        
        var centroid = geom.Centroid;
        var c = centroid.Coordinate;
        return new CoordinateZM(
            c.X, 
            c.Y, 
            double.IsNaN(c.Z) ? 0 : c.Z,
            double.IsNaN(c.M) ? 0 : c.M
        );
    }

    /// <summary>
    /// Reconstruct text from composition atom ID
    /// </summary>
    public async Task<string> ReconstructTextAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        var composition = await _context.Atoms
            .Where(a => a.Id == compositionId && !a.IsConstant)
            .FirstOrDefaultAsync(cancellationToken);

        if (composition == null)
            throw new InvalidOperationException($"Composition atom {compositionId} not found");

        if (composition.Refs == null || composition.Refs.Length == 0)
            return string.Empty;

        if (composition.Multiplicities == null || composition.Multiplicities.Length != composition.Refs.Length)
            throw new InvalidOperationException($"Composition {compositionId} has invalid multiplicities");

        var refIds = composition.Refs;
        var constants = await _context.Atoms
            .Where(a => refIds.Contains(a.Id) && a.IsConstant)
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var sb = new StringBuilder();
        
        for (int i = 0; i < composition.Refs.Length; i++)
        {
            var refId = composition.Refs[i];
            var multiplicity = composition.Multiplicities[i];
            
            if (!constants.TryGetValue(refId, out var constant))
            {
                _logger?.LogWarning("Referenced atom {RefId} not found during reconstruction", refId);
                continue;
            }

            if (constant.SeedType != SEED_TYPE_UNICODE || constant.SeedValue == null)
            {
                _logger?.LogWarning("Atom {AtomId} is not a Unicode constant", refId);
                continue;
            }

            uint codepoint = (uint)constant.SeedValue.Value;
            string charStr = char.ConvertFromUtf32((int)codepoint);
            
            for (int m = 0; m < multiplicity; m++)
            {
                sb.Append(charStr);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get atom by ID
    /// </summary>
    public async Task<Atom?> GetAtomAsync(long atomId, CancellationToken cancellationToken = default)
    {
        return await _context.Atoms
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == atomId, cancellationToken);
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("23505") == true 
            || ex.InnerException?.Message.Contains("duplicate key") == true;
    }
}
