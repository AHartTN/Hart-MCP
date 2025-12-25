namespace Hart.MCP.Core.Services.Ingestion;

/// <summary>
/// SafeTensor file metadata - parsed from JSON header
/// </summary>
public class SafeTensorMetadata
{
    /// <summary>
    /// Tensor name → tensor info mapping from header
    /// </summary>
    public Dictionary<string, TensorInfo> Tensors { get; set; } = new();

    /// <summary>
    /// Model metadata from __metadata__ section
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Individual tensor information from SafeTensor header
/// </summary>
public class TensorInfo
{
    /// <summary>
    /// Tensor name (e.g., "model.layers.0.attention.wq.weight")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Data type: F16, BF16, F32, I32, etc.
    /// </summary>
    public string DType { get; set; } = string.Empty;

    /// <summary>
    /// Shape array (e.g., [4096, 4096] for weight matrix)
    /// </summary>
    public long[] Shape { get; set; } = Array.Empty<long>();

    /// <summary>
    /// Byte offset in data section
    /// </summary>
    public long DataOffset { get; set; }

    /// <summary>
    /// Byte length of tensor data
    /// </summary>
    public long DataLength { get; set; }

    /// <summary>
    /// Computed: total elements = product of shape
    /// </summary>
    public long TotalElements => Shape.Length > 0 ? Shape.Aggregate(1L, (a, b) => a * b) : 0;
}

/// <summary>
/// Vocabulary entry from tokenizer
/// </summary>
public class VocabularyEntry
{
    /// <summary>
    /// Token ID (0-indexed)
    /// </summary>
    public int TokenId { get; set; }

    /// <summary>
    /// Token string (can be byte fallback like <0x0A>)
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Score/priority for BPE merging (if available)
    /// </summary>
    public float? Score { get; set; }

    /// <summary>
    /// Token type: normal, byte_fallback, control, user_defined, etc.
    /// </summary>
    public string TokenType { get; set; } = "normal";
}

/// <summary>
/// Result of model ingestion
/// </summary>
public class ModelIngestionResult
{
    /// <summary>
    /// Root atom ID representing the entire model
    /// </summary>
    public long RootAtomId { get; set; }

    /// <summary>
    /// Atom ID for vocabulary composition
    /// </summary>
    public long VocabularyAtomId { get; set; }

    /// <summary>
    /// Atom IDs for each embedding tensor
    /// </summary>
    public Dictionary<string, long> EmbeddingAtomIds { get; set; } = new();

    /// <summary>
    /// Atom IDs for each weight tensor
    /// </summary>
    public Dictionary<string, long> WeightAtomIds { get; set; } = new();

    /// <summary>
    /// Number of vocabulary tokens ingested
    /// </summary>
    public int VocabularySize { get; set; }

    /// <summary>
    /// Number of tensors ingested
    /// </summary>
    public int TensorCount { get; set; }

    /// <summary>
    /// Total parameters (float count)
    /// </summary>
    public long TotalParameters { get; set; }

    /// <summary>
    /// Processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// Model name/identifier
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
}

/// <summary>
/// Attention weight trajectory - from token A to token B with weight
/// </summary>
public class AttentionTrajectory
{
    /// <summary>
    /// Atom ID for this trajectory
    /// </summary>
    public long AtomId { get; set; }

    /// <summary>
    /// Source token position
    /// </summary>
    public int FromPosition { get; set; }

    /// <summary>
    /// Target token position
    /// </summary>
    public int ToPosition { get; set; }

    /// <summary>
    /// Layer index
    /// </summary>
    public int Layer { get; set; }

    /// <summary>
    /// Attention head index
    /// </summary>
    public int Head { get; set; }

    /// <summary>
    /// Attention weight value
    /// </summary>
    public float Weight { get; set; }
}

/// <summary>
/// Embedding with both full precision and reduced spatial coordinates
/// </summary>
public class SpatialEmbedding
{
    /// <summary>
    /// Token this embedding belongs to
    /// </summary>
    public int TokenId { get; set; }

    /// <summary>
    /// Atom ID for full-precision embedding composition
    /// </summary>
    public long FullEmbeddingAtomId { get; set; }

    /// <summary>
    /// Reduced 3D coordinates for PostGIS spatial queries (PCA/UMAP)
    /// </summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

/// <summary>
/// GGUF quantization type
/// </summary>
public enum GgufQuantType
{
    F32 = 0,
    F16 = 1,
    Q4_0 = 2,
    Q4_1 = 3,
    Q5_0 = 6,
    Q5_1 = 7,
    Q8_0 = 8,
    Q8_1 = 9,
    Q2_K = 10,
    Q3_K = 11,
    Q4_K = 12,
    Q5_K = 13,
    Q6_K = 14,
    Q8_K = 15,
    IQ2_XXS = 16,
    IQ2_XS = 17,
    IQ3_XXS = 18,
    IQ1_S = 19,
    IQ4_NL = 20,
    IQ3_S = 21,
    IQ2_S = 22,
    IQ4_XS = 23
}
