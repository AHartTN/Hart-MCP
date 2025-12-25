using System.ComponentModel.DataAnnotations;

namespace Hart.MCP.Shared.DTOs;

// ==============
// Atom DTOs
// ==============

/// <summary>
/// Summary view of an atom
/// </summary>
public record AtomDto(
    long Id,
    long HilbertHigh,
    long HilbertLow,
    bool IsConstant,
    string? AtomType,
    long? SeedValue,
    int? SeedType,
    int? RefCount,
    string ContentHashHex,
    DateTime CreatedAt
);

/// <summary>
/// Full atom details including geometry info
/// </summary>
public record AtomDetailDto(
    long Id,
    long HilbertHigh,
    long HilbertLow,
    bool IsConstant,
    string? AtomType,
    long? SeedValue,
    int? SeedType,
    long[]? Refs,
    int[]? Multiplicities,
    string ContentHashHex,
    string GeometryType,
    GeometryInfoDto? Geometry,
    DateTime CreatedAt
);

/// <summary>
/// Geometry information
/// </summary>
public record GeometryInfoDto(
    string Type,
    double CentroidX,
    double CentroidY,
    double CentroidZ,
    double CentroidM,
    double? Length,
    int? NumPoints
);

// ==============
// Ingestion DTOs
// ==============

/// <summary>
/// Request to ingest text content
/// </summary>
public record IngestTextRequestDto(
    [Required]
    [MinLength(1)]
    [MaxLength(10_000_000)]
    string Text
);

/// <summary>
/// Response from text ingestion
/// </summary>
public record IngestTextResponseDto(
    long RootAtomId,
    int CharacterCount,
    int UniqueCharacterCount,
    string Status
);

/// <summary>
/// Response from text reconstruction
/// </summary>
public record ReconstructTextResponseDto(
    long AtomId,
    string Text,
    int CharacterCount,
    string Status
);

// ==============
// Query DTOs
// ==============

/// <summary>
/// Request for spatial query within a radius
/// </summary>
public record SpatialQueryRequestDto(
    double CenterX,
    double CenterY,
    [Range(-1.0, 1.0)] double Radius = 0.1,
    double? MinZ = null,
    double? MaxZ = null,
    string? AtomType = null,
    [Range(1, 10000)] int MaxResults = 100
);

/// <summary>
/// Request for Hilbert range query
/// </summary>
public record HilbertRangeQueryDto(
    long StartHigh,
    long StartLow,
    long EndHigh,
    long EndLow,
    [Range(1, 10000)] int MaxResults = 100
);

/// <summary>
/// Request for nearest neighbor query
/// </summary>
public record NearestNeighborQueryDto(
    uint Seed,
    [Range(1, 1000)] int Limit = 10
);

/// <summary>
/// Generic query result wrapper
/// </summary>
public record QueryResultDto<T>(
    List<T> Results,
    int TotalCount,
    double QueryTimeMs
);

// ==============
// Statistics DTOs
// ==============

/// <summary>
/// Atom count statistics
/// </summary>
public record AtomCountsDto(
    int TotalConstants,
    int TotalCompositions,
    int Total
);

/// <summary>
/// Composition statistics
/// </summary>
public record CompositionStatsDto(
    long Id,
    int RefCount,
    int UniqueRefs,
    int TotalMultiplicity,
    string GeometryType,
    double GeometryLength,
    GeometryInfoDto Centroid,
    int ConstantsReferenced,
    int CompositionsReferenced
);

/// <summary>
/// Difference between two compositions
/// </summary>
public record CompositionDiffDto(
    long CompositionA,
    long CompositionB,
    int OnlyInACount,
    int OnlyInBCount,
    int SharedCount,
    List<long> OnlyInA,
    List<long> OnlyInB,
    List<long> Shared
);

// ==============
// Error DTOs
// ==============

/// <summary>
/// Standard error response
/// </summary>
public record ErrorDto(
    string Error,
    string[]? Details = null,
    string? TraceId = null
);

// ==============
// Legacy DTOs (updated for atom-based architecture)
// ==============

public record SpatialNodeDto(
    long Id,
    double X,
    double Y,
    double Z,
    double M,
    string? NodeType,
    string MerkleHash,
    string? ParentHash,
    string? Metadata,
    DateTime CreatedAt
);

public record CreateSpatialNodeRequest(
    double X,
    double Y,
    double Z,
    double M,
    string NodeType,
    byte[]? Data,
    string? ParentHash,
    string? Metadata
);

public record SpatialQueryRequest(
    double CenterX,
    double CenterY,
    double Radius,
    double? MinZ = null,
    double? MaxZ = null,
    string? NodeType = null,
    int MaxResults = 100
);

public record SpatialRelationDto(
    Guid Id,
    Guid FromNodeId,
    Guid ToNodeId,
    string RelationType,
    double Strength,
    double Distance
);
