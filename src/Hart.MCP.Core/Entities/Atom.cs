using NetTopologySuite.Geometries;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Hart.MCP.Core.Entities;

/// <summary>
/// Atom entity - single table for ALL content (text, images, AI models, everything)
/// Constants are stored as 4D points on hypersphere surface (SRID 0)
/// Compositions are stored as LINESTRING/POLYGON/GEOMETRYCOLLECTION
/// </summary>
[Table("atom")]
public class Atom
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>
    /// Hilbert space-filling curve index (high 64 bits)
    /// Used for locality-preserving range queries
    /// </summary>
    [Required]
    [Column("hilbert_high")]
    public long HilbertHigh { get; set; }

    /// <summary>
    /// Hilbert space-filling curve index (low 64 bits)
    /// Combined with HilbertHigh forms 128-bit index
    /// </summary>
    [Required]
    [Column("hilbert_low")]
    public long HilbertLow { get; set; }

    /// <summary>
    /// 4D geometry on hypersphere (SRID 0)
    /// POINTZM for constants (single point)
    /// LINESTRING/POLYGON/etc for compositions (connected structure)
    /// </summary>
    [Required]
    [Column("geom", TypeName = "geometry(GeometryZM, 0)")]
    public Geometry Geom { get; set; } = null!;

    /// <summary>
    /// Type discriminator: true for constants, false for compositions
    /// </summary>
    [Required]
    [Column("is_constant")]
    public bool IsConstant { get; set; }

    /// <summary>
    /// For constants: the original seed value (Unicode codepoint, integer, etc.)
    /// Stored for lossless reconstruction. NULL for compositions.
    /// </summary>
    [Column("seed_value")]
    public long? SeedValue { get; set; }

    /// <summary>
    /// For constants: the seed type discriminator
    /// 0 = Unicode codepoint, 1 = Integer, 2 = Float bits
    /// </summary>
    [Column("seed_type")]
    public int? SeedType { get; set; }

    /// <summary>
    /// For compositions: array of child atom IDs
    /// NULL for constants
    /// </summary>
    [Column("refs")]
    public long[]? Refs { get; set; }

    /// <summary>
    /// For compositions: parallel array with RLE counts or edge weights
    /// NULL for constants
    /// Length must match Refs array
    /// </summary>
    [Column("multiplicities")]
    public int[]? Multiplicities { get; set; }

    /// <summary>
    /// BLAKE3-256 content hash (32 bytes)
    /// Automatic deduplication: same content = same hash = same atom
    /// </summary>
    [Required]
    [Column("content_hash")]
    [MaxLength(32)]
    public byte[] ContentHash { get; set; } = null!;

    /// <summary>
    /// Reference to type atom (char, word, pattern, weight_edge, etc.)
    /// Types are themselves atoms - this enables type hierarchies and queries.
    /// NULL only for bootstrap type atoms that define themselves.
    /// </summary>
    [Column("type_ref")]
    public long? TypeRef { get; set; }

    /// <summary>
    /// Optional descriptors: atom IDs that describe this atom.
    /// Replaces JSONB metadata - all descriptors are atoms.
    /// Example: [name_atom_id, description_atom_id, config_atom_id]
    /// </summary>
    [Column("descriptors")]
    public long[]? Descriptors { get; set; }

    /// <summary>
    /// Creation timestamp
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
