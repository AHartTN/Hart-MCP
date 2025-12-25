using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Npgsql;
using System.Text;

namespace Hart.MCP.Core.Services;

/// <summary>
/// Hierarchical text ingestion service implementing Sequitur-like grammar induction.
/// 
/// KEY INNOVATION: Content-addressed hierarchical n-gram composition with cascading deduplication.
/// 
/// PERFORMANCE: Optimized for large texts (Moby Dick in &lt;15s, Unicode seeding &lt;2s)
/// - In-memory grammar induction (no DB during pattern discovery)
/// - Linked list for O(1) symbol replacement
/// - Batch persistence at end
/// - Process most-frequent digram per iteration (true Sequitur)
/// 
/// Example: "The cat in the hat"
/// - Level 0 (characters): T, h, e, (space), c, a, t, ...
/// - Level 1+ (digrams/patterns): "the" detected twice → stored ONCE, referenced twice
/// - Final: Composition of references, where shared substrings point to same atoms
/// </summary>
public class HierarchicalTextIngestionService
{
    private readonly HartDbContext _context;
    private readonly AtomIngestionService _atomService;
    private readonly GeometryFactory _geometryFactory;
    private readonly ILogger<HierarchicalTextIngestionService>? _logger;

    // Configuration
    private readonly int _maxTiers;
    private readonly int _minPatternOccurrences;

    /// <summary>
    /// In-memory grammar rule - NOT persisted until final batch write
    /// </summary>
    private class InMemoryRule
    {
        public int RuleId { get; set; } // Negative IDs for in-memory rules
        public int Left { get; set; }   // Can be char codepoint (positive) or rule ID (negative)
        public int Right { get; set; }
        public int UseCount { get; set; }
        public int Tier { get; set; }
    }

    /// <summary>
    /// Symbol in the sequence - either a character (positive) or rule reference (negative)
    /// </summary>
    private class Symbol
    {
        public int Value { get; set; }
        public Symbol? Prev { get; set; }
        public Symbol? Next { get; set; }
    }

    /// <summary>
    /// Digram for efficient hashing
    /// </summary>
    private readonly struct Digram : IEquatable<Digram>
    {
        public readonly int First;
        public readonly int Second;

        public Digram(int first, int second)
        {
            First = first;
            Second = second;
        }

        public bool Equals(Digram other) => First == other.First && Second == other.Second;
        public override bool Equals(object? obj) => obj is Digram other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(First, Second);
    }

    public HierarchicalTextIngestionService(
        HartDbContext context,
        AtomIngestionService atomService,
        ILogger<HierarchicalTextIngestionService>? logger = null,
        int maxTiers = 30,
        int minPatternOccurrences = 2)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _atomService = atomService ?? throw new ArgumentNullException(nameof(atomService));
        _geometryFactory = new GeometryFactory(new PrecisionModel(), 0);
        _logger = logger;
        _maxTiers = maxTiers;
        _minPatternOccurrences = minPatternOccurrences;
    }

    /// <summary>
    /// Ingest text hierarchically with automatic pattern discovery and deduplication.
    /// Returns the root composition atom ID.
    /// </summary>
    public async Task<HierarchicalIngestionResult> IngestTextHierarchicallyAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));

        _logger?.LogInformation("Hierarchical ingestion of text length {Length}", text.Length);

        var result = new HierarchicalIngestionResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // ============================================
        // PHASE 1: Extract codepoints (in-memory)
        // ============================================
        var codepoints = ExtractCodepoints(text);
        var uniqueCodepoints = new HashSet<int>(codepoints);

        result.Tier0CharacterCount = codepoints.Count;
        result.UniqueCharacterCount = uniqueCodepoints.Count;

        _logger?.LogDebug("Extracted {UniqueCount} unique codepoints from {Total} characters",
            uniqueCodepoints.Count, codepoints.Count);

        // ============================================
        // PHASE 2: In-memory Sequitur grammar induction (NO DB CALLS)
        // ============================================
        var (finalSequence, rules) = ApplySequiturInMemory(codepoints, cancellationToken);

        result.TotalPatternsDiscovered = rules.Count;
        result.TierCount = rules.Count > 0 ? rules.Max(r => r.Tier) + 1 : 1;
        result.FinalSequenceLength = finalSequence.Count;

        _logger?.LogDebug("Grammar induction complete: {Patterns} rules, {FinalLen} symbols",
            rules.Count, finalSequence.Count);

        // ============================================
        // PHASE 3: Batch persist to database
        // ============================================
        var (rootAtomId, rootIsConstant) = await BatchPersistGrammarAsync(
            codepoints, uniqueCodepoints, finalSequence, rules, cancellationToken);

        result.RootAtomId = rootAtomId;
        result.RootIsConstant = rootIsConstant;

        stopwatch.Stop();
        result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        _logger?.LogInformation(
            "Hierarchical ingestion complete: {Patterns} patterns, {Tiers} tiers, root atom {RootId}, {Time}ms",
            result.TotalPatternsDiscovered, result.TierCount, result.RootAtomId, result.ProcessingTimeMs);

        return result;
    }

    /// <summary>
    /// Pure in-memory Sequitur-style grammar induction.
    /// Uses linked list for O(1) symbol replacement.
    /// Processes ONE most-frequent digram per iteration.
    /// </summary>
    private (List<int> FinalSequence, List<InMemoryRule> Rules) ApplySequiturInMemory(
        List<int> initialSequence,
        CancellationToken cancellationToken)
    {
        if (initialSequence.Count <= 1)
            return (initialSequence, new List<InMemoryRule>());

        // Build linked list for O(1) deletions
        var head = new Symbol { Value = initialSequence[0] };
        var current = head;
        for (int i = 1; i < initialSequence.Count; i++)
        {
            var next = new Symbol { Value = initialSequence[i], Prev = current };
            current.Next = next;
            current = next;
        }

        var rules = new List<InMemoryRule>();
        int nextRuleId = -1; // Negative IDs for rules
        int tier = 1;
        int sequenceLength = initialSequence.Count;

        while (tier <= _maxTiers && sequenceLength > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Count digrams in single pass
            var digramCounts = new Dictionary<Digram, int>();
            var sym = head;
            while (sym?.Next != null)
            {
                var digram = new Digram(sym.Value, sym.Next.Value);
                digramCounts.TryGetValue(digram, out int count);
                digramCounts[digram] = count + 1;
                sym = sym.Next;
            }

            // Find most frequent digram
            Digram? bestDigram = null;
            int bestCount = 0;
            foreach (var (digram, count) in digramCounts)
            {
                if (count >= _minPatternOccurrences && count > bestCount)
                {
                    bestCount = count;
                    bestDigram = digram;
                }
            }

            if (bestDigram == null)
                break;

            var targetDigram = bestDigram.Value;

            // Create rule for this digram
            var rule = new InMemoryRule
            {
                RuleId = nextRuleId--,
                Left = targetDigram.First,
                Right = targetDigram.Second,
                UseCount = bestCount,
                Tier = tier
            };
            rules.Add(rule);

            // Replace all non-overlapping occurrences
            sym = head;
            int replacements = 0;
            while (sym?.Next != null)
            {
                if (sym.Value == targetDigram.First && sym.Next.Value == targetDigram.Second)
                {
                    // Replace digram with rule reference
                    sym.Value = rule.RuleId;
                    
                    // Remove next symbol
                    var toRemove = sym.Next;
                    sym.Next = toRemove.Next;
                    if (toRemove.Next != null)
                        toRemove.Next.Prev = sym;
                    
                    sequenceLength--;
                    replacements++;
                    
                    // Move to next (skip to avoid overlapping replacement)
                    sym = sym.Next;
                }
                else
                {
                    sym = sym.Next;
                }
            }

            _logger?.LogTrace("Tier {Tier}: Replaced digram ({First}, {Second}) with rule {RuleId}, {Count} times",
                tier, targetDigram.First, targetDigram.Second, rule.RuleId, replacements);

            if (replacements == 0)
                break;

            tier++;
        }

        // Convert linked list back to list
        var finalSequence = new List<int>(sequenceLength);
        var s = head;
        while (s != null)
        {
            finalSequence.Add(s.Value);
            s = s.Next;
        }

        return (finalSequence, rules);
    }

    /// <summary>
    /// Batch persist the grammar to database in minimal round-trips.
    /// Returns (rootId, isConstant) tuple.
    /// </summary>
    private async Task<(long RootId, bool IsConstant)> BatchPersistGrammarAsync(
        List<int> originalCodepoints,
        HashSet<int> uniqueCodepoints,
        List<int> finalSequence,
        List<InMemoryRule> rules,
        CancellationToken cancellationToken)
    {
        // ============================================
        // STEP 1: Bulk get/create character constants (SINGLE BATCH)
        // ============================================
        var charAtomLookup = await BulkGetOrCreateCharConstantsAsync(
            uniqueCodepoints.Select(cp => (uint)cp).ToArray(), cancellationToken);

        // Cache all constant geometries for later use (single query)
        var allConstantIds = charAtomLookup.Values.ToList();
        var constantGeomCache = await _context.Constants
            .Where(c => allConstantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Geom, cancellationToken);

        // ============================================
        // STEP 2: Create compositions for rules IN BATCH
        // ============================================
        var ruleAtomLookup = new Dictionary<int, long>();
        var sortedRules = rules.OrderBy(r => r.Tier).ToList();

        if (sortedRules.Count > 0)
        {
            // Process rules in batches by tier (all rules in a tier can be created together)
            var currentTier = sortedRules[0].Tier;
            var tierBatch = new List<InMemoryRule>();
            var compositionGeomCache = new Dictionary<long, Geometry>();

            foreach (var rule in sortedRules)
            {
                if (rule.Tier != currentTier)
                {
                    // Flush previous tier batch
                    await CreateRuleBatchAsync(tierBatch, charAtomLookup, ruleAtomLookup, 
                        constantGeomCache, compositionGeomCache, cancellationToken);
                    tierBatch.Clear();
                    currentTier = rule.Tier;
                }
                tierBatch.Add(rule);
            }
            // Flush final batch
            if (tierBatch.Count > 0)
            {
                await CreateRuleBatchAsync(tierBatch, charAtomLookup, ruleAtomLookup,
                    constantGeomCache, compositionGeomCache, cancellationToken);
            }
        }

        // ============================================
        // STEP 3: Create root composition from final sequence (SINGLE BATCH)
        // ============================================
        var (rootRefs, rootMults, rootIsConstantFlags) = ApplyRLECompression(finalSequence, charAtomLookup, ruleAtomLookup);

        if (rootRefs.Count == 1 && rootMults[0] == 1)
        {
            return (rootRefs[0], rootIsConstantFlags[0]);
        }

        // Load all needed geometries in one query
        var compositionRefsToLoad = new List<long>();
        for (int i = 0; i < rootRefs.Count; i++)
        {
            if (!rootIsConstantFlags[i])
                compositionRefsToLoad.Add(rootRefs[i]);
        }

        var compositionGeomsForRoot = compositionRefsToLoad.Count > 0
            ? await _context.Compositions
                .Where(c => compositionRefsToLoad.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Geom, cancellationToken)
            : new Dictionary<long, Geometry?>();

        // Compute root hash and geometry
        var childIsConstant = new Dictionary<long, bool>();
        for (int i = 0; i < rootRefs.Count; i++)
            childIsConstant[rootRefs[i]] = rootIsConstantFlags[i];

        var contentHash = ComputeTypedCompositionHash(rootRefs.ToArray(), rootMults.ToArray(), childIsConstant);

        // Check dedup
        var existingRoot = await _context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingRoot != 0)
            return (existingRoot, false);

        // Build geometry from cached data
        var coordinates = new List<CoordinateZM>();
        for (int i = 0; i < rootRefs.Count; i++)
        {
            var refId = rootRefs[i];
            Geometry? geom = rootIsConstantFlags[i] 
                ? constantGeomCache.GetValueOrDefault(refId)
                : compositionGeomsForRoot.GetValueOrDefault(refId);
            if (geom != null)
                coordinates.Add(ExtractRepresentativeCoordinate(geom));
        }

        Geometry rootGeom = coordinates.Count == 1
            ? _geometryFactory.CreatePoint(coordinates[0])
            : _geometryFactory.CreateLineString(coordinates.ToArray());

        var centroid = rootGeom.Centroid.Coordinate;
        var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
        {
            X = centroid.X, Y = centroid.Y,
            Z = double.IsNaN(centroid.Z) ? 0 : centroid.Z,
            M = double.IsNaN(centroid.M) ? 0 : centroid.M
        });

        var rootComposition = new Composition
        {
            HilbertHigh = (ulong)hilbert.High,
            HilbertLow = (ulong)hilbert.Low,
            Geom = rootGeom,
            ContentHash = contentHash
        };

        _context.Compositions.Add(rootComposition);
        await _context.SaveChangesAsync(cancellationToken);

        // Create relations using PostgreSQL COPY for massive speedup
        await BulkCopyRelationsAsync(
            rootComposition.Id, rootRefs, rootMults, rootIsConstantFlags, cancellationToken);

        return (rootComposition.Id, false);
    }

    /// <summary>
    /// Bulk insert relations using PostgreSQL COPY command.
    /// Orders of magnitude faster than EF Core for large batches.
    /// </summary>
    private async Task BulkCopyRelationsAsync(
        long compositionId,
        List<long> refs,
        List<int> multiplicities,
        List<bool> isConstant,
        CancellationToken cancellationToken)
    {
        var conn = _context.Database.GetDbConnection() as NpgsqlConnection;
        if (conn == null)
            throw new InvalidOperationException("Database connection is not Npgsql");

        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await conn.OpenAsync(cancellationToken);

        try
        {
            using var writer = await conn.BeginBinaryImportAsync(
                "COPY relation (composition_id, position, multiplicity, child_constant_id, child_composition_id) FROM STDIN (FORMAT BINARY)",
                cancellationToken);

            for (int i = 0; i < refs.Count; i++)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(compositionId, NpgsqlTypes.NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(i, NpgsqlTypes.NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(multiplicities[i], NpgsqlTypes.NpgsqlDbType.Integer, cancellationToken);

                if (isConstant[i])
                {
                    await writer.WriteAsync(refs[i], NpgsqlTypes.NpgsqlDbType.Bigint, cancellationToken);
                    await writer.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken);
                    await writer.WriteAsync(refs[i], NpgsqlTypes.NpgsqlDbType.Bigint, cancellationToken);
                }
            }

            await writer.CompleteAsync(cancellationToken);
        }
        finally
        {
            if (!wasOpen)
                await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Create a batch of rule compositions in minimal DB calls.
    /// </summary>
    private async Task CreateRuleBatchAsync(
        List<InMemoryRule> rules,
        Dictionary<uint, long> charAtomLookup,
        Dictionary<int, long> ruleAtomLookup,
        Dictionary<long, Geometry?> constantGeomCache,
        Dictionary<long, Geometry> compositionGeomCache,
        CancellationToken cancellationToken)
    {
        if (rules.Count == 0) return;

        // Prepare all rule data
        var ruleData = new List<(InMemoryRule rule, long leftId, long rightId, bool leftIsConst, bool rightIsConst, byte[] hash)>();
        
        foreach (var rule in rules)
        {
            long leftId = ResolveSymbolToAtomId(rule.Left, charAtomLookup, ruleAtomLookup);
            long rightId = ResolveSymbolToAtomId(rule.Right, charAtomLookup, ruleAtomLookup);
            bool leftIsConst = rule.Left >= 0;
            bool rightIsConst = rule.Right >= 0;
            
            var childIsConstant = new Dictionary<long, bool> { [leftId] = leftIsConst, [rightId] = rightIsConst };
            var hash = ComputeTypedCompositionHash(new[] { leftId, rightId }, new[] { 1, 1 }, childIsConstant);
            
            ruleData.Add((rule, leftId, rightId, leftIsConst, rightIsConst, hash));
        }

        // Batch lookup existing compositions by hash
        var allHashes = ruleData.Select(r => r.hash).ToList();
        var existingByHash = await _context.Compositions
            .Where(c => allHashes.Any(h => c.ContentHash == h))
            .Select(c => new { c.Id, c.ContentHash, c.Geom })
            .ToListAsync(cancellationToken);

        var hashToExisting = existingByHash.ToDictionary(
            c => Convert.ToHexString(c.ContentHash),
            c => (c.Id, c.Geom));

        // Separate into existing vs new
        var newRules = new List<(InMemoryRule rule, long leftId, long rightId, bool leftIsConst, bool rightIsConst, byte[] hash)>();
        foreach (var rd in ruleData)
        {
            var hashHex = Convert.ToHexString(rd.hash);
            if (hashToExisting.TryGetValue(hashHex, out var existing))
            {
                ruleAtomLookup[rd.rule.RuleId] = existing.Id;
                if (existing.Geom != null)
                    compositionGeomCache[existing.Id] = existing.Geom;
            }
            else
            {
                newRules.Add(rd);
            }
        }

        if (newRules.Count == 0) return;

        // Create all new compositions in batch
        var newCompositions = new List<(Composition comp, InMemoryRule rule, long leftId, long rightId, bool leftIsConst, bool rightIsConst)>();
        foreach (var rd in newRules)
        {
            // Get geometries for children
            Geometry? leftGeom = rd.leftIsConst 
                ? constantGeomCache.GetValueOrDefault(rd.leftId)
                : compositionGeomCache.GetValueOrDefault(rd.leftId);
            Geometry? rightGeom = rd.rightIsConst
                ? constantGeomCache.GetValueOrDefault(rd.rightId)
                : compositionGeomCache.GetValueOrDefault(rd.rightId);

            var coords = new List<CoordinateZM>();
            if (leftGeom != null) coords.Add(ExtractRepresentativeCoordinate(leftGeom));
            if (rightGeom != null) coords.Add(ExtractRepresentativeCoordinate(rightGeom));

            Geometry geom = coords.Count switch
            {
                0 => _geometryFactory.CreatePoint(new CoordinateZM(0, 0, 0, 0)),
                1 => _geometryFactory.CreatePoint(coords[0]),
                _ => _geometryFactory.CreateLineString(coords.ToArray())
            };

            var centroid = geom.Centroid.Coordinate;
            var hilbert = NativeLibrary.point_to_hilbert(new NativeLibrary.PointZM
            {
                X = centroid.X, Y = centroid.Y,
                Z = double.IsNaN(centroid.Z) ? 0 : centroid.Z,
                M = double.IsNaN(centroid.M) ? 0 : centroid.M
            });

            var comp = new Composition
            {
                HilbertHigh = (ulong)hilbert.High,
                HilbertLow = (ulong)hilbert.Low,
                Geom = geom,
                ContentHash = rd.hash
            };

            newCompositions.Add((comp, rd.rule, rd.leftId, rd.rightId, rd.leftIsConst, rd.rightIsConst));
        }

        // Batch insert compositions
        _context.Compositions.AddRange(newCompositions.Select(x => x.comp));
        await _context.SaveChangesAsync(cancellationToken);

        // Update lookups and caches
        foreach (var (comp, rule, _, _, _, _) in newCompositions)
        {
            ruleAtomLookup[rule.RuleId] = comp.Id;
            compositionGeomCache[comp.Id] = comp.Geom!;
        }

        // Batch create relations
        var allRelations = new List<Relation>();
        foreach (var (comp, _, leftId, rightId, leftIsConst, rightIsConst) in newCompositions)
        {
            allRelations.Add(new Relation
            {
                CompositionId = comp.Id,
                Position = 0,
                Multiplicity = 1,
                ChildConstantId = leftIsConst ? leftId : null,
                ChildCompositionId = leftIsConst ? null : leftId
            });
            allRelations.Add(new Relation
            {
                CompositionId = comp.Id,
                Position = 1,
                Multiplicity = 1,
                ChildConstantId = rightIsConst ? rightId : null,
                ChildCompositionId = rightIsConst ? null : rightId
            });
        }
        _context.Relations.AddRange(allRelations);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolve a symbol (char codepoint or rule ID) to an atom ID
    /// </summary>
    private long ResolveSymbolToAtomId(int symbol, Dictionary<uint, long> charLookup, Dictionary<int, long> ruleLookup)
    {
        if (symbol >= 0)
        {
            // Positive = character codepoint
            return charLookup[(uint)symbol];
        }
        else
        {
            // Negative = rule ID
            return ruleLookup[symbol];
        }
    }

    /// <summary>
    /// Apply RLE compression and resolve symbols to atom IDs.
    /// Also tracks whether each ref is a constant (true) or composition (false).
    /// </summary>
    private (List<long> Refs, List<int> Multiplicities, List<bool> IsConstant) ApplyRLECompression(
        List<int> sequence,
        Dictionary<uint, long> charLookup,
        Dictionary<int, long> ruleLookup)
    {
        var refs = new List<long>();
        var mults = new List<int>();
        var isConstant = new List<bool>();

        int i = 0;
        while (i < sequence.Count)
        {
            int current = sequence[i];
            int count = 1;

            while (i + count < sequence.Count && sequence[i + count] == current)
                count++;

            refs.Add(ResolveSymbolToAtomId(current, charLookup, ruleLookup));
            mults.Add(count);
            isConstant.Add(current >= 0); // Positive = char codepoint = constant
            i += count;
        }

        return (refs, mults, isConstant);
    }

    /// <summary>
    /// Create or get existing composition atom (content-addressed deduplication).
    /// The isConstant array specifies whether each ref is a constant (true) or composition (false).
    /// </summary>
    private async Task<long> CreateOrGetCompositionAsync(
        long[] refs,
        int[] multiplicities,
        bool[] isConstant,
        string compositionType,
        CancellationToken cancellationToken)
    {
        // Build child type lookup from the provided isConstant array
        var childIsConstant = new Dictionary<long, bool>();
        for (int i = 0; i < refs.Length; i++)
        {
            childIsConstant[refs[i]] = isConstant[i];
        }

        // Compute typed hash that distinguishes constant vs composition children
        var contentHash = ComputeTypedCompositionHash(refs, multiplicities, childIsConstant);

        // Check if already exists (deduplication across ALL ingested content)
        var existing = await _context.Compositions
            .Where(c => c.ContentHash == contentHash)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != 0)
        {
            _logger?.LogTrace("Deduplication hit: composition with hash {Hash} already exists as {Id}",
                Convert.ToHexString(contentHash), existing);
            return existing;
        }

        // Collect IDs to fetch from each table
        var constantIds = refs.Where((r, i) => isConstant[i]).Distinct().ToList();
        var compositionIds = refs.Where((r, i) => !isConstant[i]).Distinct().ToList();

        // Fetch the actual entities for geometry extraction
        var constantChildren = constantIds.Count > 0
            ? await _context.Constants
                .Where(c => constantIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, cancellationToken)
            : new Dictionary<long, Constant>();

        var compositionChildren = compositionIds.Count > 0
            ? await _context.Compositions
                .Where(c => compositionIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, cancellationToken)
            : new Dictionary<long, Composition>();

        // Build geometry from child points
        var coordinates = new List<CoordinateZM>();
        for (int i = 0; i < refs.Length; i++)
        {
            var refId = refs[i];
            if (isConstant[i])
            {
                if (!constantChildren.TryGetValue(refId, out var constant))
                    throw new InvalidOperationException($"Constant {refId} not found");
                coordinates.Add(ExtractRepresentativeCoordinate(constant.Geom));
            }
            else
            {
                if (!compositionChildren.TryGetValue(refId, out var comp))
                    throw new InvalidOperationException($"Composition {refId} not found");
                coordinates.Add(ExtractRepresentativeCoordinate(comp.Geom));
            }
        }

        Geometry geom;
        if (coordinates.Count == 1)
        {
            geom = _geometryFactory.CreatePoint(coordinates[0]);
        }
        else
        {
            geom = _geometryFactory.CreateLineString(coordinates.ToArray());
        }

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
            ContentHash = contentHash
        };

        _context.Compositions.Add(composition);
        await _context.SaveChangesAsync(cancellationToken);

        // Create Relation entries for composition edges
        for (int i = 0; i < refs.Length; i++)
        {
            var relation = new Relation
            {
                CompositionId = composition.Id,
                Position = i,
                Multiplicity = multiplicities[i]
            };
            if (isConstant[i])
                relation.ChildConstantId = refs[i];
            else
                relation.ChildCompositionId = refs[i];
            _context.Relations.Add(relation);
        }
        await _context.SaveChangesAsync(cancellationToken);

        return composition.Id;
    }

    /// <summary>
    /// Bulk get or create character constants
    /// </summary>
    private async Task<Dictionary<uint, long>> BulkGetOrCreateCharConstantsAsync(
        uint[] codepoints,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<uint, long>(codepoints.Length);
        var codepointsAsLong = codepoints.Select(cp => (long)cp).ToList();

        // Single query for existing
        var existing = await _context.Constants
            .Where(c => c.SeedType == 0 && // SEED_TYPE_UNICODE
                       codepointsAsLong.Contains(c.SeedValue))
            .Select(c => new { c.Id, c.SeedValue })
            .ToListAsync(cancellationToken);

        foreach (var constant in existing)
        {
            result[(uint)constant.SeedValue] = constant.Id;
        }

        // Create missing
        var missing = codepoints.Where(cp => !result.ContainsKey(cp)).ToList();
        if (missing.Count > 0)
        {
            var newConstants = new List<Constant>();
            
            foreach (var cp in missing)
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
                    SeedType = 0, // Unicode
                    ContentHash = contentHash
                };

                _context.Constants.Add(constant);
                newConstants.Add(constant);
            }

            await _context.SaveChangesAsync(cancellationToken);

            // EF Core updates the Id property after SaveChangesAsync
            foreach (var constant in newConstants)
            {
                result[(uint)constant.SeedValue] = constant.Id;
            }
        }

        return result;
    }

    /// <summary>
    /// Compute typed composition hash that includes child types to prevent collisions.
    /// </summary>
    private static byte[] ComputeTypedCompositionHash(
        long[] refs,
        int[] multiplicities,
        Dictionary<long, bool> childIsConstant)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        
        // Get base hash from native library
        var baseHash = NativeLibrary.ComputeCompositionHash(refs, multiplicities);
        
        // Add child type indicators to make hash unique based on constant vs composition
        var typeBytes = new byte[(refs.Length + 7) / 8];
        for (int i = 0; i < refs.Length; i++)
        {
            if (childIsConstant[refs[i]])
                typeBytes[i / 8] |= (byte)(1 << (i % 8));
        }
        
        // Combine base hash with type bytes
        var combined = new byte[baseHash.Length + typeBytes.Length];
        baseHash.CopyTo(combined, 0);
        typeBytes.CopyTo(combined, baseHash.Length);
        
        return sha256.ComputeHash(combined);
    }

    /// <summary>
    /// Extract codepoints from string (as int for compatibility with rule IDs which are negative)
    /// </summary>
    private static List<int> ExtractCodepoints(string text)
    {
        var codepoints = new List<int>(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                char low = text[i + 1];
                if (char.IsLowSurrogate(low))
                {
                    codepoints.Add(char.ConvertToUtf32(c, low));
                    i++;
                    continue;
                }
            }

            codepoints.Add(c);
        }

        return codepoints;
    }

    /// <summary>
    /// Extract representative coordinate from geometry
    /// </summary>
    private static CoordinateZM ExtractRepresentativeCoordinate(Geometry geom)
    {
        var coord = geom.Centroid.Coordinate;
        return new CoordinateZM(
            coord.X,
            coord.Y,
            double.IsNaN(coord.Z) ? 0 : coord.Z,
            double.IsNaN(coord.M) ? 0 : coord.M
        );
    }

    /// <summary>
    /// Query for all compositions containing a specific substring pattern.
    /// Finds compositions at any tier that include the pattern.
    /// </summary>
    public async Task<List<long>> FindCompositionsContainingPatternAsync(
        string pattern,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pattern))
            return new List<long>();

        // First, ingest the pattern to get its composition representation
        var patternResult = await IngestTextHierarchicallyAsync(pattern, cancellationToken);
        var patternCompositionId = patternResult.RootAtomId;

        // Now find all compositions that reference this pattern via Relations table
        var containingCompositions = await _context.Relations
            .Where(r => r.ChildCompositionId == patternCompositionId)
            .Select(r => r.CompositionId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return containingCompositions;
    }

    /// <summary>
    /// Reconstruct the original text from a node (constant or composition).
    /// Recursively expands all references.
    /// </summary>
    public async Task<string> ReconstructTextAsync(
        long nodeId,
        CancellationToken cancellationToken = default)
    {
        // When called without type hint, try composition first (most common case),
        // then fall back to constant
        return await ReconstructTextAsync(nodeId, isConstant: null, cancellationToken);
    }

    /// <summary>
    /// Reconstruct the original text from a node (constant or composition).
    /// Recursively expands all references.
    /// </summary>
    /// <param name="nodeId">The ID of the node to reconstruct</param>
    /// <param name="isConstant">If known, whether the node is a constant (true) or composition (false). Null to auto-detect.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<string> ReconstructTextAsync(
        long nodeId,
        bool? isConstant,
        CancellationToken cancellationToken = default)
    {
        // If we know it's a constant, check that first
        if (isConstant == true)
        {
            var constant = await _context.Constants.FindAsync(new object[] { nodeId }, cancellationToken);
            if (constant != null)
            {
                if (constant.SeedType == 0) // Unicode
                {
                    return char.ConvertFromUtf32((int)constant.SeedValue);
                }
                return "";
            }
            throw new ArgumentException($"Node {nodeId} not found in Constants (isConstant was true)");
        }

        // If we know it's a composition (or auto-detecting), check composition first
        var composition = await _context.Compositions.FindAsync(new object[] { nodeId }, cancellationToken);
        if (composition != null)
        {
            // Composition: get relations ordered by position
            var relations = await _context.Relations
                .Where(r => r.CompositionId == nodeId)
                .OrderBy(r => r.Position)
                .ToListAsync(cancellationToken);

            if (relations.Count == 0)
                return "";

            var sb = new StringBuilder();
            foreach (var relation in relations)
            {
                string childText;
                if (relation.ChildConstantId.HasValue)
                {
                    // We know it's a constant via the relation
                    childText = await ReconstructTextAsync(relation.ChildConstantId.Value, isConstant: true, cancellationToken);
                }
                else if (relation.ChildCompositionId.HasValue)
                {
                    // We know it's a composition via the relation
                    childText = await ReconstructTextAsync(relation.ChildCompositionId.Value, isConstant: false, cancellationToken);
                }
                else
                {
                    continue;
                }

                for (int m = 0; m < relation.Multiplicity; m++)
                {
                    sb.Append(childText);
                }
            }

            return sb.ToString();
        }

        // If isConstant was null (auto-detect) and not found as composition, try constant
        if (isConstant == null)
        {
            var constant = await _context.Constants.FindAsync(new object[] { nodeId }, cancellationToken);
            if (constant != null)
            {
                if (constant.SeedType == 0) // Unicode
                {
                    return char.ConvertFromUtf32((int)constant.SeedValue);
                }
                return "";
            }
        }

        throw new ArgumentException($"Node {nodeId} not found in Constants or Compositions");
    }

    /// <summary>
    /// Get statistics about pattern usage across the repository.
    /// Shows which patterns are most frequently reused.
    /// </summary>
    public async Task<List<PatternUsageStats>> GetPatternUsageStatsAsync(
        int topN = 100,
        CancellationToken cancellationToken = default)
    {
        // Count how many compositions reference each child composition using Relations table
        var referenceCount = await _context.Relations
            .Where(r => r.ChildCompositionId.HasValue)
            .GroupBy(r => r.ChildCompositionId!.Value)
            .Select(g => new { CompositionId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(topN * 2)
            .ToListAsync(cancellationToken);

        // Get top N most referenced patterns (compositions only)
        var topPatternIds = referenceCount.Select(r => r.CompositionId).ToList();
        var patternCompositions = await _context.Compositions
            .Where(c => topPatternIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var refCountDict = referenceCount.ToDictionary(r => r.CompositionId, r => r.Count);

        var results = new List<PatternUsageStats>();
        foreach (var patternId in topPatternIds.Where(id => patternCompositions.ContainsKey(id)).Take(topN))
        {
            var composition = patternCompositions[patternId];
            var reconstructed = await ReconstructTextAsync(patternId, cancellationToken);
            
            results.Add(new PatternUsageStats
            {
                AtomId = patternId,
                TypeRef = composition.TypeId,
                ReferenceCount = refCountDict[patternId],
                ReconstructedText = reconstructed.Length > 100
                    ? reconstructed.Substring(0, 100) + "..."
                    : reconstructed
            });
        }

        return results;
    }
}

/// <summary>
/// Result of hierarchical text ingestion
/// </summary>
public class HierarchicalIngestionResult
{
    public long RootAtomId { get; set; }
    
    /// <summary>
    /// True if RootAtomId refers to a Constant, false if it refers to a Composition.
    /// </summary>
    public bool RootIsConstant { get; set; }
    
    public int Tier0CharacterCount { get; set; }
    public int UniqueCharacterCount { get; set; }
    public int TotalPatternsDiscovered { get; set; }
    public int TierCount { get; set; }
    public int FinalSequenceLength { get; set; }
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// Compression ratio: original chars / final sequence length
    /// </summary>
    public double CompressionRatio => 
        FinalSequenceLength > 0 ? (double)Tier0CharacterCount / FinalSequenceLength : 1.0;
}

/// <summary>
/// Statistics about pattern usage in the repository
/// </summary>
public class PatternUsageStats
{
    public long AtomId { get; set; }
    public long? TypeRef { get; set; }
    public int ReferenceCount { get; set; }
    public string ReconstructedText { get; set; } = "";
}
