using System.Diagnostics;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Seeds the database with all 1,114,112 Unicode codepoints as constant atoms.
/// 
/// ARCHITECTURE:
/// - All Unicode codepoints (0x000000 - 0x10FFFF) are pre-seeded
/// - Each codepoint is projected onto the 4D unit hypersphere
/// - Stored with Hilbert indices for efficient spatial queries
/// - This is a ONE-TIME operation at database creation
/// 
/// PERFORMANCE:
/// - Bulk insert using AddRange + single SaveChanges
/// - ~1.1M atoms in < 30 seconds on decent hardware
/// - Parallel geometry computation, sequential DB write
/// </summary>
public class UnicodeSeedingService
{
    private readonly HartDbContext _context;
    private readonly ILogger<UnicodeSeedingService>? _logger;
    private readonly GeometryFactory _geometryFactory;

    // Unicode constants
    public const int MAX_UNICODE = 0x10FFFF;
    public const int TOTAL_CODEPOINTS = MAX_UNICODE + 1; // 1,114,112
    
    // Batch size for DB inserts (too large = memory issues, too small = slow)
    private const int BATCH_SIZE = 50_000;

    public UnicodeSeedingService(HartDbContext context, ILogger<UnicodeSeedingService>? logger = null)
    {
        _context = context;
        _logger = logger;
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
    }

    /// <summary>
    /// Check if Unicode seeding has already been done.
    /// </summary>
    public async Task<bool> IsSeedededAsync(CancellationToken ct = default)
    {
        // Check for presence of a few key codepoints
        var hasAscii = await _context.Constants
            .AnyAsync(c => c.SeedType == 0 && c.SeedValue == 'A', ct);
        
        if (!hasAscii) return false;
        
        // Check for max unicode
        var hasMaxUnicode = await _context.Constants
            .AnyAsync(c => c.SeedType == 0 && c.SeedValue == MAX_UNICODE, ct);
            
        return hasMaxUnicode;
    }

    /// <summary>
    /// Seed all Unicode codepoints. Idempotent - skips if already seeded.
    /// </summary>
    public async Task<UnicodeSeedResult> SeedAllAsync(
        IProgress<UnicodeSeedProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new UnicodeSeedResult();

        // Check if already seeded
        if (await IsSeedededAsync(ct))
        {
            _logger?.LogInformation("Unicode already seeded, skipping");
            result.WasAlreadySeeded = true;
            result.TotalCodepoints = await _context.Constants
                .CountAsync(c => c.SeedType == 0, ct);
            return result;
        }

        _logger?.LogInformation("Seeding {Count:N0} Unicode codepoints...", TOTAL_CODEPOINTS);

        // Process in batches
        int seeded = 0;
        for (int batchStart = 0; batchStart <= MAX_UNICODE; batchStart += BATCH_SIZE)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BATCH_SIZE - 1, MAX_UNICODE);
            var batchSize = batchEnd - batchStart + 1;

            var batchSw = Stopwatch.StartNew();

            // Generate constants for this batch (parallel computation)
            var constants = GenerateUnicodeConstantsBatch(batchStart, batchEnd);

            // Bulk insert
            _context.Constants.AddRange(constants);
            await _context.SaveChangesAsync(ct);

            seeded += constants.Count;
            batchSw.Stop();

            var rate = constants.Count * 1000.0 / batchSw.ElapsedMilliseconds;
            _logger?.LogDebug(
                "Batch {Start:X6}-{End:X6}: {Count:N0} constants in {Time}ms ({Rate:N0}/sec)",
                batchStart, batchEnd, constants.Count, batchSw.ElapsedMilliseconds, rate);

            progress?.Report(new UnicodeSeedProgress
            {
                CodepointsSeeded = seeded,
                TotalCodepoints = TOTAL_CODEPOINTS,
                CurrentBatchStart = batchStart,
                CurrentBatchEnd = batchEnd,
                BatchTimeMs = batchSw.ElapsedMilliseconds
            });
        }

        sw.Stop();
        result.TotalCodepoints = seeded;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        result.Rate = seeded * 1000.0 / sw.ElapsedMilliseconds;

        _logger?.LogInformation(
            "Unicode seeding complete: {Count:N0} codepoints in {Time:N0}ms ({Rate:N0}/sec)",
            result.TotalCodepoints, result.ElapsedMs, result.Rate);

        return result;
    }

    /// <summary>
    /// Generate constants for a range of Unicode codepoints.
    /// Uses parallel processing for geometry computation.
    /// </summary>
    private List<Constant> GenerateUnicodeConstantsBatch(int start, int end)
    {
        var count = end - start + 1;
        var constants = new Constant[count];

        // Parallel computation of geometry (CPU-bound, perfect for parallel)
        Parallel.For(0, count, i =>
        {
            var codepoint = (uint)(start + i);
            
            // Project to hypersphere
            var point = NativeLibrary.project_seed_to_hypersphere(codepoint);
            var hilbert = NativeLibrary.point_to_hilbert(point);
            var contentHash = NativeLibrary.ComputeSeedHash(codepoint);

            // Create geometry
            var geom = _geometryFactory.CreatePoint(
                new CoordinateZM(point.X, point.Y, point.Z, point.M));

            constants[i] = new Constant
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                SeedValue = codepoint,
                SeedType = 0, // SEED_TYPE_UNICODE
                ContentHash = contentHash
            };
        });

        return constants.ToList();
    }

    /// <summary>
    /// Seed only ASCII (0-127) for quick tests.
    /// </summary>
    public async Task<int> SeedAsciiOnlyAsync(CancellationToken ct = default)
    {
        var constants = GenerateUnicodeConstantsBatch(0, 127);
        _context.Constants.AddRange(constants);
        await _context.SaveChangesAsync(ct);
        return constants.Count;
    }

    /// <summary>
    /// Seed Basic Multilingual Plane (0-0xFFFF) for faster testing.
    /// </summary>
    public async Task<int> SeedBMPAsync(
        IProgress<UnicodeSeedProgress>? progress = null,
        CancellationToken ct = default)
    {
        const int BMP_MAX = 0xFFFF;
        int seeded = 0;

        for (int batchStart = 0; batchStart <= BMP_MAX; batchStart += BATCH_SIZE)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BATCH_SIZE - 1, BMP_MAX);
            var constants = GenerateUnicodeConstantsBatch(batchStart, batchEnd);

            _context.Constants.AddRange(constants);
            await _context.SaveChangesAsync(ct);

            seeded += constants.Count;

            progress?.Report(new UnicodeSeedProgress
            {
                CodepointsSeeded = seeded,
                TotalCodepoints = BMP_MAX + 1,
                CurrentBatchStart = batchStart,
                CurrentBatchEnd = batchEnd
            });
        }

        return seeded;
    }
}

public class UnicodeSeedResult
{
    public int TotalCodepoints { get; set; }
    public long ElapsedMs { get; set; }
    public double Rate { get; set; }
    public bool WasAlreadySeeded { get; set; }
}

public class UnicodeSeedProgress
{
    public int CodepointsSeeded { get; set; }
    public int TotalCodepoints { get; set; }
    public int CurrentBatchStart { get; set; }
    public int CurrentBatchEnd { get; set; }
    public long BatchTimeMs { get; set; }
    public double PercentComplete => 100.0 * CodepointsSeeded / TotalCodepoints;
}
