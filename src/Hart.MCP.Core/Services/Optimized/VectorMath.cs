using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Hart.MCP.Core.Services.Optimized;

/// <summary>
/// SIMD-accelerated vector math operations for 4D hypersphere computations.
/// Uses AVX2/AVX-512 when available, falls back to Vector&lt;T&gt; otherwise.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Check if hardware SIMD acceleration is available
    /// </summary>
    public static bool IsHardwareAccelerated => Vector.IsHardwareAccelerated;
    public static bool HasAvx2 => Avx2.IsSupported;
    public static bool HasAvx512 => Avx512F.IsSupported;

    /// <summary>
    /// Compute squared Euclidean distance between two 4D points using SIMD
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Distance4DSquared(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2)
    {
        if (Avx.IsSupported)
        {
            // Use AVX for 256-bit SIMD (4 doubles)
            var v1 = Vector256.Create(x1, y1, z1, m1);
            var v2 = Vector256.Create(x2, y2, z2, m2);
            var diff = Avx.Subtract(v1, v2);
            var sq = Avx.Multiply(diff, diff);
            
            // Horizontal sum
            var sum128 = Avx.ExtractVector128(sq, 0);
            var sum128High = Avx.ExtractVector128(sq, 1);
            var total = Sse2.Add(sum128, sum128High);
            var shuf = Sse2.Shuffle(total, total, 1);
            var result = Sse2.AddScalar(total, shuf);
            return result.ToScalar();
        }
        
        // Fallback to scalar
        double dx = x1 - x2;
        double dy = y1 - y2;
        double dz = z1 - z2;
        double dm = m1 - m2;
        return dx * dx + dy * dy + dz * dz + dm * dm;
    }

    /// <summary>
    /// Compute distance between two 4D points
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Distance4D(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2)
    {
        return Math.Sqrt(Distance4DSquared(x1, y1, z1, m1, x2, y2, z2, m2));
    }

    /// <summary>
    /// Batch compute distances from one point to many points using SIMD
    /// </summary>
    public static void ComputeDistancesBatch(
        double qx, double qy, double qz, double qm,
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, 
        ReadOnlySpan<double> zs, ReadOnlySpan<double> ms,
        Span<double> distances)
    {
        int count = Math.Min(xs.Length, distances.Length);
        
        if (Avx.IsSupported && count >= 4)
        {
            var query = Vector256.Create(qx, qy, qz, qm);
            int i = 0;
            
            // Process 4 points at a time using AVX
            // Pre-allocate buffer outside of loop
            Span<double> batchResults = stackalloc double[4];
            
            for (; i + 3 < count; i += 4)
            {
                for (int j = 0; j < 4; j++)
                {
                    var point = Vector256.Create(xs[i + j], ys[i + j], zs[i + j], ms[i + j]);
                    var diff = Avx.Subtract(query, point);
                    var sq = Avx.Multiply(diff, diff);
                    
                    // Horizontal sum for this point
                    var sum = sq.GetElement(0) + sq.GetElement(1) + sq.GetElement(2) + sq.GetElement(3);
                    batchResults[j] = Math.Sqrt(sum);
                }
                
                batchResults.CopyTo(distances.Slice(i, 4));
            }
            
            // Handle remaining
            for (; i < count; i++)
            {
                distances[i] = Distance4D(qx, qy, qz, qm, xs[i], ys[i], zs[i], ms[i]);
            }
        }
        else
        {
            // Scalar fallback
            for (int i = 0; i < count; i++)
            {
                distances[i] = Distance4D(qx, qy, qz, qm, xs[i], ys[i], zs[i], ms[i]);
            }
        }
    }

    /// <summary>
    /// Compute attention weights using SIMD (softmax over inverse distances)
    /// </summary>
    public static void ComputeAttentionWeights(
        ReadOnlySpan<double> distances,
        Span<double> weights)
    {
        int count = Math.Min(distances.Length, weights.Length);
        double sum = 0;
        
        // Compute raw weights (1 / (1 + distance))
        if (Vector.IsHardwareAccelerated && count >= Vector<double>.Count)
        {
            var ones = Vector<double>.One;
            int i = 0;
            var sumVec = Vector<double>.Zero;
            
            int vectorCount = count - (count % Vector<double>.Count);
            for (; i < vectorCount; i += Vector<double>.Count)
            {
                var dist = new Vector<double>(distances.Slice(i));
                var denom = Vector.Add(ones, dist);
                var weight = Vector.Divide(ones, denom);
                weight.CopyTo(weights.Slice(i));
                sumVec = Vector.Add(sumVec, weight);
            }
            
            // Sum vector elements
            for (int j = 0; j < Vector<double>.Count; j++)
                sum += sumVec[j];
            
            // Handle remaining
            for (; i < count; i++)
            {
                double w = 1.0 / (1.0 + distances[i]);
                weights[i] = w;
                sum += w;
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                double w = 1.0 / (1.0 + distances[i]);
                weights[i] = w;
                sum += w;
            }
        }
        
        // Normalize
        if (sum > 0)
        {
            if (Vector.IsHardwareAccelerated && count >= Vector<double>.Count)
            {
                var sumVec = new Vector<double>(sum);
                int i = 0;
                int vectorCount = count - (count % Vector<double>.Count);
                
                for (; i < vectorCount; i += Vector<double>.Count)
                {
                    var w = new Vector<double>(weights.Slice(i));
                    var normalized = Vector.Divide(w, sumVec);
                    normalized.CopyTo(weights.Slice(i));
                }
                
                for (; i < count; i++)
                    weights[i] /= sum;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    weights[i] /= sum;
            }
        }
    }

    /// <summary>
    /// Compute centroid of multiple 4D points using SIMD
    /// </summary>
    public static (double X, double Y, double Z, double M) ComputeCentroid(
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys,
        ReadOnlySpan<double> zs, ReadOnlySpan<double> ms)
    {
        int count = xs.Length;
        if (count == 0)
            return (0, 0, 0, 0);
        
        double sumX = 0, sumY = 0, sumZ = 0, sumM = 0;
        
        if (Vector.IsHardwareAccelerated && count >= Vector<double>.Count)
        {
            var xSum = Vector<double>.Zero;
            var ySum = Vector<double>.Zero;
            var zSum = Vector<double>.Zero;
            var mSum = Vector<double>.Zero;
            
            int i = 0;
            int vectorCount = count - (count % Vector<double>.Count);
            
            for (; i < vectorCount; i += Vector<double>.Count)
            {
                xSum = Vector.Add(xSum, new Vector<double>(xs.Slice(i)));
                ySum = Vector.Add(ySum, new Vector<double>(ys.Slice(i)));
                zSum = Vector.Add(zSum, new Vector<double>(zs.Slice(i)));
                mSum = Vector.Add(mSum, new Vector<double>(ms.Slice(i)));
            }
            
            for (int j = 0; j < Vector<double>.Count; j++)
            {
                sumX += xSum[j];
                sumY += ySum[j];
                sumZ += zSum[j];
                sumM += mSum[j];
            }
            
            for (; i < count; i++)
            {
                sumX += xs[i];
                sumY += ys[i];
                sumZ += zs[i];
                sumM += ms[i];
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                sumX += xs[i];
                sumY += ys[i];
                sumZ += zs[i];
                sumM += ms[i];
            }
        }
        
        return (sumX / count, sumY / count, sumZ / count, sumM / count);
    }

    /// <summary>
    /// Vector addition with SIMD
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double X, double Y, double Z, double M) Add4D(
        double x1, double y1, double z1, double m1,
        double x2, double y2, double z2, double m2)
    {
        if (Avx.IsSupported)
        {
            var v1 = Vector256.Create(x1, y1, z1, m1);
            var v2 = Vector256.Create(x2, y2, z2, m2);
            var result = Avx.Add(v1, v2);
            return (result.GetElement(0), result.GetElement(1), result.GetElement(2), result.GetElement(3));
        }
        
        return (x1 + x2, y1 + y2, z1 + z2, m1 + m2);
    }

    /// <summary>
    /// Scalar multiplication with SIMD
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double X, double Y, double Z, double M) Scale4D(
        double x, double y, double z, double m, double scalar)
    {
        if (Avx.IsSupported)
        {
            var v = Vector256.Create(x, y, z, m);
            var s = Vector256.Create(scalar);
            var result = Avx.Multiply(v, s);
            return (result.GetElement(0), result.GetElement(1), result.GetElement(2), result.GetElement(3));
        }
        
        return (x * scalar, y * scalar, z * scalar, m * scalar);
    }
}
