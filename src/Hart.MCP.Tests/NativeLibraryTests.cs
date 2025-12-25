using FluentAssertions;
using Hart.MCP.Core.Native;
using Xunit;

namespace Hart.MCP.Tests;

/// <summary>
/// Tests for native library P/Invoke wrapper.
/// 
/// MATHEMATICAL INVARIANTS TESTED:
/// 1. Hypersphere constraint: x² + y² + z² + m² = 1.0 for ALL seeds
/// 2. Determinism: identical inputs ALWAYS produce identical outputs
/// 3. Uniqueness: different seeds produce distinguishable positions
/// 4. Hilbert bijection: coords → hilbert → coords preserves within quantization
/// 5. Hash collision resistance: different inputs → different hashes
/// 
/// BEHAVIORAL EXPECTATIONS:
/// - Every Unicode codepoint maps to a unique point on unit hypersphere
/// - Adjacent codepoints should be spatially close (locality)
/// - SIMD and scalar paths produce identical results
/// </summary>
public class NativeLibraryTests
{
    private const double HypersphereRadius = 1.0;
    private const double Tolerance = 1e-10;

    #region Core Mathematical Invariant: Hypersphere Constraint

    /// <summary>
    /// INVARIANT: Every seed MUST project to a point exactly on the unit hypersphere.
    /// This is fundamental to the spatial knowledge substrate - all atoms exist on S³.
    /// </summary>
    [Fact]
    public void HypersphereConstraint_AllPrintableAscii_ExactlyOnUnitSphere()
    {
        for (uint seed = 32; seed < 127; seed++)
        {
            var point = NativeLibrary.project_seed_to_hypersphere(seed);
            var radiusSquared = point.X * point.X + point.Y * point.Y + 
                                point.Z * point.Z + point.M * point.M;
            
            radiusSquared.Should().BeApproximately(
                HypersphereRadius * HypersphereRadius, 
                Tolerance,
                because: $"codepoint U+{seed:X4} ('{(char)seed}') must lie on unit hypersphere (r²=1)");
        }
    }

    [Theory]
    [InlineData(0x00E9, "Latin-1 (é)")]
    [InlineData(0x0100, "Latin Extended-A (Ā)")]
    [InlineData(0x0370, "Greek (Ͱ)")]
    [InlineData(0x0400, "Cyrillic (Ѐ)")]
    [InlineData(0x4E00, "CJK (一)")]
    [InlineData(0x1F600, "Emoji (😀)")]
    [InlineData(0x10FFFF, "Max Unicode")]
    public void HypersphereConstraint_UnicodeRanges_ExactlyOnUnitSphere(uint seed, string description)
    {
        var point = NativeLibrary.project_seed_to_hypersphere(seed);
        var radiusSquared = point.X * point.X + point.Y * point.Y + 
                            point.Z * point.Z + point.M * point.M;
        
        radiusSquared.Should().BeApproximately(
            HypersphereRadius * HypersphereRadius, 
            Tolerance,
            because: $"{description} (U+{seed:X4}) must lie on unit hypersphere");
    }

    [Theory]
    [InlineData(0u, "NULL character")]
    [InlineData(0x7Fu, "DEL")]
    [InlineData(0xFFFEu, "BOM")]
    [InlineData(0xFFFFu, "Invalid codepoint")]
    public void HypersphereConstraint_EdgeCases_StillOnUnitSphere(uint seed, string description)
    {
        var point = NativeLibrary.project_seed_to_hypersphere(seed);
        var radiusSquared = point.X * point.X + point.Y * point.Y + 
                            point.Z * point.Z + point.M * point.M;
        
        radiusSquared.Should().BeApproximately(
            HypersphereRadius * HypersphereRadius, 
            Tolerance,
            because: $"edge case {description} (U+{seed:X4}) must still lie on unit hypersphere");
    }

    #endregion

    #region Core Mathematical Invariant: Determinism

    /// <summary>
    /// INVARIANT: Same input MUST ALWAYS produce byte-identical output.
    /// This is required for content-addressing to work correctly.
    /// </summary>
    [Fact]
    public void Determinism_SameSeed_ByteIdenticalCoordinates()
    {
        const uint seed = 'A';
        
        var point1 = NativeLibrary.project_seed_to_hypersphere(seed);
        var point2 = NativeLibrary.project_seed_to_hypersphere(seed);
        var point3 = NativeLibrary.project_seed_to_hypersphere(seed);
        
        // Byte-identical means exact equality, not approximate
        point1.X.Should().Be(point2.X, because: "deterministic projection requires exact equality");
        point1.Y.Should().Be(point2.Y);
        point1.Z.Should().Be(point2.Z);
        point1.M.Should().Be(point2.M);
        
        point2.X.Should().Be(point3.X);
        point2.Y.Should().Be(point3.Y);
        point2.Z.Should().Be(point3.Z);
        point2.M.Should().Be(point3.M);
    }

    [Fact]
    public void Determinism_SamePoint_IdenticalHilbertIndex()
    {
        var point = new NativeLibrary.PointZM { X = 0.5, Y = 0.5, Z = 0.5, M = 0.5 };
        
        var hilbert1 = NativeLibrary.point_to_hilbert(point);
        var hilbert2 = NativeLibrary.point_to_hilbert(point);
        
        hilbert1.High.Should().Be(hilbert2.High, because: "Hilbert index must be deterministic");
        hilbert1.Low.Should().Be(hilbert2.Low);
    }

    [Fact]
    public void Determinism_SameSeed_IdenticalHash()
    {
        const uint seed = 'A';
        
        var hash1 = NativeLibrary.ComputeSeedHash(seed);
        var hash2 = NativeLibrary.ComputeSeedHash(seed);
        var hash3 = NativeLibrary.ComputeSeedHash(seed);
        
        hash1.Should().Equal(hash2, because: "BLAKE3 must be deterministic");
        hash2.Should().Equal(hash3);
    }

    #endregion

    #region Core Mathematical Invariant: Uniqueness

    /// <summary>
    /// INVARIANT: Different seeds MUST produce different spatial positions.
    /// This ensures atoms are distinguishable in the knowledge substrate.
    /// </summary>
    [Fact]
    public void Uniqueness_AdjacentCodepoints_ProduceDifferentPoints()
    {
        var pointA = NativeLibrary.project_seed_to_hypersphere('A');
        var pointB = NativeLibrary.project_seed_to_hypersphere('B');
        
        var distance = Math.Sqrt(
            Math.Pow(pointA.X - pointB.X, 2) +
            Math.Pow(pointA.Y - pointB.Y, 2) +
            Math.Pow(pointA.Z - pointB.Z, 2) +
            Math.Pow(pointA.M - pointB.M, 2));
        
        distance.Should().BeGreaterThan(1e-10, 
            because: "different codepoints must map to different positions for disambiguation");
    }

    [Fact]
    public void Uniqueness_AllPrintableAscii_NoDuplicatePositions()
    {
        var positions = new List<(double X, double Y, double Z, double M)>();
        
        for (uint seed = 32; seed < 127; seed++)
        {
            var point = NativeLibrary.project_seed_to_hypersphere(seed);
            var position = (point.X, point.Y, point.Z, point.M);
            
            // Check this position doesn't match any previous
            foreach (var existing in positions)
            {
                var distance = Math.Sqrt(
                    Math.Pow(position.X - existing.X, 2) +
                    Math.Pow(position.Y - existing.Y, 2) +
                    Math.Pow(position.Z - existing.Z, 2) +
                    Math.Pow(position.M - existing.M, 2));
                
                distance.Should().BeGreaterThan(1e-10,
                    because: "all ASCII characters must have unique positions");
            }
            
            positions.Add(position);
        }
    }

    [Fact]
    public void Uniqueness_DifferentSeeds_DifferentHilbertIndices()
    {
        var pointA = NativeLibrary.project_seed_to_hypersphere('A');
        var pointB = NativeLibrary.project_seed_to_hypersphere('B');
        
        var hilbertA = NativeLibrary.point_to_hilbert(pointA);
        var hilbertB = NativeLibrary.point_to_hilbert(pointB);
        
        var sameIndex = hilbertA.High == hilbertB.High && hilbertA.Low == hilbertB.Low;
        sameIndex.Should().BeFalse(because: "different positions must have different Hilbert indices");
    }

    [Fact]
    public void Uniqueness_DifferentSeeds_DifferentHashes()
    {
        var hashA = NativeLibrary.ComputeSeedHash('A');
        var hashB = NativeLibrary.ComputeSeedHash('B');
        
        hashA.Should().NotEqual(hashB, because: "BLAKE3 is collision-resistant");
    }

    #endregion

    #region Hilbert Curve: Bijection Property

    /// <summary>
    /// INVARIANT: coords → hilbert → coords must preserve within quantization bounds.
    /// The Hilbert curve is a bijection - we should be able to roundtrip.
    /// </summary>
    [Fact]
    public void HilbertBijection_Roundtrip_PreservesWithinQuantization()
    {
        // Max quantization error for 16 bits per dimension = 2 / 65535 ≈ 3e-5
        const double maxQuantizationError = 2.0 / 65535.0;
        
        var originalPoint = NativeLibrary.project_seed_to_hypersphere('A');
        var hilbert = NativeLibrary.point_to_hilbert(originalPoint);
        var reconstructedPoint = NativeLibrary.hilbert_to_point(hilbert);
        
        Math.Abs(originalPoint.X - reconstructedPoint.X).Should().BeLessThan(maxQuantizationError,
            because: "X coordinate must survive Hilbert roundtrip within quantization bounds");
        Math.Abs(originalPoint.Y - reconstructedPoint.Y).Should().BeLessThan(maxQuantizationError);
        Math.Abs(originalPoint.Z - reconstructedPoint.Z).Should().BeLessThan(maxQuantizationError);
        Math.Abs(originalPoint.M - reconstructedPoint.M).Should().BeLessThan(maxQuantizationError);
    }

    [Fact]
    public void HilbertBijection_MultipleSeeds_AllRoundtripCorrectly()
    {
        const double maxQuantizationError = 2.0 / 65535.0;
        var testSeeds = new uint[] { 'A', 'Z', '0', '9', 0x4E00, 0x1F600 };
        
        foreach (var seed in testSeeds)
        {
            var original = NativeLibrary.project_seed_to_hypersphere(seed);
            var hilbert = NativeLibrary.point_to_hilbert(original);
            var reconstructed = NativeLibrary.hilbert_to_point(hilbert);
            
            var maxError = Math.Max(
                Math.Max(Math.Abs(original.X - reconstructed.X), Math.Abs(original.Y - reconstructed.Y)),
                Math.Max(Math.Abs(original.Z - reconstructed.Z), Math.Abs(original.M - reconstructed.M)));
            
            maxError.Should().BeLessThan(maxQuantizationError,
                because: $"seed U+{seed:X4} must roundtrip within quantization bounds");
        }
    }

    #endregion

    #region Hash: BLAKE3-256 Properties

    [Fact]
    public void Hash_SeedHash_Returns32Bytes()
    {
        var hash = NativeLibrary.ComputeSeedHash('A');
        
        hash.Should().HaveCount(32, because: "BLAKE3-256 always produces 32-byte hashes");
    }

    [Fact]
    public void Hash_CompositionOrder_AffectsHash()
    {
        var refs = new long[] { 1, 2 };
        var multiplicities = new int[] { 1, 1 };
        var refsReversed = new long[] { 2, 1 };
        
        var hash1 = NativeLibrary.ComputeCompositionHash(refs, multiplicities);
        var hash2 = NativeLibrary.ComputeCompositionHash(refsReversed, multiplicities);
        
        hash1.Should().NotEqual(hash2, 
            because: "order of children affects hash (compositions are ordered)");
    }

    [Fact]
    public void Hash_Multiplicity_AffectsHash()
    {
        var refs = new long[] { 1 };
        var mult1 = new int[] { 1 };
        var mult2 = new int[] { 2 };
        
        var hash1 = NativeLibrary.ComputeCompositionHash(refs, mult1);
        var hash2 = NativeLibrary.ComputeCompositionHash(refs, mult2);
        
        hash1.Should().NotEqual(hash2, 
            because: "multiplicity is part of the content identity");
    }

    #endregion

    #region SIMD Capability Tests

    [Fact]
    public void SIMD_CapabilitiesDetection_ReturnsValidStruct()
    {
        var caps = NativeLibrary.detect_simd_capabilities();
        
        // At minimum, SSE2 should be available on any x64 CPU
        // (SSE2 is required for x64)
        caps.HasSse2.Should().Be(1,
            because: "SSE2 is mandatory for x64 architecture");
    }

    [Fact]
    public void SIMD_BatchDistanceComputation_MatchesScalarResults()
    {
        // Prepare test data
        const int count = 10;
        var xs = new double[count];
        var ys = new double[count];
        var zs = new double[count];
        var ms = new double[count];
        var simdDistances = new double[count];
        
        for (int i = 0; i < count; i++)
        {
            var point = NativeLibrary.project_seed_to_hypersphere((uint)('A' + i));
            xs[i] = point.X;
            ys[i] = point.Y;
            zs[i] = point.Z;
            ms[i] = point.M;
        }
        
        var queryPoint = NativeLibrary.project_seed_to_hypersphere('Q');
        
        // Compute batch distances using SIMD
        NativeLibrary.BatchComputeDistances(
            queryPoint.X, queryPoint.Y, queryPoint.Z, queryPoint.M,
            xs, ys, zs, ms, simdDistances);
        
        // Compute scalar distances for comparison
        for (int i = 0; i < count; i++)
        {
            var scalarDistance = Math.Sqrt(
                Math.Pow(queryPoint.X - xs[i], 2) +
                Math.Pow(queryPoint.Y - ys[i], 2) +
                Math.Pow(queryPoint.Z - zs[i], 2) +
                Math.Pow(queryPoint.M - ms[i], 2));
            
            simdDistances[i].Should().BeApproximately(scalarDistance, 1e-10,
                because: "SIMD and scalar paths must produce identical results");
        }
    }

    [Fact]
    public void SIMD_AttentionWeights_SumToOne()
    {
        // Prepare distances
        var distances = new double[] { 0.1, 0.2, 0.5, 1.0, 2.0 };
        var weights = new double[distances.Length];
        
        NativeLibrary.ComputeAttentionWeights(distances, weights);
        
        var sum = weights.Sum();
        sum.Should().BeApproximately(1.0, 1e-10,
            because: "attention weights must normalize to 1.0 (softmax property)");
    }

    [Fact]
    public void SIMD_AttentionWeights_CloserDistanceGetsHigherWeight()
    {
        var distances = new double[] { 0.1, 1.0 };
        var weights = new double[distances.Length];
        
        NativeLibrary.ComputeAttentionWeights(distances, weights);
        
        weights[0].Should().BeGreaterThan(weights[1],
            because: "closer atoms (smaller distance) should receive higher attention weight");
    }

    [Fact]
    public void SIMD_CentroidComputation_ReturnsAveragePosition()
    {
        // Test with simple points where centroid is predictable
        var xs = new double[] { 0.0, 1.0, 0.0, 1.0 };
        var ys = new double[] { 0.0, 0.0, 1.0, 1.0 };
        var zs = new double[] { 0.5, 0.5, 0.5, 0.5 };
        var ms = new double[] { 0.0, 0.0, 0.0, 0.0 };
        
        var centroid = NativeLibrary.ComputeCentroid(xs, ys, zs, ms);
        
        centroid.X.Should().BeApproximately(0.5, 1e-10, because: "centroid X is average of X coords");
        centroid.Y.Should().BeApproximately(0.5, 1e-10, because: "centroid Y is average of Y coords");
        centroid.Z.Should().BeApproximately(0.5, 1e-10, because: "centroid Z is average of Z coords");
        centroid.M.Should().BeApproximately(0.0, 1e-10, because: "centroid M is average of M coords");
    }

    #endregion
}
