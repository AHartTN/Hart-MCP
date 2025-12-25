using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Entities;

/// <summary>
/// A composition - a node that references other nodes (constants or compositions).
/// Compositions are content-addressed by their ordered child references.
/// Examples: strings (sequence of codepoints), tensors (sequence of floats), documents.
/// </summary>
public class Composition
{
    public long Id { get; set; }

    /// <summary>
    /// BLAKE3-256 content hash for deduplication.
    /// Computed from ordered child references and multiplicities.
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
    /// 4D hypersphere geometry for spatial queries.
    /// Typically centroid of child geometries.
    /// </summary>
    public Geometry? Geom { get; set; }

    /// <summary>
    /// Optional reference to a type composition (for typed compositions).
    /// </summary>
    public long? TypeId { get; set; }
    public virtual Composition? Type { get; set; }

    // Navigation properties
    public virtual ICollection<Relation> OutgoingRelations { get; set; } = new List<Relation>();
    public virtual ICollection<Relation> IncomingRelations { get; set; } = new List<Relation>();
}
