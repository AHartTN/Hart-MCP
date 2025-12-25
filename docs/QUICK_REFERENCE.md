# Hartonomous Quick Reference

## Core Types

| Type | Geometry | Meaning |
|------|----------|---------|
| Constant | PointZM | Character, number, or single-char token |
| Composition | LineString | Sequence/trajectory (text, embeddings, audio) |
| Composition | Polygon | Region/boundary (concept clusters via ST_ConvexHull) |
| Composition | GeometryCollection | Mixed structures |
| Edge | NULL geom | Connection [A,B] with multiplicity (geometry computed on demand) |

## Atom Table

```sql
atom (
    id BIGSERIAL PRIMARY KEY,
    hilbert_high BIGINT,
    hilbert_low BIGINT,
    geom GEOMETRY,
    is_constant BOOLEAN,
    refs BIGINT[],
    multiplicities INT[],
    content_hash BYTEA UNIQUE
)
```

## Hypersphere Constraint

```
X² + Y² + Z² + M² = R²
```

## Landmark Projection

- Segment by category (letter/number/punct/etc)
- Fibonacci spiral within segment: θ = 2π × i × φ⁻¹
- Related chars cluster (A near a, è near e)
- φ = (1 + √5) / 2 ≈ 1.618

## Embedding = Trajectory

**An embedding IS a LineString** (not "stored as")

```
N-dim embedding = N-point LineString on hypersphere
768 floats → 768 points → trajectory through 4D

Point i coordinates (satisfies X² + Y² + Z² + M² = R²):
  θ = 2π × (i / N)           // Angular position
  φ = π × sigmoid(value)     // Value → polar angle
  X = R × sin(φ) × cos(θ)
  Y = R × sin(φ) × sin(θ)
  Z = R × cos(φ)
  M = encodes sign
```

**Hilbert index**: Use `ST_Centroid(geom)` → Hilbert on centroid

**Similarity = trajectory comparison:**
- `ST_FrechetDistance` - path similarity
- `ST_HausdorffDistance` - shape similarity

## Weight Ingestion

**Weights = connection counts, not value atoms**

- Threshold: keep normalized abs(weight) >= 0.5
- Store as: refs = [A, B] with multiplicity (no stored geometry)
- Geometry computed on demand: `ST_MakeLine(a.geom, b.geom)`
- Multiplicity = quantized weight magnitude
- Negative weights: encoded in spatial distance (far = repulsion)
- Query strength: `SUM(multiplicities[2])` or `COUNT(*)`

## Hilbert Index

- Constants: 4D coords → 1D index (preserves locality)
- Compositions: `ST_Centroid(geom)` → 1D index
- Split into hilbert_high, hilbert_low (2x BIGINT)
- Enables B-tree range queries on 4D space

## Content Hash

Algorithm: BLAKE3-256 (32 bytes). Zero collision tolerance.

- Constants: hash(X, Y, Z, M)
- Compositions: hash(child_hashes || multiplicities)
- Dedup: ON CONFLICT (content_hash) DO NOTHING

## Spatial = Semantic

**Spatial operations ARE semantic operations:**

| PostGIS Function | Semantic Meaning |
|-----------------|------------------|
| `ST_Distance(A, B)` | Similarity - closer = more similar |
| `ST_FrechetDistance(A, B)` | Trajectory similarity (for LineStrings/embeddings) |
| `ST_HausdorffDistance(A, B)` | Maximum shape deviation |
| `ST_Intersects(A, B)` | Relatedness - shared semantic space |
| `ST_Within(A, B)` | Containment - A is part of B's concept |
| `ST_DWithin(A, B, r)` | Neighborhood - k-NN search |
| `ST_ConvexHull(COLLECT)` | Concept boundary - semantic envelope |
| `ST_Centroid(A)` | Representative point - "average meaning" |

**Query Examples:**
```sql
-- k-NN: Find 10 most similar atoms
SELECT id FROM atom WHERE id != :target 
ORDER BY geom <-> (SELECT geom FROM atom WHERE id = :target)
LIMIT 10;

-- Trajectory match: Similar embedding shapes
SELECT id, ST_FrechetDistance(geom, :query_geom) AS dist
FROM atom WHERE NOT is_constant
ORDER BY dist LIMIT 10;
```

## Tech Stack

- PostgreSQL + PostGIS: storage
- C++: projection, hilbert, ingestion (heavy lift)
- C#: orchestration, API

## NOT Using - CRITICAL

- pg_vector - NO
- Graph databases - NO
- External embeddings - NO
- Third-party LLM APIs - NO
- External Hilbert libraries - NO (implement from scratch)
- SRID 4326 or any geographic projection - NO (abstract 4D space)
- Assumed dimensionality - NO (N depends on ingested model)

## Key Files

- `ARCHITECTURE.md` - Full concept explanation
- `TECHNICAL_SPEC.md` - Implementation details, pseudocode
