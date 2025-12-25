using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using System.Text;

namespace Hart.MCP.Core.Services;

/// <summary>
/// Atom ingestion service - converts any content into constants/compositions and persists to database
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
    /// Ingest UTF-8 text into constants/compositions
    /// Each Unicode codepoint becomes a constant
    /// Returns root composition ID
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
        var constantIds = new List<long>(codepoints.Count);
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

            constantIds.Add(codepointLookup[currentCodepoint]); // O(1), NO DB
            multiplicities.Add(count);
            
            i += count;
        }

        // Create composition (root node for this text)
        var compositionId = await CreateCompositionFromConstantsAsync(
            constantIds.ToArray(),
            multiplicities.ToArray(),
            null, // typeId - can be set by caller if needed
            cancellationToken
        );

        _logger?.LogInformation("Ingested text as composition {CompositionId} with {RefCount} references", 
            compositionId, constantIds.Count);

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
        
        // Convert to long for query (SeedValue is stored as long)
        var codepointsAsLong = uniqueCodepoints.Select(cp => (long)cp).ToList();

        // SINGLE QUERY: Get all existing constants by seed value and seedType
        var existing = await _context.Constants
            .Where(c => c.SeedType == SEED_TYPE_UNICODE && codepointsAsLong.Contains(c.SeedValue))
            .Select(c => new { c.Id, c.SeedValue })
            .ToListAsync(cancellationToken);

        // Map existing to result
        foreach (var constant in existing)
        {
            var cp = (uint)constant.SeedValue;
            result[cp] = constant.Id;
        }

        // Find missing codepoints
        var missingCodepoints = uniqueCodepoints.Where(cp => !result.ContainsKey(cp)).ToList();
        
        if (missingCodepoints.Count > 0)
        {
            // BATCH INSERT: Create all missing constants
            var newConstants = new List<(uint Codepoint, Constant Constant)>();
            
            foreach (var cp in missingCodepoints)
            {
                var contentHash = NativeLibrary.ComputeSeedHash(cp);
                var point = NativeLibrary.project_seed_to_hypersphere(cp);
                var hilbert = NativeLibrary.point_to_hilbert(point);
                var geom = _geometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

                var constant = new Constant
                {
                    HilbertHigh = (ulong)hilbert.High,
                    HilbertLow = (ulong)hilbert.Low,
                    Geom = geom,
                    SeedValue = cp,
                    SeedType = SEED_TYPE_UNICODE,
                    ContentHash = contentHash
                };

                _context.Constants.Add(constant);
                newConstants.Add((cp, constant));
            }

            // ONE SaveChanges for all new constants
            await _context.SaveChangesAsync(cancellationToken);

            // Map new constants to result
            foreach (var (cp, constant) in newConstants)
            {
                result[cp] = constant.Id;
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
    /// Get or create a constant for a Unicode codepoint
    /// Uses content hash for automatic deduplication
    /// </summary>
    public async Task<long> GetOrCreateConstantAsync(uint codepoint, CancellationToken cancellationToken = default)
    {
        // Compute deterministic hash
        var contentHash = NativeLibrary.ComputeSeedHash(codepoint);

        // Check if already exists (deduplication)
        var existing = await _context.Constants
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
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
        var constant = new Constant
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            SeedValue = codepoint,
            SeedType = SEED_TYPE_UNICODE,
            ContentHash = contentHash
        };

        _context.Constants.Add(constant);
        
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Race condition: another process created this constant
            _context.Entry(constant).State = EntityState.Detached;
            
            existing = await _context.Constants
                .Where(c => c.ContentHash == contentHash)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(cancellationToken);
                
            if (existing != 0)
                return existing;
                
            throw;
        }

        return constant.Id;
    }

    /// <summary>
    /// Create a composition from child constant IDs
    /// </summary>
    public async Task<long> CreateCompositionFromConstantsAsync(
        long[] constantIds,
        int[] multiplicities,
        long? typeId = null,
        CancellationToken cancellationToken = default)
    {
        if (constantIds == null || constantIds.Length == 0)
            throw new ArgumentException("ConstantIds cannot be null or empty", nameof(constantIds));
        if (multiplicities == null || multiplicities.Length != constantIds.Length)
            throw new ArgumentException("Multiplicities must match constantIds length", nameof(multiplicities));

        // Compute deterministic hash
        var contentHash = NativeLibrary.ComputeCompositionHash(constantIds, multiplicities);

        // Check if already exists (deduplication)
        var existing = await _context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
            return existing;

        // Load child constants to get their geometries
        var children = await _context.Constants
            .Where(c => constantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Build LINESTRING from child points (in order)
        var coordinates = new List<CoordinateZM>();
        foreach (var constId in constantIds)
        {
            if (!children.TryGetValue(constId, out var child))
                throw new InvalidOperationException($"Referenced constant {constId} not found");

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

        var composition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash,
            TypeId = typeId
        };

        _context.Compositions.Add(composition);
        
        try
        {
            await _context.SaveChangesAsync(cancellationToken);

            // Create Relation entries to link to children
            for (int i = 0; i < constantIds.Length; i++)
            {
                var relation = new Relation
                {
                    CompositionId = composition.Id,
                    ChildConstantId = constantIds[i],
                    ChildCompositionId = null,
                    Position = i,
                    Multiplicity = multiplicities[i]
                };
                _context.Relations.Add(relation);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            _context.Entry(composition).State = EntityState.Detached;
            
            existing = await _context.Compositions
                .Where(c => c.ContentHash == contentHash)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(cancellationToken);
                
            if (existing != 0)
                return existing;
                
            throw;
        }

        return composition.Id;
    }

    /// <summary>
    /// Create a composition from child composition IDs (for nested structures)
    /// </summary>
    public async Task<long> CreateCompositionFromCompositionsAsync(
        long[] compositionIds,
        int[] multiplicities,
        long? typeId = null,
        CancellationToken cancellationToken = default)
    {
        if (compositionIds == null || compositionIds.Length == 0)
            throw new ArgumentException("CompositionIds cannot be null or empty", nameof(compositionIds));
        if (multiplicities == null || multiplicities.Length != compositionIds.Length)
            throw new ArgumentException("Multiplicities must match compositionIds length", nameof(multiplicities));

        // Compute deterministic hash
        var contentHash = NativeLibrary.ComputeCompositionHash(compositionIds, multiplicities);

        // Check if already exists (deduplication)
        var existing = await _context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
            return existing;

        // Load child compositions to get their geometries
        var children = await _context.Compositions
            .Where(c => compositionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Build LINESTRING from child centroids (in order)
        var coordinates = new List<CoordinateZM>();
        foreach (var compId in compositionIds)
        {
            if (!children.TryGetValue(compId, out var child))
                throw new InvalidOperationException($"Referenced composition {compId} not found");

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

        var composition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = geom,
            ContentHash = contentHash,
            TypeId = typeId
        };

        _context.Compositions.Add(composition);
        
        try
        {
            await _context.SaveChangesAsync(cancellationToken);

            // Create Relation entries to link to child compositions
            for (int i = 0; i < compositionIds.Length; i++)
            {
                var relation = new Relation
                {
                    CompositionId = composition.Id,
                    ChildConstantId = null,
                    ChildCompositionId = compositionIds[i],
                    Position = i,
                    Multiplicity = multiplicities[i]
                };
                _context.Relations.Add(relation);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            _context.Entry(composition).State = EntityState.Detached;
            
            existing = await _context.Compositions
                .Where(c => c.ContentHash == contentHash)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(cancellationToken);
                
            if (existing != 0)
                return existing;
                
            throw;
        }

        return composition.Id;
    }

    private static CoordinateZM ExtractRepresentativeCoordinate(Geometry? geom)
    {
        if (geom == null)
            return new CoordinateZM(0, 0, 0, 0);

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
    /// Reconstruct text from composition ID
    /// </summary>
    public async Task<string> ReconstructTextAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        var composition = await _context.Compositions
            .Where(c => c.Id == compositionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (composition == null)
            throw new InvalidOperationException($"Composition {compositionId} not found");

        // Get relations from Relation table ordered by position
        var relations = await _context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);

        if (relations.Count == 0)
            return string.Empty;

        var constantIds = relations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();
            
        var constants = await _context.Constants
            .Where(c => constantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var sb = new StringBuilder();
        
        foreach (var relation in relations)
        {
            if (!relation.ChildConstantId.HasValue)
            {
                _logger?.LogWarning("Relation at position {Position} has no ChildConstantId during text reconstruction", relation.Position);
                continue;
            }

            if (!constants.TryGetValue(relation.ChildConstantId.Value, out var constant))
            {
                _logger?.LogWarning("Referenced constant {ConstId} not found during reconstruction", relation.ChildConstantId.Value);
                continue;
            }

            if (constant.SeedType != SEED_TYPE_UNICODE)
            {
                _logger?.LogWarning("Constant {ConstId} is not a Unicode constant (SeedType={SeedType})", constant.Id, constant.SeedType);
                continue;
            }

            uint codepoint = (uint)constant.SeedValue;
            string charStr = char.ConvertFromUtf32((int)codepoint);
            
            for (int m = 0; m < relation.Multiplicity; m++)
            {
                sb.Append(charStr);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get constant by ID
    /// </summary>
    public async Task<Constant?> GetConstantAsync(long constantId, CancellationToken cancellationToken = default)
    {
        return await _context.Constants
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == constantId, cancellationToken);
    }

    /// <summary>
    /// Get composition by ID
    /// </summary>
    public async Task<Composition?> GetCompositionAsync(long compositionId, CancellationToken cancellationToken = default)
    {
        return await _context.Compositions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == compositionId, cancellationToken);
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("23505") == true 
            || ex.InnerException?.Message.Contains("duplicate key") == true;
    }
}
