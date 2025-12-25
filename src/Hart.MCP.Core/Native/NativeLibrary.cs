using System.Runtime.InteropServices;

namespace Hart.MCP.Core.Native;

/// <summary>
/// P/Invoke wrapper for native C++ library (hartonomous_native.dll/.so)
/// Provides lossless, deterministic operations for seed projection and Hilbert curves
/// Now includes SIMD-accelerated operations for high-performance batch processing
/// </summary>
public static class NativeLibrary
{
    private const string LibName = "hartonomous_native";

    #region Structures

    /// <summary>
    /// 4D point on hypersphere surface (X² + Y² + Z² + M² = R²)
    /// 64-bit IEEE-754 double precision per coordinate
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PointZM
    {
        public double X;
        public double Y;
        public double Z;
        public double M;
    }

    /// <summary>
    /// 128-bit Hilbert curve index (space-filling curve)
    /// Preserves locality: nearby points in 4D space = nearby indices
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HilbertIndex
    {
        public long High;  // Upper 64 bits
        public long Low;   // Lower 64 bits
    }

    /// <summary>
    /// BLAKE3-256 hash output (32 bytes)
    /// Zero collision tolerance for content deduplication
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ContentHash
    {
        public fixed byte Data[32];
    }

    /// <summary>
    /// SIMD capabilities detected at runtime
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SIMDCapabilities
    {
        public int HasSse2;
        public int HasSse41;
        public int HasAvx;
        public int HasAvx2;
        public int HasAvx512F;
    }

    #endregion

    #region Core Native Methods

    /// <summary>
    /// Project Unicode codepoint to 4D hypersphere surface
    /// Deterministic, bijective mapping: same seed → same point
    /// Result satisfies X² + Y² + Z² + M² = 1.0 (unit hypersphere)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern PointZM project_seed_to_hypersphere(uint seed);

    /// <summary>
    /// Convert 4D point to Hilbert curve index
    /// Space-filling curve preserves locality for efficient spatial queries
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern HilbertIndex point_to_hilbert(PointZM point);

    /// <summary>
    /// Convert Hilbert index back to 4D point
    /// Inverse of point_to_hilbert (with quantization error < 0.01)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern PointZM hilbert_to_point(HilbertIndex hilbert);

    /// <summary>
    /// Compute BLAKE3 hash of composition (refs + multiplicities)
    /// Deterministic: same inputs → same hash
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe ContentHash compute_composition_hash(
        long* refs,
        int* multiplicities,
        int count
    );

    /// <summary>
    /// Compute BLAKE3 hash of constant (single seed)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ContentHash compute_seed_hash(uint seed);

    #endregion

    #region SIMD-Accelerated Methods

    /// <summary>
    /// Detect available SIMD capabilities at runtime
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SIMDCapabilities detect_simd_capabilities();

    /// <summary>
    /// Get human-readable SIMD capabilities string
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr simd_capabilities_string();

    /// <summary>
    /// Compute 4D Euclidean distance (SIMD-accelerated)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double distance_4d(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2);

    /// <summary>
    /// Compute squared 4D Euclidean distance (SIMD-accelerated, no sqrt)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double distance_4d_squared(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2);

    /// <summary>
    /// Batch compute distances using SIMD (AVX2/AVX-512)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void batch_distance_4d(
        double qx, double qy, double qz, double qm,
        double* xs, double* ys, double* zs, double* ms,
        double* distances,
        nuint count);

    /// <summary>
    /// Compute attention weights from distances (SIMD-accelerated softmax)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void compute_attention_weights(
        double* distances,
        double* weights,
        nuint count);

    /// <summary>
    /// Compute 4D dot product (SIMD-accelerated)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double vector_dot_4d(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2);

    /// <summary>
    /// Compute centroid of multiple 4D points (SIMD-accelerated)
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void compute_centroid_4d(
        double* xs, double* ys, double* zs, double* ms,
        nuint count,
        double* cx, double* cy, double* cz, double* cm);

    #endregion

    #region Safe Wrappers

    /// <summary>
    /// Safe wrapper for computing composition hash from managed arrays
    /// </summary>
    public static unsafe byte[] ComputeCompositionHash(long[] refs, int[] multiplicities)
    {
        if (refs == null || multiplicities == null)
            throw new ArgumentNullException();
        if (refs.Length != multiplicities.Length)
            throw new ArgumentException("Refs and multiplicities must have same length");

        fixed (long* pRefs = refs)
        fixed (int* pMult = multiplicities)
        {
            var hash = compute_composition_hash(pRefs, pMult, refs.Length);
            var result = new byte[32];
            for (int i = 0; i < 32; i++)
                result[i] = hash.Data[i];
            return result;
        }
    }

    /// <summary>
    /// Safe wrapper for computing seed hash
    /// </summary>
    public static unsafe byte[] ComputeSeedHash(uint seed)
    {
        var hash = compute_seed_hash(seed);
        var result = new byte[32];
        for (int i = 0; i < 32; i++)
            result[i] = hash.Data[i];
        return result;
    }

    /// <summary>
    /// Get SIMD capabilities as managed string
    /// </summary>
    public static string GetSIMDCapabilities()
    {
        var ptr = simd_capabilities_string();
        return Marshal.PtrToStringAnsi(ptr) ?? "Unknown";
    }

    /// <summary>
    /// Safe wrapper for batch distance computation
    /// </summary>
    public static unsafe void BatchComputeDistances(
        double qx, double qy, double qz, double qm,
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys,
        ReadOnlySpan<double> zs, ReadOnlySpan<double> ms,
        Span<double> distances)
    {
        int count = Math.Min(Math.Min(xs.Length, distances.Length), 
                            Math.Min(ys.Length, Math.Min(zs.Length, ms.Length)));
        
        fixed (double* pXs = xs)
        fixed (double* pYs = ys)
        fixed (double* pZs = zs)
        fixed (double* pMs = ms)
        fixed (double* pDist = distances)
        {
            batch_distance_4d(qx, qy, qz, qm, pXs, pYs, pZs, pMs, pDist, (nuint)count);
        }
    }

    /// <summary>
    /// Safe wrapper for attention weight computation
    /// </summary>
    public static unsafe void ComputeAttentionWeights(
        ReadOnlySpan<double> distances,
        Span<double> weights)
    {
        int count = Math.Min(distances.Length, weights.Length);
        
        fixed (double* pDist = distances)
        fixed (double* pWeights = weights)
        {
            compute_attention_weights(pDist, pWeights, (nuint)count);
        }
    }

    /// <summary>
    /// Safe wrapper for centroid computation
    /// </summary>
    public static unsafe (double X, double Y, double Z, double M) ComputeCentroid(
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys,
        ReadOnlySpan<double> zs, ReadOnlySpan<double> ms)
    {
        int count = Math.Min(Math.Min(xs.Length, ys.Length), Math.Min(zs.Length, ms.Length));
        double cx, cy, cz, cm;
        
        fixed (double* pXs = xs)
        fixed (double* pYs = ys)
        fixed (double* pZs = zs)
        fixed (double* pMs = ms)
        {
            compute_centroid_4d(pXs, pYs, pZs, pMs, (nuint)count, &cx, &cy, &cz, &cm);
        }
        
        return (cx, cy, cz, cm);
    }

    #endregion

    #region Bulk Ingestion Structures

    /// <summary>
    /// Result from SafeTensor ingestion
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SafeTensorResult
    {
        public long RootAtomId;
        public int TensorCount;
        public long TotalParameters;
        public long TotalValues;
        public long StoredValues;
        public long SkippedValues;
        public double SparsityPercent;
        public long ProcessingTimeMs;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string ErrorMessage;
    }

    /// <summary>
    /// Progress callback delegate for native ingestion
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate void IngestionProgressCallback(
        string phase,
        int tensorsProcessed,
        int tensorsTotal,
        long valuesProcessed,
        double sparsityPercent,
        IntPtr userData);

    #endregion

    #region Bulk Ingestion Methods

    /// <summary>
    /// Bulk ingest a SafeTensor file using native PostgreSQL COPY.
    /// This is the FAST path - bypasses EF Core entirely.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int hart_ingest_safetensor(
        IntPtr conn,
        string filepath,
        string modelName,
        float sparsityThreshold,
        float targetSparsityPercent,
        IngestionProgressCallback? progressCallback,
        IntPtr userData,
        out SafeTensorResult result);

    /// <summary>
    /// Bulk seed Unicode codepoints using native COPY.
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long hart_seed_unicode(
        IntPtr conn,
        uint startCodepoint,
        uint endCodepoint,
        IngestionProgressCallback? progressCallback,
        IntPtr userData);

    /// <summary>
    /// Connect to PostgreSQL database
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr hart_db_connect(string conninfo);

    /// <summary>
    /// Disconnect from PostgreSQL database
    /// </summary>
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void hart_db_disconnect(IntPtr conn);

    #endregion
}
