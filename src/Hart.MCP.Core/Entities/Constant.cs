using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Entities;

/// <summary>
/// A constant atom - an irreducible value with no children.
/// Constants are content-addressed by their SeedValue and SeedType.
/// Examples: Unicode codepoints, bytes, float bit patterns, integers.
/// </summary>
public class Constant
{
    public long Id { get; set; }

    /// <summary>
    /// 64-bit seed value (codepoint, byte, float bits, etc.)
    /// </summary>
    public long SeedValue { get; set; }

    /// <summary>
    /// Type discriminator for seed interpretation.
    /// 1 = Unicode codepoint, 2 = byte, 3 = float32 bits, 4 = integer, etc.
    /// </summary>
    public int SeedType { get; set; }

    /// <summary>
    /// BLAKE3-256 content hash for deduplication.
    /// Computed from SeedValue + SeedType.
    /// </summary>
    public byte[] ContentHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// High 64 bits of 128-bit Hilbert curve index.
    /// </summary>
    public ulong HilbertHigh { get; set; }

    /// <summary>
    /// Low 64 bits of 128-bit Hilbert curve index.
    /// </summary>
    public ulong HilbertLow { get; set; }

    /// <summary>
    /// 4D hypersphere geometry (POINTZM) for spatial queries.
    /// Coordinates derived deterministically from SeedValue.
    /// </summary>
    public Geometry? Geom { get; set; }

    // Navigation properties
    public virtual ICollection<Relation> IncomingRelations { get; set; } = new List<Relation>();
}
