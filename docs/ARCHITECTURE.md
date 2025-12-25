# Hartonomous Architecture

## Constraints

- No external libraries for core algorithms
- No SRID - abstract 4D coordinate space
- No pg_vector
- No third-party LLM APIs
- No assumed dimensionality
- C++ / PostGIS / C# only

---

## Universal Substrate

**One table stores everything:**

| Content Type | Ingestion |
|--------------|-----------|
| Text | Character constants → word/sentence/paragraph compositions |
| Audio | Sample values as constants → waveform LineStrings |
| Image | Pixel values as constants → row/column/frame compositions |
| Video | Frame compositions → temporal sequence compositions |
| AI model vocabulary | Token compositions |
| AI model embeddings | Trajectories (N-point LineStrings) |
| AI model weights | Sparse edges [A,B] with multiplicity |
| Code | Character constants + AST compositions |
| PDF | Text + layout compositions |
| 3D mesh | Vertex constants → face compositions |
| Any future format | Constants + compositions |

Same queries work on all content. Same spatial operations. Same deduplication. The substrate is modality-agnostic.

**Deduplication = Connection**: "H" in "Hello" and "H" in "Hydrogen" are the same atom. Every shared atom is a semantic link across all content that contains it.

---

## Foundation

All digital content reduces to two primitives:

**Constant**: A point on the surface of a 4D hypersphere. Represents an indivisible unit (character, number, sample value). Stored as `PointZM`. Meaningless in isolation.

**Composition**: A relationship through the hypersphere. This is where meaning exists. Geometry types:
- **LineString**: Sequence/trajectory (text, embeddings, time series, audio waveforms)
- **Polygon**: Region/boundary (concept clusters, semantic neighborhoods via ST_ConvexHull)
- **GeometryCollection**: Mixed (documents with multiple trajectories, complex structures)

The hypersphere surface constraint:
```
X² + Y² + Z² + M² = R²
```

Every constant satisfies this. PostGIS `PointZM` provides the four coordinates directly.

---

## Landmark Projection

Maps any constant (character, number) to a deterministic position on the hypersphere.

**Segmentation**: The hypersphere surface is divided into regions by category:
- Letters (uppercase and lowercase clustered)
- Digits
- Punctuation
- Control/system characters
- Extended character sets

**Placement**: Within each segment, positions follow Fibonacci spiral distribution using the golden angle:
```
θ = 2π × i × φ⁻¹
φ = (1 + √5) / 2
```

**Clustering**: Related characters share proximity:
- A and a adjacent
- e, é, è, ê clustered
- Latin variants near base forms

The projection is pure mathematics. No learned embeddings. Given any input, the output coordinates are deterministic and reproducible.

---

## Hilbert Indexing

The 4D coordinates must be queryable via B-tree for performance. Hilbert curves preserve locality when mapping N-dimensional space to 1D.

**Process**:
1. Quantize float coordinates to integers (bit depth determines precision)
2. Compute 4D Hilbert index using Gray code traversal and dimension rotation
3. Split result into `hilbert_high` and `hilbert_low` (two 64-bit integers)

**Result**: Points near each other on the hypersphere have numerically close Hilbert indices. Range queries on the 1D index approximate spatial proximity queries.

Implementation from first principles. No library.

---

## Atom Table

```sql
CREATE TABLE atom (
    id BIGSERIAL PRIMARY KEY,
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,
    geom GEOMETRY NOT NULL,
    is_constant BOOLEAN NOT NULL,
    refs BIGINT[],
    multiplicities INT[],
    content_hash BYTEA UNIQUE NOT NULL
);

CREATE INDEX idx_atom_geom ON atom USING GIST (geom);
CREATE INDEX idx_atom_hilbert ON atom (hilbert_high, hilbert_low);
CREATE INDEX idx_atom_hash ON atom USING HASH (content_hash);
CREATE INDEX idx_atom_refs ON atom USING GIN (refs);
```

One table. All content types. Constants and compositions together.

`refs` contains atom IDs for compositions. `NULL` for constants.

`multiplicities` contains repeat counts (RLE) or edge weights. Parallel array with `refs`.

`content_hash` enables deduplication. Same content produces same hash produces same atom ID everywhere.

---

## Content Addressing

**Algorithm**: BLAKE3, 256-bit output (32 bytes). Zero collision tolerance.

**Constants**: Hash the four coordinates as raw bytes.
```
hash = BLAKE3(X_bytes || Y_bytes || Z_bytes || M_bytes)
```

**Compositions**: Hash ordered child hashes concatenated with multiplicities.
```
hash = BLAKE3(child_hash_1 || mult_1_bytes || child_hash_2 || mult_2_bytes || ...)
```

One collision = system corrupted. Merkle DAG integrity lost. Reconstruction impossible. 256-bit = 2^128 birthday bound = never happens.

Insert with `ON CONFLICT (content_hash) DO NOTHING`. Automatic deduplication. Same character everywhere in every ingested document points to one atom. Same weight value across models points to one atom.

---

## Text Ingestion

1. Iterate UTF-8 codepoints
2. For each codepoint:
   - Compute landmark projection → (X, Y, Z, M)
   - Compute Hilbert index → (high, low)
   - Compute content hash
   - Upsert atom, get ID
3. Build compositions:
   - Characters → N-grams (LineString through character atoms)
   - N-grams → Words
   - Words → Sentences
   - Sentences → Paragraphs
   - Paragraphs → Document

Each level is a composition referencing the level below. The document atom contains the full structure. Traverse refs to reconstruct original text.

Run-length encoding: "Hello" stores H, E, L, O as four constant atoms. The composition [H, E, L, L, O] references L twice. The L atom exists once.

---

## Embedding Ingestion

An N-dimensional embedding IS a trajectory. Not "stored as" a LineString - it IS one.

**768 floats → 768 points → LineString ZM**

Each dimension becomes a point ON the hypersphere surface. The trajectory IS the meaning.

**Point Coordinates** (satisfies X² + Y² + Z² + M² = R²):

For dimension i with value v in N-dimensional embedding:
```
θ = 2π × (i / N)           // Angular position along trajectory
φ = π × sigmoid(v)         // Value maps to polar angle [0, π]
r = R                      // Hypersphere radius

X = r × sin(φ) × cos(θ)
Y = r × sin(φ) × sin(θ)
Z = r × cos(φ)
M = r × sin(φ) × sign(v) × |cos(θ) - sin(θ)|  // Encodes sign
```

Every point satisfies the hypersphere constraint. The embedding value (including sign) is encoded in the angular position. Different embedding sizes (384-dim, 768-dim, 1024-dim) all produce valid trajectories.

**Hilbert Index for Embeddings**: Use `ST_Centroid(geom)` to get the trajectory's center of mass, then compute Hilbert index on that centroid. Similar embeddings have similar centroids → similar Hilbert indices → efficient k-NN via B-tree range scan.

**Process**:
1. Token → composition if multi-character ("cat" = [c, a, t]), or constant if single character ('a' = character constant)
2. Embedding vector (N numbers) → N-point LineString where each point is (value, i/N, 0, 0)
3. The LineString IS the embedding
4. Link to token atom via refs

**Embedding similarity = trajectory similarity**:
- `ST_FrechetDistance` - trajectory comparison
- `ST_HausdorffDistance` - shape comparison
- `ST_Distance` on centroids - rough similarity

N dimensions = N points. Single-character tokens reference the character constant directly.

---

## Weight Ingestion

A weight connects input atom A to output atom B. **Weights are connection counts, not value atoms.**

**Storage**: refs and multiplicity only. Geometry is computed on demand.
- refs = [input_atom_id, output_atom_id]
- multiplicities = [1, weight_multiplicity]
- Geometry (2-point LineString [A, B]) derived when needed via:
  ```sql
  SELECT ST_MakeLine(a.geom, b.geom)
  FROM atom a, atom b
  WHERE a.id = edge.refs[1] AND b.id = edge.refs[2];
  ```

**Weight magnitude = edge multiplicity**:
- Normalize weights across the layer
- Quantize to integer (e.g., 0.95 → 95 connections)
- Store as multiplicity on the edge
- Query weight strength: `SUM(multiplicities[2])` or `COUNT(*)`

**Negative weights = spatial distance**:
- Positive weight (attraction): A and B are close on hypersphere
- Negative weight (repulsion): A and B are far on hypersphere
- The distance between atom positions encodes relationship type
- Multiplicity encodes magnitude regardless of sign

**No weight value atoms. No stored edge geometry.** Compute geometry from refs when spatial queries are needed.

**Sparsity**: Weights below threshold (0.5 / 50%) are non-relationships. Do not store. Only meaningful connections exist. This is not compression - it is the natural representation. Most weights are noise.

---

## Spatial Operations

PostGIS functions become the query language:

| Operation | Function |
|-----------|----------|
| Similarity | `ST_Distance(a.geom, b.geom)` |
| Relatedness | `ST_Intersects(a.geom, b.geom)` |
| Containment | `ST_Within(a.geom, b.geom)` |
| Overlap | `ST_Intersection(a.geom, b.geom)` |
| Proximity search | `ST_DWithin(a.geom, b.geom, threshold)` |

Trajectories that intersect share semantic content. The length of intersection indicates degree of overlap. Spatial joins across compositions find related content regardless of original source type.

---

## Reconstruction

Any ingested content can be reconstructed by traversing refs to leaf constants.

**Text**: Collect leaf atoms in order, reverse landmark projection (or store codepoint directly), emit UTF-8.

**Embedding**: Read LineString points, extract X coordinate from each point, return as float array.

**Weights**: Query edge compositions between input/output atoms, extract multiplicities, reconstruct sparse matrix.

### Weight Reconstruction (Export)

Weights are stored as sparse edges with multiplicity. To export back to a weight matrix:

```cpp
void reconstruct_weight_matrix(
    AtomStore& store,
    const std::vector<int64_t>& input_atoms,   // Row atom IDs
    const std::vector<int64_t>& output_atoms,  // Column atom IDs
    std::vector<float>& weights_out            // Output matrix (row-major)
) {
    size_t rows = input_atoms.size();
    size_t cols = output_atoms.size();
    weights_out.resize(rows * cols, 0.0f);  // Initialize to zero (sparse)
    
    // Find max multiplicity for denormalization
    int32_t max_mult = store.query_scalar<int32_t>(
        "SELECT MAX(multiplicities[2]) FROM atom WHERE NOT is_constant AND array_length(refs, 1) = 2"
    );
    
    // Query all edges between these atoms
    for (size_t i = 0; i < rows; i++) {
        for (size_t j = 0; j < cols; j++) {
            int32_t mult = store.get_edge_multiplicity(input_atoms[i], output_atoms[j]);
            
            // Denormalize: multiplicity → approximate weight
            // Note: This is LOSSY - sub-threshold weights are zero
            float weight = static_cast<float>(mult) / static_cast<float>(max_mult);
            weights_out[i * cols + j] = weight;
        }
    }
}
```

**Important**: Reconstruction is lossy for weights. Sub-threshold weights (below 0.5) were not stored and export as 0. This is by design - the exported model is smaller and tighter.

Lossless for text. Lossless for embeddings. Lossy-by-design for weights.

---

## Technology

**PostgreSQL + PostGIS**: Storage, spatial indexing, referential integrity, ACID.

**C++ native library**:
- Landmark projection computation
- Hilbert curve computation
- High-throughput ingestion
- Content hashing

**C#**:
- Orchestration
- API surface
- Integration

**Communication**: C# calls C++ via P/Invoke. C++ connects to PostgreSQL via libpq.

---

## What This Is

A substrate where storage and computation are unified. The atom table is not a cache for an external model. The atom table IS the model. Relationships between atoms ARE the weights. Traversal of compositions IS inference.

Ingesting a model does not copy it - it decomposes it into the universal structure. Ingesting text, code, audio, video does the same. All content becomes atoms and compositions on the same hypersphere, queryable with the same spatial operations.

Deduplication is automatic. Common patterns across sources share atoms. The structure self-organizes through content addressing.

---

## Deduplication Implications

Same content = same hash = same atom ID. Everywhere.

**The word "the" in document A and document B is THE SAME ATOM.** Not a copy. Not a duplicate. The same row in the database.

**What this means**:
- If a model and a document share vocabulary, they share atoms
- If two models share tokens, they share token atoms
- If code and documentation use the same words, they share those atoms

**Shared atoms = semantic connections**:
- Query: "What atoms does this model share with this document?"
- Query: "What vocabulary overlaps between these two models?"
- Query: "What concepts connect this image caption to this code comment?"

The refs arrays form a DAG. Multiple roots can share subtrees. The structure IS the knowledge graph.

---

## Gap Inference (Mendeleev Insight)

Gaps in Hilbert space have meaning. Like Mendeleev predicting elements from gaps in the periodic table.

**The hypersphere is seeded with characters and common patterns. But most of the space is empty.**

When content is ingested:
- Trajectories cluster in regions
- Some regions remain sparse
- The sparse regions are not noise - they are undiscovered semantic territory

**Inference opportunities**:
- A trajectory passes near an unexplored region → potential concept exists there
- Two trajectories nearly intersect but don't → potential connection exists
- A composition references atoms that cluster except for one outlier → the outlier may be misassigned

**This is not speculation.** Hilbert curves preserve locality. Nearby indices mean nearby positions. Clusters of atoms in Hilbert space are semantically related. Gaps between clusters are semantic boundaries. A gap where there "should" be content (based on surrounding topology) is a prediction.

Query for gaps:
```sql
-- Find Hilbert regions with high density neighbors but low local density
-- (Simplified - real implementation needs spatial statistics)
SELECT h.region, COUNT(*) as neighbors
FROM hilbert_grid h
LEFT JOIN atom a ON a.hilbert_high BETWEEN h.low AND h.high
GROUP BY h.region
HAVING COUNT(*) < (SELECT AVG(count) FROM neighboring_regions)
```
