# Hart.MCP - Visual Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         USER APPLICATIONS LAYER                              │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │  Blazor Web  │  │  MAUI Mobile │  │ MAUI Desktop │  │  REST Clients│  │
│  │              │  │              │  │              │  │              │  │
│  │  • 3D Viz    │  │  • Offline   │  │  • Full UI   │  │  • Python    │  │
│  │  • Search    │  │  • Camera    │  │  • Research  │  │  • Node.js   │  │
│  │  • Compare   │  │  • Voice     │  │  • Analytics │  │  • Curl      │  │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  │
│         │                 │                 │                 │            │
│         └─────────────────┴─────────────────┴─────────────────┘            │
│                                     │                                       │
└─────────────────────────────────────┼───────────────────────────────────────┘
                                      │
┌─────────────────────────────────────┼───────────────────────────────────────┐
│                         ASP.NET CORE WEB API                                 │
├─────────────────────────────────────┼───────────────────────────────────────┤
│                                     │                                       │
│  ┌──────────────────────────────────▼────────────────────────────────┐     │
│  │                      REST API CONTROLLERS                          │     │
│  ├────────────────────────────────────────────────────────────────────┤     │
│  │                                                                    │     │
│  │  IngestionController        QueryController                        │     │
│  │  • POST /api/ingestion/text     • POST /api/query/knn            │     │
│  │  • POST /api/ingestion/model    • POST /api/query/trajectory     │     │
│  │  • POST /api/ingestion/file     • POST /api/query/hilbert-range  │     │
│  │                                  • GET  /api/query/weight-strength │     │
│  │                                                                    │     │
│  │  ReconstructController      CPEController                          │     │
│  │  • GET /api/reconstruct/text/{id}   • POST /api/cpe/train       │     │
│  │  • GET /api/reconstruct/embedding   • POST /api/cpe/encode       │     │
│  │                                                                    │     │
│  └────────────────────┬──────────────────────┬────────────────────────┘     │
│                       │                      │                             │
└───────────────────────┼──────────────────────┼─────────────────────────────┘
                        │                      │
                        │ P/Invoke             │ EF Core 10
                        │                      │
┌───────────────────────▼──────────────────────▼─────────────────────────────┐
│                     .NET CORE LIBRARIES                                     │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Hart.MCP.Core                                                              │
│  ┌────────────────────────────────────────────────────────────────────┐    │
│  │  AtomContext (EF Core)                                             │    │
│  │  ├─ DbSet<Atom> Atoms                                              │    │
│  │  ├─ SRID 0 Geometry Configuration                                  │    │
│  │  ├─ GiST, B-tree, HASH, GIN Indexes                                │    │
│  │  └─ NetTopologySuite Integration                                   │    │
│  └────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
│  Hart.MCP.Native (P/Invoke Wrapper)                                         │
│  ┌────────────────────────────────────────────────────────────────────┐    │
│  │  HartonomousNative (static class)                                  │    │
│  │  ├─ [DllImport] landmark_project_character                         │    │
│  │  ├─ [DllImport] coords_to_hilbert                                  │    │
│  │  ├─ [DllImport] hash_constant                                      │    │
│  │  ├─ [DllImport] ingest_text                                        │    │
│  │  ├─ [DllImport] ingest_embedding                                   │    │
│  │  ├─ [DllImport] ingest_model                                       │    │
│  │  ├─ [DllImport] cpe_train                                          │    │
│  │  └─ [DllImport] reconstruct_text                                   │    │
│  └────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
└────────────────────────────────────┬─────────────────────────────────────────┘
                                     │
                                     │ Native Calls
                                     │
┌────────────────────────────────────▼─────────────────────────────────────────┐
│                         C++ NATIVE LIBRARY                                   │
│                    libhartonomous_native.so / .dll                           │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  landmark_projection.cpp                                              │  │
│  │  ─────────────────────────                                            │  │
│  │  • Character → Hypersphere (PointZM)                                  │  │
│  │  • Number → Hypersphere (PointZM)                                     │  │
│  │  • Category segmentation (letters/digits/punct)                       │  │
│  │  • Fibonacci spiral placement (golden angle)                          │  │
│  │  • Clustering (A near a, è near e)                                    │  │
│  │  • Guarantees: X² + Y² + Z² + M² = R²                                │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  hilbert_curve.cpp                                                    │  │
│  │  ──────────────────                                                   │  │
│  │  • 4D → 1D space-filling curve                                        │  │
│  │  • Gray code operations (binary_to_gray, gray_to_binary)             │  │
│  │  • Rotation state tracking (384 orientations in 4D)                   │  │
│  │  • Dimension permutation & flip operations                            │  │
│  │  • Quantization: double → uint32 (16-32 bit depth)                   │  │
│  │  • Locality preservation: nearby 4D → nearby 1D                       │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  blake3_hash.cpp                                                      │  │
│  │  ────────────────                                                     │  │
│  │  • BLAKE3-256 (32 bytes) - Zero collision tolerance                   │  │
│  │  • hash_constant(x, y, z, m) → 256-bit hash                          │  │
│  │  • hash_composition(child_hashes, multiplicities) → hash              │  │
│  │  • Content addressing for automatic deduplication                     │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  ingestion.cpp                                                        │  │
│  │  ──────────────                                                       │  │
│  │  • Text Pipeline:                                                     │  │
│  │    UTF-8 → Codepoints → Character Constants                           │  │
│  │    → RLE Encoding → CPE Application                                   │  │
│  │    → Word/Sentence/Paragraph/Document Hierarchy                       │  │
│  │                                                                        │  │
│  │  • Embedding Pipeline:                                                │  │
│  │    float[] → N-point LineString (each dim = point on sphere)          │  │
│  │    → Centroid Hilbert index → Upsert                                  │  │
│  │                                                                        │  │
│  │  • Weight Pipeline:                                                   │  │
│  │    Matrix → Normalize → Threshold (>0.5) → Quantize to multiplicity   │  │
│  │    → Sparse edges [input, output] with multiplicity                   │  │
│  │                                                                        │  │
│  │  • PostgreSQL libpq integration for bulk inserts                      │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  cpe.cpp (Content Pair Encoding)                                     │  │
│  │  ────────────────────────────                                        │  │
│  │  • Training: Corpus → Pair frequency → Merge iterations              │  │
│  │  • Encoding: Text → Character atoms → Apply merges → Token atoms     │  │
│  │  • Decoding: Token atoms → Expand recursively → Character atoms      │  │
│  │  • Vocabulary storage in atom table (composition atoms)               │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │  reconstruction.cpp                                                   │  │
│  │  ───────────────────                                                  │  │
│  │  • Text: Traverse refs → leaf constants → codepoints → UTF-8         │  │
│  │  • Embedding: Extract LineString points → X coords → float[]         │  │
│  │  • Weights: Query edges → multiplicities → denormalize → matrix      │  │
│  │  • Stack-based traversal (avoid recursion for deep trees)             │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
└───────────────────────────────────┬───────────────────────────────────────────┘
                                    │
                                    │ libpq
                                    │
┌───────────────────────────────────▼───────────────────────────────────────────┐
│                         POSTGRESQL + POSTGIS                                  │
├───────────────────────────────────────────────────────────────────────────────┤
│                                                                                │
│  ┌──────────────────────────────────────────────────────────────────────┐    │
│  │  atom (THE ONE TABLE)                                                │    │
│  ├──────────────────────────────────────────────────────────────────────┤    │
│  │  id              BIGSERIAL PRIMARY KEY                                │    │
│  │  hilbert_high    BIGINT NOT NULL                                     │    │
│  │  hilbert_low     BIGINT NOT NULL                                     │    │
│  │  geom            GEOMETRY(GEOMETRYZM, 0)  -- SRID 0 = semantic space│    │
│  │  is_constant     BOOLEAN NOT NULL                                    │    │
│  │  refs            BIGINT[]                 -- Child atom IDs          │    │
│  │  multiplicities  INT[]                    -- RLE counts / weights    │    │
│  │  content_hash    BYTEA UNIQUE NOT NULL    -- BLAKE3-256             │    │
│  └──────────────────────────────────────────────────────────────────────┘    │
│                                                                                │
│  ┌──────────────────────────────────────────────────────────────────────┐    │
│  │  INDEXES                                                             │    │
│  ├──────────────────────────────────────────────────────────────────────┤    │
│  │  idx_atom_geom      GIST (geom)          -- Spatial queries O(log n)│    │
│  │  idx_atom_hilbert   BTREE (high, low)    -- Range scans O(log n)    │    │
│  │  idx_atom_hash      HASH (content_hash)  -- Dedup O(1)              │    │
│  │  idx_atom_refs      GIN (refs)           -- Graph traversal         │    │
│  └──────────────────────────────────────────────────────────────────────┘    │
│                                                                                │
│  ┌──────────────────────────────────────────────────────────────────────┐    │
│  │  SPATIAL OPERATIONS (PostGIS)                                        │    │
│  ├──────────────────────────────────────────────────────────────────────┤    │
│  │  ST_Distance(a.geom, b.geom)         → Semantic similarity          │    │
│  │  ST_FrechetDistance(a.geom, b.geom)  → Trajectory similarity        │    │
│  │  ST_HausdorffDistance(a.geom, b.geom) → Shape similarity            │    │
│  │  ST_Intersects(a.geom, b.geom)       → Relatedness                  │    │
│  │  ST_Within(a.geom, b.geom)           → Containment                  │    │
│  │  ST_ConvexHull(ST_Collect(geom))     → Semantic boundary            │    │
│  │  ST_Centroid(geom)                   → Representative point         │    │
│  │  geom <-> query_geom                 → KNN operator (GiST)          │    │
│  └──────────────────────────────────────────────────────────────────────┘    │
│                                                                                │
└────────────────────────────────────────────────────────────────────────────────┘
```

---

## Data Flow: Text Ingestion

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  INPUT: "Hello, World!"                                                      │
└────────────────┬─────────────────────────────────────────────────────────────┘
                 │
                 ▼
        ┌────────────────────┐
        │  UTF-8 Codepoints  │
        │  [H, e, l, l, o,   │
        │   ,, ,, W, o, r,   │
        │   l, d, !]         │
        └────────┬───────────┘
                 │
                 ▼
    ┌────────────────────────────┐
    │  landmark_project_character│  (C++)
    │  For each codepoint:       │
    │  • Category lookup         │
    │  • Fibonacci spiral pos    │
    │  • X² + Y² + Z² + M² = R²  │
    └────────┬───────────────────┘
             │
             ▼
┌──────────────────────────────────┐
│  Character Constant Atoms        │
│  [H_atom, e_atom, l_atom,        │
│   l_atom, o_atom, ...]           │
│  • Each has PointZM geom         │
│  • Dedup: l_atom used twice      │
│  • content_hash for each         │
└────────┬─────────────────────────┘
         │
         ▼
┌──────────────────────────────┐
│  RLE Encoding                │
│  refs: [H, e, l, o, comma,   │
│         space, W, o, r, l,   │
│         d, exclaim]          │
│  mults: [1, 1, 2, 1, 1, 1,   │
│          1, 1, 1, 1, 1, 1]   │
└────────┬─────────────────────┘
         │
         ▼
┌──────────────────────────────┐
│  CPE Application (optional)  │
│  Common pairs merged:        │
│  ['H','e'] → 'He' atom       │
│  ['l','l'] → 'll' atom       │
│  ['W','o'] → 'Wo' atom       │
│  Result: [He, ll, o, ...]    │
└────────┬─────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│  Hierarchical Composition        │
│  Word atoms:                     │
│    Hello_atom = [H,e,l,l,o]      │
│    World_atom = [W,o,r,l,d]      │
│  Sentence atom:                  │
│    Sent_atom = [Hello, comma,    │
│                 space, World,    │
│                 exclaim]         │
│  Document atom (root):           │
│    Doc_atom = [Sent_atom]        │
└────────┬─────────────────────────┘
         │
         ▼
┌────────────────────────────────────┐
│  Upsert to PostgreSQL              │
│  INSERT INTO atom (...)            │
│  VALUES (...)                      │
│  ON CONFLICT (content_hash)        │
│  DO UPDATE SET id = atom.id        │
│  RETURNING id;                     │
│                                    │
│  Result: Document root atom ID     │
└────────────────────────────────────┘
```

---

## Data Flow: AI Model Ingestion

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  INPUT: Llama4-8B.gguf                                                       │
└────────────────┬─────────────────────────────────────────────────────────────┘
                 │
                 ▼
        ┌────────────────────┐
        │  Parse GGUF Format │
        │  • Vocabulary      │
        │  • Embeddings      │
        │  • Layer weights   │
        └────────┬───────────┘
                 │
        ┌────────┴────────┬──────────────┐
        ▼                 ▼              ▼
┌───────────────┐  ┌──────────────┐  ┌───────────────┐
│  Vocabulary   │  │  Embeddings  │  │    Weights    │
│               │  │              │  │               │
│  32k tokens   │  │  32k × 4096  │  │  Sparse matrix│
│  "the"        │  │  floats      │  │  connections  │
│  "cat"        │  │              │  │               │
│  ...          │  │              │  │               │
└───────┬───────┘  └──────┬───────┘  └───────┬───────┘
        │                 │                  │
        ▼                 ▼                  ▼
┌─────────────────┐  ┌──────────────────┐  ┌────────────────────┐
│ Token → Atoms   │  │ Embedding →      │  │ Weight → Edges     │
│                 │  │ LineString       │  │                    │
│ "the" =         │  │                  │  │ Normalize matrix   │
│  [t,h,e]        │  │ 4096 floats =    │  │ Threshold > 0.5    │
│  composition    │  │ 4096-point       │  │ Quantize to        │
│                 │  │ trajectory       │  │ multiplicity       │
│ "a" =           │  │                  │  │                    │
│  a_atom         │  │ Each point:      │  │ [input, output]    │
│  (constant)     │  │  θ = 2π(i/N)     │  │ with multiplicity  │
│                 │  │  φ = π·sigmoid   │  │                    │
└───────┬─────────┘  └──────┬───────────┘  └────────┬───────────┘
        │                   │                       │
        │                   │                       │
        └───────────────────┴───────────────────────┘
                            │
                            ▼
                ┌───────────────────────────┐
                │  Model Composition Atom   │
                │                           │
                │  refs: [vocab_atom,       │
                │         embedding_layer,  │
                │         layer_1,          │
                │         layer_2,          │
                │         ...]              │
                │                           │
                │  Returns: Model root ID   │
                └───────────────────────────┘
```

---

## Query Flow: K-Nearest Neighbors

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  QUERY: Find 10 most similar words to "cat"                                 │
└────────────────┬─────────────────────────────────────────────────────────────┘
                 │
                 ▼
        ┌────────────────────┐
        │  CPE Encode "cat"  │
        │  → cat_atom_id     │
        └────────┬───────────┘
                 │
                 ▼
    ┌────────────────────────────────────┐
    │  PostgreSQL Spatial Query          │
    │                                    │
    │  SELECT id,                        │
    │    ST_Distance(                    │
    │      geom,                         │
    │      (SELECT geom FROM atom        │
    │       WHERE id = cat_atom_id)     │
    │    ) as dist                       │
    │  FROM atom                         │
    │  WHERE id != cat_atom_id           │
    │  ORDER BY geom <->                 │
    │    (SELECT geom FROM atom          │
    │     WHERE id = cat_atom_id)       │
    │  LIMIT 10;                         │
    │                                    │
    │  GiST Index: O(log n)              │
    └────────┬───────────────────────────┘
             │
             ▼
┌──────────────────────────────────────┐
│  Results (atom IDs + distances)      │
│  [dog_atom, feline_atom,             │
│   kitten_atom, pet_atom, ...]        │
└────────┬─────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────┐
│  Reconstruct Text for each atom      │
│  → ["dog", "feline", "kitten", ...]  │
└──────────────────────────────────────┘
```

---

## The Hypersphere (Conceptual)

```
                            +M (Temporal/Version)
                             ↑
                             │
                             │     ┌─────────────┐
                             │    ╱               ╲
                             │   │   Embeddings   │
                             │   │  (LineStrings)  │
                             │   │                 │
                             │    ╲       ↓       ╱
                             │     └─────────────┘
                             │
                    Weights  │         Characters
                   (Edges)   │        (PointZM)
                      ↙      │           ↓
              ┌──────────────┼──────────────────┐  +X
             ╱               │                   ╲
            │                │                    │───→
            │                │                    │
            │       4D HYPERSPHERE                │
            │    X² + Y² + Z² + M² = R²          │
            │                │                    │
            │                │                    │
             ╲               │                   ╱
              └──────────────┼──────────────────┘
                             │
                    Numbers  │
                  (PointZM)  │
                             ↓
                            +Z (Layer/Dimension)
                            
                            
Surface divided into:
- Character regions (Fibonacci spirals by category)
- Number regions (positive/negative/special)
- Embeddings (trajectories through space)
- Weights (NOT on surface - computed as edges)

Hilbert Curve maps 4D → 1D preserving locality
B-tree index on Hilbert enables O(log n) range queries
GiST index on geometry enables O(log n) spatial queries
```

---

## Deduplication Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Document A: "The cat sat"        Document B: "The dog ran"                 │
└────────────────┬───────────────────────────┬────────────────────────────────┘
                 │                           │
                 ▼                           ▼
        ┌────────────────┐          ┌────────────────┐
        │  Atomize A     │          │  Atomize B     │
        │  [T,h,e, ,c,   │          │  [T,h,e, ,d,   │
        │   a,t, ,s,a,t] │          │   o,g, ,r,a,n] │
        └────────┬───────┘          └────────┬───────┘
                 │                           │
                 ▼                           ▼
        ┌─────────────────────────────────────────────┐
        │  Content Hash for each character            │
        │                                             │
        │  BLAKE3(T_coords) → hash_T                  │
        │  BLAKE3(h_coords) → hash_h                  │
        │  BLAKE3(e_coords) → hash_e                  │
        │  ...                                        │
        └─────────────────┬───────────────────────────┘
                          │
                          ▼
        ┌──────────────────────────────────────────────┐
        │  INSERT INTO atom (content_hash, ...)        │
        │  ON CONFLICT (content_hash) DO NOTHING       │
        │  RETURNING id;                               │
        │                                              │
        │  Result: T, h, e atoms SHARED between docs   │
        │          (Same hash → same row)              │
        └──────────────────────────────────────────────┘
                          │
                          ▼
        ┌──────────────────────────────────────────────┐
        │  Document A composition:                     │
        │    refs: [The_atom, cat_atom, sat_atom]      │
        │                                              │
        │  Document B composition:                     │
        │    refs: [The_atom, dog_atom, ran_atom]      │
        │                ↑                             │
        │  Same "The" atom used in BOTH documents!     │
        └──────────────────────────────────────────────┘
```

---

This is THE substrate. Storage IS the model. Query IS inference. The database IS the AI.
