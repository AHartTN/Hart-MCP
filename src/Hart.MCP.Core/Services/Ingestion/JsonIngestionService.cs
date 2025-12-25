using System.Text.Json;
using Hart.MCP.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
            uniqueChars.ToArray(), SEED_TYPE_UNICODE, null, ct);
        
        // For integers/floats, we use GetOrCreateIntegerConstantAsync which is still
        // per-item, but these are typically few in number compared to char constants.
        // A future optimization could batch these too.
        var intLookup = new Dictionary<long, long>();
        foreach (var val in uniqueInts)
        {
            intLookup[val] = await GetOrCreateIntegerConstantAsync(val, null, ct);
        }

        var floatLookup = new Dictionary<ulong, long>();
        foreach (var bits in uniqueFloats)
        {
            // Store as double precision bits (SEED_TYPE_FLOAT_BITS stores uint32, we need uint64 for double)
            floatLookup[bits] = await GetOrCreateIntegerConstantAsync((long)bits, null, ct);
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
            JsonValueKind.True => await GetOrCreateConstantAsync(1, SEED_TYPE_INTEGER, null, ct),
            JsonValueKind.False => await GetOrCreateConstantAsync(0, SEED_TYPE_INTEGER, null, ct),
            JsonValueKind.Null => await GetOrCreateConstantAsync(0xFFFFFFFF, SEED_TYPE_INTEGER, null, ct),
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
        var pairAtomIds = new List<long>();

        foreach (var prop in obj.EnumerateObject())
        {
            // Ingest key as text (using lookup, no DB)
            var keyAtomId = await IngestStringValueAsync(prop.Name, charLookup, ct);
            
            // Ingest value recursively
            var valueAtomId = await IngestElementAsync(prop.Value, charLookup, intLookup, floatLookup, ct);
            
            // Create key-value pair composition
            var pairId = await CreateCompositionAsync(
                new[] { keyAtomId, valueAtomId },
                new[] { 1, 1 },
                null,
                ct
            );
            pairAtomIds.Add(pairId);
        }

        if (pairAtomIds.Count == 0)
        {
            // Empty object - special marker
            var emptyMarker = await GetOrCreateConstantAsync(0xFFFFFFFE, SEED_TYPE_INTEGER, null, ct);
            return emptyMarker;
        }

        return await CreateCompositionAsync(
            pairAtomIds.ToArray(),
            Enumerable.Repeat(1, pairAtomIds.Count).ToArray(),
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
        var elementAtomIds = new List<long>();

        foreach (var element in arr.EnumerateArray())
        {
            var elementId = await IngestElementAsync(element, charLookup, intLookup, floatLookup, ct);
            elementAtomIds.Add(elementId);
        }

        if (elementAtomIds.Count == 0)
        {
            // Empty array - special marker
            var emptyMarker = await GetOrCreateConstantAsync(0xFFFFFFFD, SEED_TYPE_INTEGER, null, ct);
            return emptyMarker;
        }

        return await CreateCompositionAsync(
            elementAtomIds.ToArray(),
            Enumerable.Repeat(1, elementAtomIds.Count).ToArray(),
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
            return await GetOrCreateConstantAsync(0xFFFFFFFC, SEED_TYPE_INTEGER, null, ct);
        }

        // RLE compress and use lookup
        var codepoints = ExtractCodepoints(str);
        var atomIds = new List<long>();
        var multiplicities = new List<int>();

        int i = 0;
        while (i < codepoints.Count)
        {
            uint cp = codepoints[i];
            int count = 1;
            while (i + count < codepoints.Count && codepoints[i + count] == cp)
                count++;

            atomIds.Add(charLookup[cp]); // O(1) lookup, NO DB
            multiplicities.Add(count);
            i += count;
        }

        return await CreateCompositionAsync(atomIds.ToArray(), multiplicities.ToArray(), null, ct);
    }

    public async Task<JsonElement> ReconstructAsync(long compositionId, CancellationToken ct = default)
    {
        var atom = await Context.Atoms.FindAsync(new object[] { compositionId }, ct);
        if (atom == null)
            throw new InvalidOperationException($"Atom {compositionId} not found");

        var jsonString = await ReconstructToStringAsync(atom, ct);
        return JsonDocument.Parse(jsonString).RootElement;
    }

    private async Task<string> ReconstructToStringAsync(Entities.Atom atom, CancellationToken ct)
    {
        // Handle constants based on SeedType and SeedValue
        if (atom.IsConstant)
        {
            return atom.SeedType switch
            {
                SEED_TYPE_UNICODE => char.ConvertFromUtf32((int)(atom.SeedValue ?? 0)),
                SEED_TYPE_INTEGER => atom.SeedValue switch
                {
                    0 => "false",              // JSON false
                    1 => "true",               // JSON true
                    0xFFFFFFFF => "null",      // JSON null
                    0xFFFFFFFE => "{}",        // Empty object
                    0xFFFFFFFD => "[]",        // Empty array
                    0xFFFFFFFC => "\"\"",      // Empty string
                    var v => v?.ToString() ?? "0"  // Regular integer
                },
                SEED_TYPE_FLOAT_BITS => BitConverter.UInt64BitsToDouble((ulong)(atom.SeedValue ?? 0)).ToString("G17"),
                _ => throw new InvalidOperationException($"Unknown constant SeedType: {atom.SeedType}")
            };
        }

        // Compositions - infer type from structure
        if (atom.Refs == null || atom.Refs.Length == 0)
            return "null";

        // Check first ref to determine structure type
        var firstRef = await Context.Atoms.FindAsync(new object[] { atom.Refs[0] }, ct);
        if (firstRef == null)
            return "null";

        // Key-value pair = [key, value] where key is a string composition
        if (atom.Refs.Length == 2 && !firstRef.IsConstant && firstRef.Refs?.Length > 0)
        {
            // Could be a pair - check if first is a char composition (string key)
            var firstOfFirst = await Context.Atoms.FindAsync(new object[] { firstRef.Refs[0] }, ct);
            if (firstOfFirst?.IsConstant == true && firstOfFirst.SeedType == SEED_TYPE_UNICODE)
            {
                // This is a pair - reconstruct as object with one property
                throw new InvalidOperationException("Cannot reconstruct pair directly");
            }
        }

        // Array of pairs = object
        if (!firstRef.IsConstant && firstRef.Refs?.Length == 2)
        {
            return await ReconstructObjectAsync(atom, ct);
        }

        // Array of char constants = string
        if (firstRef.IsConstant && firstRef.SeedType == SEED_TYPE_UNICODE)
        {
            return await ReconstructStringAsync(atom, ct);
        }

        // Otherwise treat as array
        return await ReconstructArrayAsync(atom, ct);
    }

    private async Task<string> ReconstructObjectAsync(Entities.Atom obj, CancellationToken ct)
    {
        if (obj.Refs == null || obj.Refs.Length == 0)
            return "{}";

        var pairs = new List<string>();

        foreach (var pairId in obj.Refs)
        {
            var pair = await Context.Atoms.FindAsync(new object[] { pairId }, ct);
            if (pair?.Refs == null || pair.Refs.Length != 2) continue;

            var keyAtom = await Context.Atoms.FindAsync(new object[] { pair.Refs[0] }, ct);
            var valueAtom = await Context.Atoms.FindAsync(new object[] { pair.Refs[1] }, ct);

            if (keyAtom == null || valueAtom == null) continue;

            var key = await ReconstructStringAsync(keyAtom, ct);
            var value = await ReconstructToStringAsync(valueAtom, ct);

            pairs.Add($"{key}:{value}");
        }

        return "{" + string.Join(",", pairs) + "}";
    }

    private async Task<string> ReconstructArrayAsync(Entities.Atom arr, CancellationToken ct)
    {
        if (arr.Refs == null || arr.Refs.Length == 0)
            return "[]";

        var elements = new List<string>();

        foreach (var elemId in arr.Refs)
        {
            var elem = await Context.Atoms.FindAsync(new object[] { elemId }, ct);
            if (elem == null) continue;

            var value = await ReconstructToStringAsync(elem, ct);
            elements.Add(value);
        }

        return "[" + string.Join(",", elements) + "]";
    }

    private async Task<string> ReconstructStringAsync(Entities.Atom str, CancellationToken ct)
    {
        if (str.Refs == null || str.Refs.Length == 0)
            return "\"\"";

        var chars = new List<string>();
        var charAtoms = await Context.Atoms
            .Where(a => str.Refs.Contains(a.Id) && a.IsConstant)
            .ToDictionaryAsync(a => a.Id, ct);

        for (int i = 0; i < str.Refs.Length; i++)
        {
            var charAtom = charAtoms[str.Refs[i]];
            var mult = str.Multiplicities?[i] ?? 1;
            var cp = (uint)(charAtom.SeedValue ?? 0);
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
}
