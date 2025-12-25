using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Hart.MCP.Core.Services;

/// <summary>
/// Fast text export service using breadth-first traversal with batch queries and caching.
/// 
/// KEY OPTIMIZATIONS:
/// 1. BFS traversal (1→2→4→8) instead of recursive DFS
/// 2. Batch load children in single queries
/// 3. Cache reconstructed patterns (content-addressed = immutable)
/// 4. Stream output for large texts
/// 
/// The Merkle DAG structure means:
/// - Root Composition contains everything
/// - Each level expands geometrically
/// - Constants (chars) are at the leaves
/// - Hilbert curves, projections = deterministic from SeedValue
/// 
/// Schema:
/// - Constant: leaf nodes (Unicode codepoints) with SeedValue, SeedType
/// - Composition: internal nodes with Relations to children
/// - Relation: edges linking Composition to child Constants or Compositions
/// </summary>
public class TextExportService
{
    private readonly HartDbContext _context;
    private readonly ILogger<TextExportService>? _logger;

    // Cache for reconstructed patterns - content-addressed means immutable
    // Key format: "C{id}" for Constant, "P{id}" for Composition
    private readonly Dictionary<string, string> _reconstructionCache = new();

    public TextExportService(HartDbContext context, ILogger<TextExportService>? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;
    }

    /// <summary>
    /// Export text from a composition using optimized BFS traversal.
    /// Returns the reconstructed text and export statistics.
    /// </summary>
    public async Task<TextExportResult> ExportTextAsync(
        long rootCompositionId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stats = new ExportStats();

        // Clear cache for fresh export (or keep for repeated exports of similar content)
        _reconstructionCache.Clear();

        // Load root composition
        var rootComposition = await _context.Compositions.FindAsync(new object[] { rootCompositionId }, cancellationToken);
        if (rootComposition == null)
            throw new ArgumentException($"Composition {rootCompositionId} not found");

        stats.TotalNodes++;

        // BFS reconstruction with batch loading
        var text = await ReconstructBFSAsync(rootComposition, stats, cancellationToken);

        stopwatch.Stop();
        stats.ExportTimeMs = stopwatch.ElapsedMilliseconds;

        _logger?.LogInformation(
            "Export complete: {Chars} chars, {Nodes} nodes, {CacheHits} cache hits, {Queries} queries, {Time}ms",
            text.Length, stats.TotalNodes, stats.CacheHits, stats.DbQueries, stats.ExportTimeMs);

        return new TextExportResult
        {
            Text = text,
            Stats = stats
        };
    }

    /// <summary>
    /// BFS reconstruction with batch loading.
    /// 
    /// Strategy:
    /// 1. Start with root's relations
    /// 2. Batch-load all referenced constants and compositions
    /// 3. For each: if constant → emit char; if composition → queue children
    /// 4. Use cache to skip already-reconstructed patterns
    /// </summary>
    private async Task<string> ReconstructBFSAsync(
        Composition rootComposition,
        ExportStats stats,
        CancellationToken cancellationToken)
    {
        // Check if composition has any relations
        var hasRelations = await _context.Relations.AnyAsync(r => r.CompositionId == rootComposition.Id, cancellationToken);
        if (!hasRelations)
            return "";

        // We need to maintain order, so we'll use a different approach:
        // Recursively reconstruct but with BATCH loading at each level
        var result = await ReconstructCompositionAsync(rootComposition, stats, cancellationToken);
        return result;
    }

    /// <summary>
    /// Reconstruct a composition with batch loading of children.
    /// Uses memoization cache for patterns.
    /// </summary>
    private async Task<string> ReconstructCompositionAsync(
        Composition composition,
        ExportStats stats,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"P{composition.Id}";
        
        // Check cache first
        if (_reconstructionCache.TryGetValue(cacheKey, out var cached))
        {
            stats.CacheHits++;
            return cached;
        }

        // Get relations ordered by position
        var relations = await _context.Relations
            .Where(r => r.CompositionId == composition.Id)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);

        stats.DbQueries++;

        if (relations.Count == 0)
        {
            _reconstructionCache[cacheKey] = "";
            return "";
        }

        // Separate constant and composition children
        var constantIds = relations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();
        
        var compositionIds = relations
            .Where(r => r.ChildCompositionId.HasValue)
            .Select(r => r.ChildCompositionId!.Value)
            .Distinct()
            .ToList();

        // Batch load constants that aren't cached
        var uncachedConstantIds = constantIds.Where(id => !_reconstructionCache.ContainsKey($"C{id}")).ToList();
        if (uncachedConstantIds.Count > 0)
        {
            stats.DbQueries++;
            var constants = await _context.Constants
                .Where(c => uncachedConstantIds.Contains(c.Id))
                .ToListAsync(cancellationToken);

            stats.TotalNodes += constants.Count;

            foreach (var constant in constants)
            {
                _reconstructionCache[$"C{constant.Id}"] = ReconstructConstant(constant);
            }
        }

        // Batch load compositions that aren't cached
        var uncachedCompositionIds = compositionIds.Where(id => !_reconstructionCache.ContainsKey($"P{id}")).ToList();
        if (uncachedCompositionIds.Count > 0)
        {
            stats.DbQueries++;
            var childCompositions = await _context.Compositions
                .Where(c => uncachedCompositionIds.Contains(c.Id))
                .ToListAsync(cancellationToken);

            stats.TotalNodes += childCompositions.Count;

            // Recursively reconstruct each uncached child composition
            foreach (var childComp in childCompositions)
            {
                if (!_reconstructionCache.ContainsKey($"P{childComp.Id}"))
                {
                    var childText = await ReconstructCompositionAsync(childComp, stats, cancellationToken);
                    _reconstructionCache[$"P{childComp.Id}"] = childText;
                }
            }
        }

        // Now build the result from cached values
        var sb = new StringBuilder();
        foreach (var relation in relations)
        {
            string? childText = null;
            
            if (relation.ChildConstantId.HasValue)
            {
                _reconstructionCache.TryGetValue($"C{relation.ChildConstantId.Value}", out childText);
            }
            else if (relation.ChildCompositionId.HasValue)
            {
                _reconstructionCache.TryGetValue($"P{relation.ChildCompositionId.Value}", out childText);
            }

            if (childText != null)
            {
                for (int m = 0; m < relation.Multiplicity; m++)
                {
                    sb.Append(childText);
                }
            }
        }

        var result = sb.ToString();
        _reconstructionCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Reconstruct a constant to its character.
    /// SeedValue → Unicode codepoint → string
    /// No geometry needed - it's deterministic from the seed.
    /// </summary>
    private static string ReconstructConstant(Constant constant)
    {
        if (constant.SeedType == 0) // Unicode
        {
            return char.ConvertFromUtf32((int)constant.SeedValue);
        }
        return "";
    }

    /// <summary>
    /// Export text as a stream for very large texts.
    /// Yields chunks as they're reconstructed.
    /// </summary>
    public async IAsyncEnumerable<string> ExportTextStreamAsync(
        long rootCompositionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rootComposition = await _context.Compositions.FindAsync(new object[] { rootCompositionId }, cancellationToken);
        if (rootComposition == null)
            throw new ArgumentException($"Composition {rootCompositionId} not found");

        // Get relations ordered by position
        var relations = await _context.Relations
            .Where(r => r.CompositionId == rootCompositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);

        if (relations.Count == 0)
            yield break;

        // Batch load all child constants
        var constantIds = relations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();
        
        var constants = await _context.Constants
            .Where(c => constantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Batch load all child compositions
        var compositionIds = relations
            .Where(r => r.ChildCompositionId.HasValue)
            .Select(r => r.ChildCompositionId!.Value)
            .Distinct()
            .ToList();
        
        var childCompositions = await _context.Compositions
            .Where(c => compositionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var stats = new ExportStats();
        
        foreach (var relation in relations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? childText = null;

            if (relation.ChildConstantId.HasValue && constants.TryGetValue(relation.ChildConstantId.Value, out var constant))
            {
                var cacheKey = $"C{constant.Id}";
                if (_reconstructionCache.TryGetValue(cacheKey, out var cached))
                {
                    childText = cached;
                }
                else
                {
                    childText = ReconstructConstant(constant);
                    _reconstructionCache[cacheKey] = childText;
                }
            }
            else if (relation.ChildCompositionId.HasValue && childCompositions.TryGetValue(relation.ChildCompositionId.Value, out var childComp))
            {
                var cacheKey = $"P{childComp.Id}";
                if (_reconstructionCache.TryGetValue(cacheKey, out var cached))
                {
                    childText = cached;
                }
                else
                {
                    childText = await ReconstructCompositionAsync(childComp, stats, cancellationToken);
                    _reconstructionCache[cacheKey] = childText;
                }
            }

            if (childText != null)
            {
                for (int m = 0; m < relation.Multiplicity; m++)
                {
                    yield return childText;
                }
            }
        }
    }

    /// <summary>
    /// Export text directly to a file with streaming.
    /// Most efficient for very large texts.
    /// </summary>
    public async Task<ExportStats> ExportToFileAsync(
        long rootAtomId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stats = new ExportStats();

        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

        await foreach (var chunk in ExportTextStreamAsync(rootAtomId, cancellationToken))
        {
            await writer.WriteAsync(chunk);
            stats.ChunksWritten++;
        }

        stopwatch.Stop();
        stats.ExportTimeMs = stopwatch.ElapsedMilliseconds;

        return stats;
    }

    /// <summary>
    /// Verify export is bit-perfect compared to original.
    /// </summary>
    public async Task<VerificationResult> VerifyExportAsync(
        long rootAtomId,
        string originalText,
        CancellationToken cancellationToken = default)
    {
        var exportResult = await ExportTextAsync(rootAtomId, cancellationToken);
        
        var isMatch = exportResult.Text == originalText;
        
        int? firstDifferenceIndex = null;
        if (!isMatch)
        {
            for (int i = 0; i < Math.Min(exportResult.Text.Length, originalText.Length); i++)
            {
                if (exportResult.Text[i] != originalText[i])
                {
                    firstDifferenceIndex = i;
                    break;
                }
            }
            firstDifferenceIndex ??= Math.Min(exportResult.Text.Length, originalText.Length);
        }

        return new VerificationResult
        {
            IsBitPerfect = isMatch,
            OriginalLength = originalText.Length,
            ExportedLength = exportResult.Text.Length,
            FirstDifferenceIndex = firstDifferenceIndex,
            Stats = exportResult.Stats
        };
    }
}

/// <summary>
/// Result of text export operation
/// </summary>
public record TextExportResult
{
    public required string Text { get; init; }
    public required ExportStats Stats { get; init; }
}

/// <summary>
/// Statistics about export operation
/// </summary>
public record ExportStats
{
    public int TotalNodes { get; set; }
    public int CacheHits { get; set; }
    public int DbQueries { get; set; }
    public int ChunksWritten { get; set; }
    public long ExportTimeMs { get; set; }
    
    public double CacheHitRate => TotalNodes > 0 ? (double)CacheHits / TotalNodes : 0;
}

/// <summary>
/// Result of verification comparing export to original
/// </summary>
public record VerificationResult
{
    public bool IsBitPerfect { get; init; }
    public int OriginalLength { get; init; }
    public int ExportedLength { get; init; }
    public int? FirstDifferenceIndex { get; init; }
    public required ExportStats Stats { get; init; }
}
