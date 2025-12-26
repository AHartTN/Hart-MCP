using System.Text.Json;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// JSON ingestion service.
/// 
/// REPRESENTATION:
/// - Strings → text compositions (via existing text ingestion)
/// - Numbers → integer/float constants
/// - Booleans → constant atoms (0 or 1)
/// - Null → special null constant
/// - Arrays → ordered compositions
/// - Objects → compositions of key-value pair compositions
/// 
/// STRUCTURE PRESERVATION:
/// - Full JSON structure preserved as atom tree
/// - Keys and values are separate atoms (enables key reuse)
/// - Array ordering preserved via refs order
/// 
/// LOSSLESS: Original JSON exactly reconstructable.
/// </summary>
public class JsonIngestionService : IngestionServiceBase, IIngestionService<JsonElement>
{
    public JsonIngestionService(HartDbContext context, ILogger<JsonIngestionService>? logger = null)
        : base(context, logger) { }

    public async Task<long> IngestAsync(JsonElement json, CancellationToken ct = default)
    {
        Logger?.LogInformation("Ingesting JSON of kind {Kind}", json.ValueKind);

        // ============================================
        // PHASE 1: Extract ALL unique primitives (memory only)
        // ============================================
        var uniqueChars = new HashSet<uint>();
        var uniqueInts = new HashSet<long>();
        var uniqueFloats = new HashSet<ulong>();
        CollectPrimitives(json, uniqueChars, uniqueInts, uniqueFloats);
        
        Logger?.LogDebug("Found {Chars} unique chars, {Ints} unique ints, {Floats} unique floats",
            uniqueChars.Count, uniqueInts.Count, uniqueFloats.Count);

        // ============================================
        // PHASE 2: BULK create ALL primitive constants
        // ============================================
        var charLookup = await BulkGetOrCreateConstantsAsync(
            uniqueChars.ToArray(), SEED_TYPE_UNICODE, ct);
        
        // For integers/floats, use GetOrCreateConstantAsync from base class
        var intLookup = new Dictionary<long, long>();
        foreach (var val in uniqueInts)
        {
            intLookup[val] = await GetOrCreateConstantAsync(val, SEED_TYPE_INTEGER, ct);
        }

        var floatLookup = new Dictionary<ulong, long>();
        foreach (var bits in uniqueFloats)
        {
            // Store as double precision bits
            floatLookup[bits] = await GetOrCreateConstantAsync((long)bits, SEED_TYPE_FLOAT_BITS, ct);
        }

        // ============================================
        // PHASE 3: Build JSON tree using lookups
        // ============================================
        return await IngestElementAsync(json, charLookup, intLookup, floatLookup, ct);
    }

    private void CollectPrimitives(
        JsonElement element,
        HashSet<uint> chars,
        HashSet<long> ints,
        HashSet<ulong> floats)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    CollectCharsFromString(prop.Name, chars);
                    CollectPrimitives(prop.Value, chars, ints, floats);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPrimitives(item, chars, ints, floats);
                }
                break;

            case JsonValueKind.String:
                CollectCharsFromString(element.GetString()!, chars);
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long intVal))
                {
                    ints.Add(intVal);
                }
                else
                {
                    floats.Add(BitConverter.DoubleToUInt64Bits(element.GetDouble()));
                }
                break;
        }
    }

    private void CollectCharsFromString(string str, HashSet<uint> chars)
    {
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (char.IsHighSurrogate(c) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
            {
                chars.Add((uint)char.ConvertToUtf32(c, str[++i]));
            }
            else
            {
                chars.Add(c);
            }
        }
    }

    public async Task<long> IngestStringAsync(string jsonString, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(jsonString);
        return await IngestAsync(doc.RootElement, ct);
    }

    private async Task<long> IngestElementAsync(
        JsonElement element,
        Dictionary<uint, long> charLookup,
        Dictionary<long, long> intLookup,
        Dictionary<ulong, long> floatLookup,
        CancellationToken ct)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => await IngestObjectAsync(element, charLookup, intLookup, floatLookup, ct),
            JsonValueKind.Array => await IngestArrayAsync(element, charLookup, intLookup, floatLookup, ct),
            JsonValueKind.String => await IngestStringValueAsync(element.GetString()!, charLookup, ct),
            JsonValueKind.Number => IngestNumber(element, intLookup, floatLookup),
            JsonValueKind.True => await GetOrCreateConstantAsync(1, SEED_TYPE_BOOLEAN, ct),
            JsonValueKind.False => await GetOrCreateConstantAsync(0, SEED_TYPE_BOOLEAN, ct),
            JsonValueKind.Null => await GetOrCreateConstantAsync(0, SEED_TYPE_JSON_NULL, ct),
            _ => throw new ArgumentException($"Unknown JSON value kind: {element.ValueKind}")
        };
    }

    private long IngestNumber(JsonElement num, Dictionary<long, long> intLookup, Dictionary<ulong, long> floatLookup)
    {
        if (num.TryGetInt64(out long intValue))
        {
            return intLookup[intValue];
        }
        else
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(num.GetDouble());
            return floatLookup[bits];
        }
    }

    private async Task<long> IngestObjectAsync(
        JsonElement obj,
        Dictionary<uint, long> charLookup,
        Dictionary<long, long> intLookup,
        Dictionary<ulong, long> floatLookup,
        CancellationToken ct)
    {
        var pairIds = new List<long>();
        var pairIsConstant = new List<bool>();

        foreach (var prop in obj.EnumerateObject())
        {
            // Ingest key as text (using lookup, no DB) - returns composition ID
            var keyId = await IngestStringValueAsync(prop.Name, charLookup, ct);
            
            // Ingest value recursively - can return constant or composition
            var (valueId, valueIsConstant) = await IngestElementWithTypeAsync(prop.Value, charLookup, intLookup, floatLookup, ct);
            
            // Create key-value pair composition (key is always a composition from string)
            var pairChildren = new (long id, bool isConstant)[] 
            { 
                (keyId, false), // key string is a composition
                (valueId, valueIsConstant) 
            };
            var pairId = await CreateCompositionAsync(pairChildren, new[] { 1, 1 }, null, ct);
            pairIds.Add(pairId);
            pairIsConstant.Add(false); // pairs are compositions
        }

        if (pairIds.Count == 0)
        {
            // Empty object - special marker constant
            return await GetOrCreateConstantAsync(0xFFFFFFFE, SEED_TYPE_INTEGER, ct);
        }

        var children = pairIds.Select(id => (id, isConstant: false)).ToArray();
        return await CreateCompositionAsync(
            children,
            Enumerable.Repeat(1, pairIds.Count).ToArray(),
            null,
            ct
        );
    }

    private async Task<long> IngestArrayAsync(
        JsonElement arr,
        Dictionary<uint, long> charLookup,
        Dictionary<long, long> intLookup,
        Dictionary<ulong, long> floatLookup,
        CancellationToken ct)
    {
        var elementIds = new List<(long id, bool isConstant)>();

        foreach (var element in arr.EnumerateArray())
        {
            var (elementId, isConst) = await IngestElementWithTypeAsync(element, charLookup, intLookup, floatLookup, ct);
            elementIds.Add((elementId, isConst));
        }

        if (elementIds.Count == 0)
        {
            // Empty array - special marker constant
            return await GetOrCreateConstantAsync(0xFFFFFFFD, SEED_TYPE_INTEGER, ct);
        }

        return await CreateCompositionAsync(
            elementIds.ToArray(),
            Enumerable.Repeat(1, elementIds.Count).ToArray(),
            null,
            ct
        );
    }

    private async Task<long> IngestStringValueAsync(
        string str,
        Dictionary<uint, long> charLookup,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(str))
        {
            return await GetOrCreateConstantAsync(0xFFFFFFFC, SEED_TYPE_INTEGER, ct);
        }

        // RLE compress and use lookup
        var codepoints = ExtractCodepoints(str);
        var constantIds = new List<long>();
        var multiplicities = new List<int>();

        int i = 0;
        while (i < codepoints.Count)
        {
            uint cp = codepoints[i];
            int count = 1;
            while (i + count < codepoints.Count && codepoints[i + count] == cp)
                count++;

            constantIds.Add(charLookup[cp]); // O(1) lookup, NO DB
            multiplicities.Add(count);
            i += count;
        }

        return await CreateCompositionFromConstantsAsync(constantIds.ToArray(), multiplicities.ToArray(), null, ct);
    }

    public async Task<JsonElement> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        // First try to find as a composition
        var composition = await Context.Compositions.FindAsync(new object[] { compositionId }, ct);
        if (composition != null)
        {
            var jsonString = await ReconstructCompositionToStringAsync(compositionId, ct);
            return JsonDocument.Parse(jsonString).RootElement;
        }

        // Otherwise it might be a constant (for primitives)
        var constant = await Context.Constants.FindAsync(new object[] { compositionId }, ct);
        if (constant != null)
        {
            var jsonString = ReconstructConstantToString(constant);
            return JsonDocument.Parse(jsonString).RootElement;
        }

        throw new InvalidOperationException($"Entity {compositionId} not found as Constant or Composition");
    }

    private string ReconstructConstantToString(Constant constant)
    {
        return constant.SeedType switch
        {
            SEED_TYPE_UNICODE => char.ConvertFromUtf32((int)constant.SeedValue),
            SEED_TYPE_INTEGER => constant.SeedValue switch
            {
                0xFFFFFFFE => "{}",        // Empty object
                0xFFFFFFFD => "[]",        // Empty array
                0xFFFFFFFC => "\"\"",      // Empty string
                var v => v.ToString()      // Regular integer
            },
            SEED_TYPE_FLOAT_BITS => BitConverter.UInt64BitsToDouble((ulong)constant.SeedValue).ToString("G17"),
            SEED_TYPE_BOOLEAN => constant.SeedValue == 1 ? "true" : "false",
            SEED_TYPE_JSON_NULL => "null",
            _ => throw new InvalidOperationException($"Unknown constant SeedType: {constant.SeedType}")
        };
    }

    private async Task<string> ReconstructCompositionToStringAsync(long compositionId, CancellationToken ct)
    {
        // Get relations for this composition
        var relations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (relations.Count == 0)
            return "null";

        // Get first child to determine structure type
        var firstRel = relations[0];
        bool firstIsConstant = firstRel.ChildConstantId.HasValue;
        long firstChildId = firstRel.ChildConstantId ?? firstRel.ChildCompositionId!.Value;

        if (firstIsConstant)
        {
            var firstConstant = await Context.Constants.FindAsync(new object[] { firstChildId }, ct);
            if (firstConstant?.SeedType == SEED_TYPE_UNICODE)
            {
                // This is a string (composition of Unicode constants)
                return await ReconstructStringFromCompositionAsync(compositionId, ct);
            }
        }
        else
        {
            // First child is a composition - check if it's a key-value pair (object structure)
            // A key-value pair has exactly 2 children, where the first is a string key
            var firstChildRelations = await Context.Relations
                .Where(r => r.CompositionId == firstChildId)
                .OrderBy(r => r.Position)
                .ToListAsync(ct);

            if (firstChildRelations.Count == 2)
            {
                // Check if the first child of this pair is a string (the key)
                var keyRel = firstChildRelations[0];
                if (keyRel.ChildCompositionId.HasValue)
                {
                    // Key is a composition - check if it's a string (Unicode chars)
                    var keyFirstRel = await Context.Relations
                        .Where(r => r.CompositionId == keyRel.ChildCompositionId.Value)
                        .OrderBy(r => r.Position)
                        .FirstOrDefaultAsync(ct);
                    
                    if (keyFirstRel?.ChildConstantId.HasValue == true)
                    {
                        var keyFirstConstant = await Context.Constants.FindAsync(
                            new object[] { keyFirstRel.ChildConstantId.Value }, ct);
                        if (keyFirstConstant?.SeedType == SEED_TYPE_UNICODE)
                        {
                            // First child is a key-value pair with string key - this is an object
                            return await ReconstructObjectAsync(compositionId, ct);
                        }
                    }
                }
            }
        }

        // Otherwise treat as array
        return await ReconstructArrayAsync(compositionId, ct);
    }

    private async Task<string> ReconstructObjectAsync(long compositionId, CancellationToken ct)
    {
        var relations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (relations.Count == 0)
            return "{}";

        var pairs = new List<string>();

        foreach (var pairRel in relations)
        {
            // Each relation points to a pair composition
            if (!pairRel.ChildCompositionId.HasValue) continue;
            var pairId = pairRel.ChildCompositionId.Value;

            var pairRelations = await Context.Relations
                .Where(r => r.CompositionId == pairId)
                .OrderBy(r => r.Position)
                .ToListAsync(ct);

            if (pairRelations.Count != 2) continue;

            // First child is the key (a string composition)
            var keyRel = pairRelations[0];
            var keyId = keyRel.ChildCompositionId ?? keyRel.ChildConstantId!.Value;
            var keyIsConstant = keyRel.ChildConstantId.HasValue;

            // Second child is the value
            var valueRel = pairRelations[1];
            var valueId = valueRel.ChildCompositionId ?? valueRel.ChildConstantId!.Value;
            var valueIsConstant = valueRel.ChildConstantId.HasValue;

            var key = keyIsConstant
                ? ReconstructConstantToString(await Context.Constants.FindAsync(new object[] { keyId }, ct) ?? throw new InvalidOperationException($"Constant {keyId} not found"))
                : await ReconstructStringFromCompositionAsync(keyId, ct);
            
            var value = valueIsConstant
                ? ReconstructConstantToString(await Context.Constants.FindAsync(new object[] { valueId }, ct) ?? throw new InvalidOperationException($"Constant {valueId} not found"))
                : await ReconstructCompositionToStringAsync(valueId, ct);

            pairs.Add($"{key}:{value}");
        }

        return "{" + string.Join(",", pairs) + "}";
    }

    private async Task<string> ReconstructArrayAsync(long compositionId, CancellationToken ct)
    {
        var relations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (relations.Count == 0)
            return "[]";

        var elements = new List<string>();

        foreach (var rel in relations)
        {
            var childId = rel.ChildCompositionId ?? rel.ChildConstantId!.Value;
            var isConstant = rel.ChildConstantId.HasValue;

            var value = isConstant
                ? ReconstructConstantToString(await Context.Constants.FindAsync(new object[] { childId }, ct) ?? throw new InvalidOperationException($"Constant {childId} not found"))
                : await ReconstructCompositionToStringAsync(childId, ct);
            elements.Add(value);
        }

        return "[" + string.Join(",", elements) + "]";
    }

    private async Task<string> ReconstructStringFromCompositionAsync(long compositionId, CancellationToken ct)
    {
        var relations = await Context.Relations
            .Where(r => r.CompositionId == compositionId)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);

        if (relations.Count == 0)
            return "\"\"";

        var chars = new List<string>();
        var charConstantIds = relations
            .Where(r => r.ChildConstantId.HasValue)
            .Select(r => r.ChildConstantId!.Value)
            .Distinct()
            .ToList();
        var charConstants = await Context.Constants
            .Where(c => charConstantIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var rel in relations)
        {
            if (!rel.ChildConstantId.HasValue) continue;
            
            var charConstant = charConstants[rel.ChildConstantId.Value];
            var mult = rel.Multiplicity;
            var cp = (uint)charConstant.SeedValue;
            var c = char.ConvertFromUtf32((int)cp);

            for (int m = 0; m < mult; m++)
                chars.Add(c);
        }

        var content = string.Join("", chars);
        return JsonSerializer.Serialize(content); // Proper escaping
    }

    private static List<uint> ExtractCodepoints(string text)
    {
        var codepoints = new List<uint>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoints.Add((uint)char.ConvertToUtf32(c, text[++i]));
            }
            else
            {
                codepoints.Add(c);
            }
        }
        return codepoints;
    }

    /// <summary>
    /// Ingest a JSON element and return both the ID and whether it's a constant.
    /// </summary>
    private async Task<(long id, bool isConstant)> IngestElementWithTypeAsync(
        JsonElement element,
        Dictionary<uint, long> charLookup,
        Dictionary<long, long> intLookup,
        Dictionary<ulong, long> floatLookup,
        CancellationToken ct)
    {
        return element.ValueKind switch
        {
            // Objects and arrays become compositions
            JsonValueKind.Object => (await IngestObjectAsync(element, charLookup, intLookup, floatLookup, ct), false),
            JsonValueKind.Array => (await IngestArrayAsync(element, charLookup, intLookup, floatLookup, ct), false),
            // Strings become compositions of character constants
            JsonValueKind.String => (await IngestStringValueAsync(element.GetString()!, charLookup, ct), false),
            // Numbers are constants (already in lookup)
            JsonValueKind.Number => (IngestNumber(element, intLookup, floatLookup), true),
            // Booleans are constants with SEED_TYPE_BOOLEAN
            JsonValueKind.True => (await GetOrCreateConstantAsync(1, SEED_TYPE_BOOLEAN, ct), true),
            JsonValueKind.False => (await GetOrCreateConstantAsync(0, SEED_TYPE_BOOLEAN, ct), true),
            // Null is a constant with SEED_TYPE_JSON_NULL
            JsonValueKind.Null => (await GetOrCreateConstantAsync(0, SEED_TYPE_JSON_NULL, ct), true),
            _ => throw new ArgumentException($"Unknown JSON value kind: {element.ValueKind}")
        };
    }

    /// <summary>
    /// BULK get or create constants for multiple seed values.
    /// Queries DB ONCE for all existing, batch inserts all missing.
    /// </summary>
    private async Task<Dictionary<uint, long>> BulkGetOrCreateConstantsAsync(
        uint[] seedValues,
        int seedType,
        CancellationToken ct)
    {
        var result = new Dictionary<uint, long>(seedValues.Length);
        
        // Get unique values
        var uniqueValues = seedValues.Distinct().ToArray();
        
        // Convert to long for query (SeedValue is stored as long)
        var valuesAsLong = uniqueValues.Select(v => (long)v).ToList();

        // SINGLE QUERY: Get all existing constants by seed value and seedType
        var existing = await Context.Constants
            .Where(c => c.SeedType == seedType && valuesAsLong.Contains(c.SeedValue))
            .Select(c => new { c.Id, c.SeedValue })
            .ToListAsync(ct);

        // Map existing to result
        foreach (var constant in existing)
        {
            var val = (uint)constant.SeedValue;
            result[val] = constant.Id;
        }

        // Find missing values
        var missingValues = uniqueValues.Where(v => !result.ContainsKey(v)).ToList();
        
        if (missingValues.Count > 0)
        {
            // BATCH INSERT: Create all missing constants
            var newConstants = new List<(uint Value, Constant Constant)>();
            
            foreach (var val in missingValues)
            {
                var contentHash = HartNative.ComputeSeedHash(val);
                var point = HartNative.project_seed_to_hypersphere(val);
                var hilbert = HartNative.point_to_hilbert(point);
                var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

                var constant = new Constant
                {
                    HilbertHigh = (ulong)hilbert.High,
                    HilbertLow = (ulong)hilbert.Low,
                    Geom = geom,
                    SeedValue = val,
                    SeedType = seedType,
                    ContentHash = contentHash
                };

                Context.Constants.Add(constant);
                newConstants.Add((val, constant));
            }

            // ONE SaveChanges for all new constants
            await Context.SaveChangesAsync(ct);

            // Map new constants to result
            foreach (var (val, constant) in newConstants)
            {
                result[val] = constant.Id;
            }
        }

        return result;
    }
}
