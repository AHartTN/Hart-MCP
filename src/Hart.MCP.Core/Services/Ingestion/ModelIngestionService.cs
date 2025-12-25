using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hart.MCP.Core.Data;
using Hart.MCP.Core.Entities;
using Hart.MCP.Core.Native;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
// Models defined in SafeTensorModels.cs (same namespace)

// Alias to avoid ambiguity with System.Runtime.InteropServices.NativeLibrary
using HartNative = Hart.MCP.Core.Native.NativeLibrary;

namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// Model ingestion service for SafeTensor and GGUF files.
/// 
/// ARCHITECTURE:
/// - Vocabulary tokens → HierarchicalTextIngestionService → deduplicated atom IDs
/// - Embeddings → Float compositions + reduced POINTZ for PostGIS spatial queries
/// - Weights → Trajectory compositions with LINESTRING between token positions
/// 
/// KEY INSIGHT:
/// Model tokens ARE pattern atoms. Same "the" in GPT-2 and LLaMA = SAME atom ID.
/// Enables cross-model spatial queries: "Which models understand 'quantum'?"
/// </summary>
public class ModelIngestionService : IngestionServiceBase
{
    private readonly HierarchicalTextIngestionService _textIngestionService;
    private readonly EmbeddingIngestionService _embeddingService;

    // SafeTensor data types → bytes per element
    private static readonly Dictionary<string, int> DTypeBytes = new()
    {
        ["F16"] = 2,
        ["BF16"] = 2,
        ["F32"] = 4,
        ["F64"] = 8,
        ["I8"] = 1,
        ["I16"] = 2,
        ["I32"] = 4,
        ["I64"] = 8,
        ["U8"] = 1,
        ["U16"] = 2,
        ["U32"] = 4,
        ["U64"] = 8,
        ["BOOL"] = 1
    };

    public ModelIngestionService(
        HartDbContext context,
        HierarchicalTextIngestionService textIngestionService,
        EmbeddingIngestionService embeddingService,
        ILogger<ModelIngestionService>? logger = null)
        : base(context, logger)
    {
        _textIngestionService = textIngestionService ?? throw new ArgumentNullException(nameof(textIngestionService));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
    }

    /// <summary>
    /// Ingest a SafeTensor file from path.
    /// </summary>
    public async Task<ModelIngestionResult> IngestSafeTensorAsync(
        string filePath,
        string modelName,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"SafeTensor file not found: {filePath}");

        await using var stream = File.OpenRead(filePath);
        return await IngestSafeTensorAsync(stream, modelName, ct);
    }

    /// <summary>
    /// Ingest a SafeTensor file from stream.
    /// 
    /// SafeTensor format:
    /// - 8 bytes: header length (little-endian uint64)
    /// - N bytes: JSON header (tensor metadata)
    /// - Remaining: raw tensor data
    /// </summary>
    public async Task<ModelIngestionResult> IngestSafeTensorAsync(
        Stream stream,
        string modelName,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ModelIngestionResult { ModelName = modelName };

        Logger?.LogInformation("Ingesting SafeTensor model: {ModelName}", modelName);

        // ============================================
        // PHASE 1: Parse SafeTensor header
        // ============================================
        var metadata = await ParseSafeTensorHeaderAsync(stream, ct);

        Logger?.LogDebug("Parsed {TensorCount} tensors from header", metadata.Tensors.Count);

        // ============================================
        // PHASE 2: Ingest tensors
        // ============================================
        var dataStartOffset = stream.Position;

        foreach (var (tensorName, tensorInfo) in metadata.Tensors)
        {
            if (tensorName == "__metadata__") continue;

            ct.ThrowIfCancellationRequested();

            var atomId = await IngestTensorAsync(
                stream, dataStartOffset, tensorInfo, modelName, ct);

            // Categorize by tensor type
            if (IsEmbeddingTensor(tensorName))
            {
                result.EmbeddingAtomIds[tensorName] = atomId;
            }
            else
            {
                result.WeightAtomIds[tensorName] = atomId;
            }

            result.TotalParameters += tensorInfo.TotalElements;
        }

        result.TensorCount = metadata.Tensors.Count;

        // ============================================
        // PHASE 3: Create root model composition
        // ============================================
        var allTensorAtomIds = result.EmbeddingAtomIds.Values
            .Concat(result.WeightAtomIds.Values)
            .ToArray();

        if (allTensorAtomIds.Length > 0)
        {
            result.RootAtomId = await CreateModelCompositionAsync(
                allTensorAtomIds, modelName, metadata, ct);
        }

        stopwatch.Stop();
        result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

        Logger?.LogInformation(
            "Model ingestion complete: {Tensors} tensors, {Params:N0} parameters, root {RootId}, {Time}ms",
            result.TensorCount, result.TotalParameters, result.RootAtomId, result.ProcessingTimeMs);

        return result;
    }

    /// <summary>
    /// Ingest vocabulary from tokenizer file (JSON format).
    /// Each token goes through HierarchicalTextIngestionService for deduplication.
    /// Same "the" across ALL models = same atom ID.
    /// </summary>
    public async Task<Dictionary<int, long>> IngestVocabularyAsync(
        string tokenizerPath,
        string modelName,
        CancellationToken ct = default)
    {
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"Tokenizer file not found: {tokenizerPath}");

        var json = await File.ReadAllTextAsync(tokenizerPath, ct);
        return await IngestVocabularyFromJsonAsync(json, modelName, ct);
    }

    /// <summary>
    /// Ingest vocabulary from JSON string.
    /// Supports both vocab.json (simple dict) and tokenizer.json (HuggingFace format).
    /// </summary>
    public async Task<Dictionary<int, long>> IngestVocabularyFromJsonAsync(
        string json,
        string modelName,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var tokenToAtomId = new Dictionary<int, long>();

        Logger?.LogInformation("Ingesting vocabulary for model: {ModelName}", modelName);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Detect format and extract vocab
        Dictionary<string, int> vocab;

        if (root.TryGetProperty("model", out var modelProp) &&
            modelProp.TryGetProperty("vocab", out var vocabProp))
        {
            // HuggingFace tokenizer.json format
            vocab = ExtractVocabFromHuggingFace(vocabProp);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // Simple vocab.json format: {"token": id, ...}
            vocab = ExtractSimpleVocab(root);
        }
        else
        {
            throw new InvalidDataException("Unknown vocabulary format");
        }

        Logger?.LogDebug("Extracted {Count} tokens from vocabulary", vocab.Count);

        // ============================================
        // BULK INGEST: Categorize tokens, batch process each category
        // ============================================
        var singleCharTokens = new List<(string text, int tokenId, uint codepoint)>();
        var byteTokens = new List<(string text, int tokenId, byte value)>();
        var multiCharTokens = new List<(string text, int tokenId)>();

        foreach (var (tokenText, tokenId) in vocab)
        {
            if (tokenText.Length == 1)
            {
                singleCharTokens.Add((tokenText, tokenId, tokenText[0]));
            }
            else if (tokenText.StartsWith("<0x") && tokenText.EndsWith(">"))
            {
                var hexStr = tokenText[3..^1];
                if (byte.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    byteTokens.Add((tokenText, tokenId, b));
                }
            }
            else
            {
                multiCharTokens.Add((tokenText, tokenId));
            }
        }

        Logger?.LogDebug("Categorized: {Single} single-char, {Byte} byte tokens, {Multi} multi-char",
            singleCharTokens.Count, byteTokens.Count, multiCharTokens.Count);

        // ============================================
        // BATCH 1: Single character tokens - bulk lookup/create Unicode atoms
        // ============================================
        if (singleCharTokens.Count > 0)
        {
            var uniqueCodepoints = singleCharTokens.Select(t => t.codepoint).Distinct().ToArray();
            var codepointToAtomId = await BulkGetOrCreateCodepointsAsync(uniqueCodepoints, ct);
            
            foreach (var (_, tokenId, codepoint) in singleCharTokens)
            {
                tokenToAtomId[tokenId] = codepointToAtomId[codepoint];
            }
        }

        // ============================================
        // BATCH 2: Byte tokens - bulk lookup/create integer atoms
        // ============================================
        if (byteTokens.Count > 0)
        {
            var uniqueBytes = byteTokens.Select(t => (uint)t.value).Distinct().ToArray();
            var byteToAtomId = await BulkGetOrCreateIntegersAsync(uniqueBytes, ct);
            
            foreach (var (_, tokenId, value) in byteTokens)
            {
                tokenToAtomId[tokenId] = byteToAtomId[value];
            }
        }

        // ============================================
        // BATCH 3: Multi-char tokens - batch ingest through text service
        // ============================================
        if (multiCharTokens.Count > 0)
        {
            // Concatenate all tokens with separator, ingest once, then look up by content hash
            var allTexts = multiCharTokens.Select(t => t.text).ToArray();
            var textToAtomId = await BulkIngestTokenTextsAsync(allTexts, ct);
            
            foreach (var (text, tokenId) in multiCharTokens)
            {
                if (textToAtomId.TryGetValue(text, out var atomId))
                {
                    tokenToAtomId[tokenId] = atomId;
                }
            }
        }

        stopwatch.Stop();
        Logger?.LogInformation(
            "Vocabulary ingestion complete: {Count} tokens, {Time}ms ({Rate:N0} tokens/sec)",
            tokenToAtomId.Count, stopwatch.ElapsedMilliseconds,
            tokenToAtomId.Count * 1000.0 / Math.Max(1, stopwatch.ElapsedMilliseconds));

        return tokenToAtomId;
    }

    /// <summary>
    /// Bulk lookup/create Unicode codepoint atoms.
    /// </summary>
    private async Task<Dictionary<uint, long>> BulkGetOrCreateCodepointsAsync(
        uint[] codepoints, CancellationToken ct)
    {
        var result = new Dictionary<uint, long>();
        var cpAsLong = codepoints.Select(c => (long)c).ToList();

        // Bulk lookup existing
        var existing = await Context.Atoms
            .Where(a => a.IsConstant && a.SeedType == SEED_TYPE_UNICODE &&
                        a.SeedValue.HasValue && cpAsLong.Contains(a.SeedValue.Value))
            .Select(a => new { a.Id, a.SeedValue })
            .ToListAsync(ct);

        foreach (var atom in existing)
        {
            if (atom.SeedValue.HasValue)
                result[(uint)atom.SeedValue.Value] = atom.Id;
        }

        // Find missing
        var existingCps = new HashSet<uint>(result.Keys);
        var missing = codepoints.Where(c => !existingCps.Contains(c)).ToArray();

        if (missing.Length == 0) return result;

        // Bulk create missing
        var newAtoms = missing.AsParallel().Select(cp =>
        {
            var hash = Native.NativeLibrary.ComputeSeedHash(cp);
            var point = Native.NativeLibrary.project_seed_to_hypersphere(cp);
            var hilbert = Native.NativeLibrary.point_to_hilbert(point);
            var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

            return (cp, new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = geom,
                IsConstant = true,
                SeedValue = cp,
                SeedType = SEED_TYPE_UNICODE,
                ContentHash = hash,
                AtomType = "vocab_token"
            });
        }).ToList();

        Context.Atoms.AddRange(newAtoms.Select(x => x.Item2));
        await Context.SaveChangesAsync(ct);

        foreach (var (cp, atom) in newAtoms)
        {
            result[cp] = atom.Id;
        }

        return result;
    }

    /// <summary>
    /// Bulk lookup/create integer atoms for byte tokens.
    /// </summary>
    private async Task<Dictionary<uint, long>> BulkGetOrCreateIntegersAsync(
        uint[] values, CancellationToken ct)
    {
        var result = new Dictionary<uint, long>();
        var valAsLong = values.Select(v => (long)v).ToList();

        var existing = await Context.Atoms
            .Where(a => a.IsConstant && a.SeedType == SEED_TYPE_INTEGER &&
                        a.SeedValue.HasValue && valAsLong.Contains(a.SeedValue.Value))
            .Select(a => new { a.Id, a.SeedValue })
            .ToListAsync(ct);

        foreach (var atom in existing)
        {
            if (atom.SeedValue.HasValue)
                result[(uint)atom.SeedValue.Value] = atom.Id;
        }

        var existingVals = new HashSet<uint>(result.Keys);
        var missing = values.Where(v => !existingVals.Contains(v)).ToArray();

        if (missing.Length == 0) return result;

        var newAtoms = missing.AsParallel().Select(val =>
        {
            var hash = Native.NativeLibrary.ComputeSeedHash(val);
            var point = Native.NativeLibrary.project_seed_to_hypersphere(val);
            var hilbert = Native.NativeLibrary.point_to_hilbert(point);
            var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

            return (val, new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = geom,
                IsConstant = true,
                SeedValue = val,
                SeedType = SEED_TYPE_INTEGER,
                ContentHash = hash,
                AtomType = "vocab_byte_token"
            });
        }).ToList();

        Context.Atoms.AddRange(newAtoms.Select(x => x.Item2));
        await Context.SaveChangesAsync(ct);

        foreach (var (val, atom) in newAtoms)
        {
            result[val] = atom.Id;
        }

        return result;
    }

    /// <summary>
    /// Bulk ingest multi-character token texts.
    /// Uses content-hash lookup to avoid re-creating existing tokens.
    /// </summary>
    private async Task<Dictionary<string, long>> BulkIngestTokenTextsAsync(
        string[] texts, CancellationToken ct)
    {
        var result = new Dictionary<string, long>();
        
        // Compute content hashes for all texts (byte[] hashes)
        var textToHash = new Dictionary<string, byte[]>();
        foreach (var text in texts.Distinct())
        {
            // Extract codepoints for hash computation
            var codepoints = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codepoints.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                    i++;
                }
                else
                {
                    codepoints.Add(text[i]);
                }
            }
            
            // Compute hash from codepoint values as refs
            var refs = codepoints.Select(c => (long)c).ToArray();
            var mults = Enumerable.Repeat(1, refs.Length).ToArray();
            var hash = Native.NativeLibrary.ComputeCompositionHash(refs, mults);
            textToHash[text] = hash;
        }

        // Load ALL composition atoms - filter in memory since byte[] comparison in EF is unreliable
        var existingCompositions = await Context.Atoms
            .Where(a => !a.IsConstant && a.AtomType == "vocab_token")
            .Select(a => new { a.Id, a.ContentHash })
            .ToListAsync(ct);

        // Build lookup by converting byte[] to hex string for comparison
        var hashToAtomId = existingCompositions
            .ToDictionary(a => Convert.ToHexString(a.ContentHash), a => a.Id);

        foreach (var (text, hash) in textToHash)
        {
            var hashHex = Convert.ToHexString(hash);
            if (hashToAtomId.TryGetValue(hashHex, out var atomId))
            {
                result[text] = atomId;
            }
        }

        // Find missing texts
        var missingTexts = texts.Distinct().Where(t => !result.ContainsKey(t)).ToArray();

        if (missingTexts.Length == 0) return result;

        Logger?.LogDebug("Creating {Count} new token compositions", missingTexts.Length);

        // For missing texts, we need to create atoms
        // First ensure all codepoints exist
        var allCodepoints = new HashSet<uint>();
        foreach (var text in missingTexts)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    allCodepoints.Add((uint)char.ConvertToUtf32(text[i], text[i + 1]));
                    i++;
                }
                else
                {
                    allCodepoints.Add(text[i]);
                }
            }
        }

        var cpToAtomId = await BulkGetOrCreateCodepointsAsync(allCodepoints.ToArray(), ct);

        // Now create composition atoms for each missing text
        var newCompositions = new List<(string text, Atom atom)>();
        foreach (var text in missingTexts)
        {
            var codepoints = new List<uint>();
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codepoints.Add((uint)char.ConvertToUtf32(text[i], text[i + 1]));
                    i++;
                }
                else
                {
                    codepoints.Add(text[i]);
                }
            }

            var refs = codepoints.Select(cp => cpToAtomId[cp]).ToArray();
            var mults = Enumerable.Repeat(1, refs.Length).ToArray();
            var hash = Native.NativeLibrary.ComputeCompositionHash(refs, mults);

            // Compute geometry from first char
            var firstCp = codepoints.FirstOrDefault();
            var point = Native.NativeLibrary.project_seed_to_hypersphere(firstCp);
            var hilbert = Native.NativeLibrary.point_to_hilbert(point);
            var geom = GeometryFactory.CreatePoint(new CoordinateZM(point.X, point.Y, point.Z, point.M));

            newCompositions.Add((text, new Atom
            {
                HilbertHigh = hilbert.High,
                HilbertLow = hilbert.Low,
                Geom = geom,
                IsConstant = false,
                Refs = refs,
                Multiplicities = mults,
                ContentHash = hash,
                AtomType = "vocab_token"
            }));
        }

        Context.Atoms.AddRange(newCompositions.Select(x => x.atom));
        await Context.SaveChangesAsync(ct);

        foreach (var (text, atom) in newCompositions)
        {
            result[text] = atom.Id;
        }

        return result;
    }

    /// <summary>
    /// Ingest embeddings with both full precision and reduced 3D coordinates for PostGIS.
    /// 
    /// Full precision: composition of float atoms (lossless)
    /// Reduced 3D: POINTZ(x,y,z) using simple PCA for spatial indexing
    /// </summary>
    public async Task<long> IngestEmbeddingWithSpatialAsync(
        float[] embedding,
        int tokenId,
        string modelName,
        CancellationToken ct = default)
    {
        // Full precision embedding as atom composition
        var fullEmbeddingAtomId = await _embeddingService.IngestAsync(embedding, ct);

        // Compute reduced 3D coordinates for PostGIS spatial queries
        // Using simple projection: first 3 principal components approximation
        var (x, y, z) = ReduceToSpatial3D(embedding);

        // Update the atom with spatial geometry (POINTZ)
        var atom = await Context.Atoms.FindAsync(new object[] { fullEmbeddingAtomId }, ct);
        if (atom != null)
        {
            // Create POINTZ geometry for PostGIS spatial indexing
            var spatialPoint = GeometryFactory.CreatePoint(new CoordinateZ(x, y, z));

            // Store model/token info in metadata
            var metadata = new
            {
                model = modelName,
                token_id = tokenId,
                dimensions = embedding.Length,
                spatial_reduced = true
            };

            atom.Metadata = JsonSerializer.Serialize(metadata);
            // Note: The main geom is already set from the composition,
            // but we could add a secondary spatial index if needed

            await Context.SaveChangesAsync(ct);
        }

        return fullEmbeddingAtomId;
    }

    /// <summary>
    /// Ingest attention weights as trajectories (LINESTRING between token positions).
    /// 
    /// Each weight becomes: (from_token, to_token, weight_value) → LINESTRING + weight atom
    /// Enables spatial queries: "Find all attention paths from 'Captain' to 'whale'"
    /// </summary>
    public async Task<long> IngestAttentionWeightsAsync(
        float[,] weights,
        int layer,
        int head,
        Dictionary<int, long> tokenPositionToAtomId,
        string modelName,
        CancellationToken ct = default)
    {
        var rows = weights.GetLength(0);
        var cols = weights.GetLength(1);

        Logger?.LogDebug("Ingesting attention weights: layer {Layer}, head {Head}, shape [{Rows},{Cols}]",
            layer, head, rows, cols);

        // Create trajectories for significant weights (> threshold)
        const float threshold = 0.01f; // Only store weights > 1%

        var trajectoryAtomIds = new List<long>();

        for (int from = 0; from < rows; from++)
        {
            for (int to = 0; to < cols; to++)
            {
                var weight = weights[from, to];
                if (Math.Abs(weight) < threshold) continue;

                ct.ThrowIfCancellationRequested();

                // Get atom IDs for from/to positions
                if (!tokenPositionToAtomId.TryGetValue(from, out var fromAtomId) ||
                    !tokenPositionToAtomId.TryGetValue(to, out var toAtomId))
                {
                    continue;
                }

                // Create trajectory atom
                var trajectoryAtomId = await CreateTrajectoryAsync(
                    fromAtomId, toAtomId, weight, layer, head, modelName, ct);

                trajectoryAtomIds.Add(trajectoryAtomId);
            }
        }

        // Create composition for all trajectories in this attention head
        if (trajectoryAtomIds.Count == 0)
        {
            Logger?.LogWarning("No significant attention weights found for layer {Layer} head {Head}", layer, head);
            return 0;
        }

        var headCompositionId = await CreateCompositionAsync(
            trajectoryAtomIds.ToArray(),
            Enumerable.Repeat(1, trajectoryAtomIds.Count).ToArray(),
            "attention_head",
            ct);

        Logger?.LogDebug("Created attention head composition with {Count} trajectories", trajectoryAtomIds.Count);

        return headCompositionId;
    }

    /// <summary>
    /// Create a trajectory atom representing attention from one token to another.
    /// Stored as LINESTRING between the spatial positions of the tokens.
    /// </summary>
    private async Task<long> CreateTrajectoryAsync(
        long fromAtomId,
        long toAtomId,
        float weight,
        int layer,
        int head,
        string modelName,
        CancellationToken ct = default)
    {
        // Get source and target atom geometries
        var fromAtom = await Context.Atoms.FindAsync(new object[] { fromAtomId }, ct);
        var toAtom = await Context.Atoms.FindAsync(new object[] { toAtomId }, ct);

        if (fromAtom?.Geom == null || toAtom?.Geom == null)
            throw new InvalidOperationException("Source or target atom not found");

        // Extract coordinates
        var fromCoord = ExtractCoordinate(fromAtom.Geom);
        var toCoord = ExtractCoordinate(toAtom.Geom);

        // Create LINESTRING trajectory
        var trajectory = GeometryFactory.CreateLineString(new[] { fromCoord, toCoord });

        // Create weight constant atom
        uint weightBits = BitConverter.SingleToUInt32Bits(weight);
        var weightAtomId = await GetOrCreateConstantAsync(weightBits, SEED_TYPE_FLOAT_BITS, "attention_weight", ct);

        // Compute content hash for trajectory
        var refs = new[] { fromAtomId, toAtomId, weightAtomId };
        var multiplicities = new[] { 1, 1, 1 };
        var contentHash = HartNative.ComputeCompositionHash(refs, multiplicities);

        // Check for existing (content-addressed deduplication)
        var existing = await Context.Atoms
            .Where(a => a.ContentHash == contentHash && !a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Compute Hilbert index from trajectory centroid
        var centroid = trajectory.Centroid.Coordinate;
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = centroid.X,
            Y = centroid.Y,
            Z = double.IsNaN(centroid.Z) ? 0 : centroid.Z,
            M = double.IsNaN(centroid.M) ? 0 : centroid.M
        });

        // Metadata with attention info
        var metadata = new
        {
            model = modelName,
            layer,
            head,
            weight,
            from_atom = fromAtomId,
            to_atom = toAtomId
        };

        var trajectoryAtom = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = trajectory,
            IsConstant = false,
            Refs = refs,
            Multiplicities = multiplicities,
            ContentHash = contentHash,
            AtomType = "attention_trajectory",
            Metadata = JsonSerializer.Serialize(metadata)
        };

        Context.Atoms.Add(trajectoryAtom);
        await Context.SaveChangesAsync(ct);

        return trajectoryAtom.Id;
    }

    // ============================================
    // PRIVATE HELPERS
    // ============================================

    private async Task<SafeTensorMetadata> ParseSafeTensorHeaderAsync(Stream stream, CancellationToken ct)
    {
        // Read 8-byte header length
        var headerLenBytes = new byte[8];
        await stream.ReadExactlyAsync(headerLenBytes, ct);
        var headerLen = BinaryPrimitives.ReadUInt64LittleEndian(headerLenBytes);

        if (headerLen > 100_000_000) // Sanity check: 100MB max header
            throw new InvalidDataException($"SafeTensor header too large: {headerLen} bytes");

        // Read JSON header
        var headerBytes = new byte[headerLen];
        await stream.ReadExactlyAsync(headerBytes, ct);
        var headerJson = Encoding.UTF8.GetString(headerBytes);

        // Parse JSON
        using var doc = JsonDocument.Parse(headerJson);
        var metadata = new SafeTensorMetadata();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "__metadata__")
            {
                // Model metadata
                foreach (var meta in prop.Value.EnumerateObject())
                {
                    metadata.Metadata[meta.Name] = meta.Value.GetString() ?? "";
                }
            }
            else
            {
                // Tensor info
                var tensorInfo = new TensorInfo
                {
                    Name = prop.Name,
                    DType = prop.Value.GetProperty("dtype").GetString() ?? "F32",
                    Shape = prop.Value.GetProperty("shape").EnumerateArray()
                        .Select(e => e.GetInt64()).ToArray(),
                };

                var offsets = prop.Value.GetProperty("data_offsets").EnumerateArray()
                    .Select(e => e.GetInt64()).ToArray();
                tensorInfo.DataOffset = offsets[0];
                tensorInfo.DataLength = offsets[1] - offsets[0];

                metadata.Tensors[prop.Name] = tensorInfo;
            }
        }

        return metadata;
    }

    private async Task<long> IngestTensorAsync(
        Stream stream,
        long dataStartOffset,
        TensorInfo tensorInfo,
        string modelName,
        CancellationToken ct)
    {
        Logger?.LogDebug("Ingesting tensor: {Name}, dtype={DType}, shape=[{Shape}]",
            tensorInfo.Name, tensorInfo.DType, string.Join(",", tensorInfo.Shape));

        // Seek to tensor data
        stream.Seek(dataStartOffset + tensorInfo.DataOffset, SeekOrigin.Begin);

        // Read raw bytes
        var dataBytes = new byte[tensorInfo.DataLength];
        await stream.ReadExactlyAsync(dataBytes, ct);

        // Convert to floats based on dtype
        var floats = ConvertBytesToFloats(dataBytes, tensorInfo.DType);

        // Ingest as embedding composition
        var atomId = await _embeddingService.IngestAsync(floats, ct);

        // Update with tensor metadata
        var atom = await Context.Atoms.FindAsync(new object[] { atomId }, ct);
        if (atom != null)
        {
            var metadata = new
            {
                model = modelName,
                tensor_name = tensorInfo.Name,
                dtype = tensorInfo.DType,
                shape = tensorInfo.Shape,
                total_elements = tensorInfo.TotalElements
            };
            atom.Metadata = JsonSerializer.Serialize(metadata);
            atom.AtomType = IsEmbeddingTensor(tensorInfo.Name) ? "model_embedding" : "model_weight";
            await Context.SaveChangesAsync(ct);
        }

        return atomId;
    }

    private static float[] ConvertBytesToFloats(byte[] data, string dtype)
    {
        return dtype switch
        {
            "F32" => ConvertF32(data),
            "F16" => ConvertF16(data),
            "BF16" => ConvertBF16(data),
            "F64" => ConvertF64(data),
            _ => throw new NotSupportedException($"Unsupported dtype: {dtype}")
        };
    }

    private static float[] ConvertF32(byte[] data)
    {
        var floats = new float[data.Length / 4];
        Buffer.BlockCopy(data, 0, floats, 0, data.Length);
        return floats;
    }

    private static float[] ConvertF16(byte[] data)
    {
        var floats = new float[data.Length / 2];
        for (int i = 0; i < floats.Length; i++)
        {
            var half = BitConverter.ToHalf(data, i * 2);
            floats[i] = (float)half;
        }
        return floats;
    }

    private static float[] ConvertBF16(byte[] data)
    {
        var floats = new float[data.Length / 2];
        for (int i = 0; i < floats.Length; i++)
        {
            // BF16: Same exponent as F32, truncated mantissa
            // Convert by left-shifting 16 bits
            ushort bf16 = BitConverter.ToUInt16(data, i * 2);
            uint f32Bits = (uint)bf16 << 16;
            floats[i] = BitConverter.UInt32BitsToSingle(f32Bits);
        }
        return floats;
    }

    private static float[] ConvertF64(byte[] data)
    {
        var floats = new float[data.Length / 8];
        for (int i = 0; i < floats.Length; i++)
        {
            var d = BitConverter.ToDouble(data, i * 8);
            floats[i] = (float)d;
        }
        return floats;
    }

    private static bool IsEmbeddingTensor(string name)
    {
        // Common patterns for embedding tensors
        return name.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("wte", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("token_embedding", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> CreateModelCompositionAsync(
        long[] tensorAtomIds,
        string modelName,
        SafeTensorMetadata metadata,
        CancellationToken ct)
    {
        var multiplicities = Enumerable.Repeat(1, tensorAtomIds.Length).ToArray();
        var contentHash = HartNative.ComputeCompositionHash(tensorAtomIds, multiplicities);

        var existing = await Context.Atoms
            .Where(a => a.ContentHash == contentHash && !a.IsConstant)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (existing != 0) return existing;

        // Load child atoms to build geometry
        var children = await Context.Atoms
            .Where(a => tensorAtomIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var coordinates = new List<CoordinateZM>();
        foreach (var atomId in tensorAtomIds)
        {
            if (children.TryGetValue(atomId, out var child))
            {
                coordinates.Add(ExtractCoordinate(child.Geom));
            }
        }

        var geom = coordinates.Count == 1
            ? (Geometry)GeometryFactory.CreatePoint(coordinates[0])
            : GeometryFactory.CreateLineString(coordinates.ToArray());

        var centroid = geom.Centroid.Coordinate;
        var hilbert = HartNative.point_to_hilbert(new HartNative.PointZM
        {
            X = centroid.X,
            Y = centroid.Y,
            Z = double.IsNaN(centroid.Z) ? 0 : centroid.Z,
            M = double.IsNaN(centroid.M) ? 0 : centroid.M
        });

        var modelMetadata = new
        {
            model_name = modelName,
            tensor_count = tensorAtomIds.Length,
            safetensor_metadata = metadata.Metadata
        };

        var atom = new Atom
        {
            HilbertHigh = hilbert.High,
            HilbertLow = hilbert.Low,
            Geom = geom,
            IsConstant = false,
            Refs = tensorAtomIds,
            Multiplicities = multiplicities,
            ContentHash = contentHash,
            AtomType = "ai_model",
            Metadata = JsonSerializer.Serialize(modelMetadata)
        };

        Context.Atoms.Add(atom);
        await Context.SaveChangesAsync(ct);

        return atom.Id;
    }

    /// <summary>
    /// Reduce high-dimensional embedding to 3D for PostGIS spatial queries.
    /// Simple projection using variance-weighted sum of dimensions.
    /// </summary>
    private static (double X, double Y, double Z) ReduceToSpatial3D(float[] embedding)
    {
        if (embedding.Length < 3)
        {
            return (
                embedding.Length > 0 ? embedding[0] : 0,
                embedding.Length > 1 ? embedding[1] : 0,
                embedding.Length > 2 ? embedding[2] : 0
            );
        }

        // Simple dimensionality reduction: 
        // X = weighted sum of first third of dimensions
        // Y = weighted sum of middle third
        // Z = weighted sum of last third
        var third = embedding.Length / 3;

        double x = 0, y = 0, z = 0;
        for (int i = 0; i < third; i++)
        {
            x += embedding[i];
            y += embedding[i + third];
            z += embedding[i + 2 * third];
        }

        // Normalize to reasonable spatial range
        var scale = Math.Sqrt(third);
        return (x / scale, y / scale, z / scale);
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

    private static Dictionary<string, int> ExtractVocabFromHuggingFace(JsonElement vocabElement)
    {
        var vocab = new Dictionary<string, int>();
        foreach (var prop in vocabElement.EnumerateObject())
        {
            vocab[prop.Name] = prop.Value.GetInt32();
        }
        return vocab;
    }

    private static Dictionary<string, int> ExtractSimpleVocab(JsonElement root)
    {
        var vocab = new Dictionary<string, int>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number)
            {
                vocab[prop.Name] = prop.Value.GetInt32();
            }
        }
        return vocab;
    }
}
