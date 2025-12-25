# ✅ Immediate Action Checklist

## Before Writing Any More Code

### Phase 0: Understanding & Planning (COMPLETE THIS FIRST)

- [ ] **Read EXECUTIVE_SUMMARY.md** (10 min) - Understand what vs why
- [ ] **Read VISUAL_ARCHITECTURE.md** (15 min) - See the data flows
- [ ] **Read ARCHITECTURE.md** (30 min) - Deep dive into concepts
- [ ] **Read PIVOT_PLAN.md** (15 min) - Week-by-week execution
- [ ] **Discuss with team** - Confirm commitment to pivot
- [ ] **Create backup branch** - `git checkout -b backup/17-table-implementation`
- [ ] **Push backup** - `git push origin backup/17-table-implementation`

**🛑 DO NOT PROCEED until above is complete!**

---

## Phase 1: Development Environment Setup

### C++ Build Tools
- [ ] Install CMake 3.20+ (`choco install cmake` or download)
- [ ] Install Visual Studio 2022 with C++ workload (Windows)
  - OR install build-essential + gcc/g++ (Linux)
  - OR install Xcode Command Line Tools (macOS)
- [ ] Verify: `cmake --version` shows 3.20+
- [ ] Verify: `g++ --version` or `cl.exe` works

### PostgreSQL Development Libraries
- [ ] Install PostgreSQL dev headers
  - Windows: Already have from PostgreSQL install
  - Linux: `sudo apt-get install libpq-dev`
  - macOS: `brew install libpq`
- [ ] Verify: `pg_config --includedir` shows headers location
- [ ] Verify: `pg_config --libdir` shows library location

### BLAKE3 Reference Implementation
- [ ] Clone: `git clone https://github.com/BLAKE3-team/BLAKE3`
- [ ] Test build: `cd BLAKE3/c && gcc -c blake3.c blake3_dispatch.c blake3_portable.c`
- [ ] Plan: Link BLAKE3 .o files into our library (or include source directly)

### Project Structure
- [ ] Create `src/Hart.MCP.Native/` directory
- [ ] Create `src/Hart.MCP.Native/CMakeLists.txt`
- [ ] Create `src/Hart.MCP.Native/src/` (for .cpp files)
- [ ] Create `src/Hart.MCP.Native/include/` (for .h files)
- [ ] Create `src/Hart.MCP.Native/tests/` (for unit tests)

---

## Phase 2: Algorithm Validation (Python Prototype)

### Landmark Projection Prototype
- [ ] Create `prototypes/landmark_projection.py`
- [ ] Implement character category segmentation (letters/digits/punct)
- [ ] Implement Fibonacci spiral placement (golden angle)
- [ ] Implement 4D hypersphere coordinates
- [ ] **Verify constraint: X² + Y² + Z² + M² ≈ R²** (within floating-point tolerance)
- [ ] Test: All ASCII characters have unique positions
- [ ] Test: 'A' and 'a' are spatially close
- [ ] Test: 'è' and 'e' are spatially close
- [ ] Visualize: Plot characters in 3D (project 4D → 3D)

### Hilbert Curve Prototype
- [ ] Create `prototypes/hilbert_curve.py`
- [ ] Implement Gray code functions (binary ↔ Gray)
- [ ] Implement quantization (double → uint32)
- [ ] Implement 4D Hilbert forward transform
- [ ] Implement 4D Hilbert inverse transform
- [ ] **Test round-trip: coords → Hilbert → coords** (within quantization error)
- [ ] **Test locality: nearby coords → nearby indices**
- [ ] Benchmark: 1M coordinate transforms/sec target

### BLAKE3 Validation
- [ ] Install Python BLAKE3: `pip install blake3`
- [ ] Test hashing 4D coordinates (x,y,z,m as doubles)
- [ ] Test hashing arrays (child hashes + multiplicities)
- [ ] Verify uniqueness: 1M random hashes, no collisions
- [ ] Profile performance: hash rate on typical inputs

---

## Phase 3: C++ Implementation (Week 1-2)

### landmark_projection.cpp
- [ ] Create header: `include/hartonomous/landmark_projection.h`
- [ ] Implement: `landmark_project_character(uint32_t codepoint, double* x, y, z, m)`
- [ ] Implement: `landmark_project_number(double value, double* x, y, z, m)`
- [ ] Implement: Category lookup (enum CharacterCategory)
- [ ] Implement: Fibonacci spiral positioning
- [ ] Implement: Clustering (A near a, è near e)
- [ ] Write unit tests: `tests/test_landmark_projection.cpp`
- [ ] **Verify:** X² + Y² + Z² + M² = R² for all outputs
- [ ] **Verify:** Determinism (same input → same output always)
- [ ] **Verify:** Clustering (spatial distance < threshold for variants)

### blake3_hash.cpp
- [ ] Create header: `include/hartonomous/blake3_hash.h`
- [ ] Integrate BLAKE3 reference C implementation
- [ ] Implement: `hash_constant(double x, y, z, m, uint8_t* hash_out)`
- [ ] Implement: `hash_composition(uint8_t* child_hashes, size_t n, int32_t* mults, uint8_t* out)`
- [ ] Implement: `hash_bytes(void* data, size_t len, uint8_t* hash_out)`
- [ ] Write unit tests: `tests/test_blake3_hash.cpp`
- [ ] **Verify:** 32-byte output for all functions
- [ ] **Verify:** Determinism (same input → same hash)
- [ ] **Verify:** Uniqueness (1M random inputs → no collisions)

### CMake Build System
- [ ] Complete `CMakeLists.txt` with find_package(PostgreSQL)
- [ ] Add BLAKE3 source files to build
- [ ] Add GTest for unit testing
- [ ] Configure shared library output (.so / .dll)
- [ ] Test: `mkdir build && cd build && cmake .. && make`
- [ ] **Verify:** `libhartonomous_native.so` (or `.dll`) created
- [ ] **Verify:** All unit tests pass

---

## Phase 4: C++ Implementation (Week 3-4)

### hilbert_curve.cpp
- [ ] Create header: `include/hartonomous/hilbert_curve.h`
- [ ] Implement Gray code: `binary_to_gray(uint32_t n)`, `gray_to_binary(uint32_t g)`
- [ ] Implement quantization: `quantize(double val)`, `dequantize(uint32_t q)`
- [ ] Implement rotation state: `struct RotationState { uint8_t perm[4], flip }`
- [ ] Implement: `update_rotation(RotationState& state, uint8_t gray_code)`
- [ ] Implement: `coords_to_hilbert(double x, y, z, m, uint64_t* high, uint64_t* low)`
- [ ] Implement: `hilbert_to_coords(uint64_t high, low, double* x, y, z, m)`
- [ ] Write unit tests: `tests/test_hilbert_curve.cpp`
- [ ] **Test:** Round-trip coords → Hilbert → coords (within quantization error)
- [ ] **Test:** Locality (distance in 4D ≈ distance in Hilbert)
- [ ] **Benchmark:** >= 100K transforms/sec

---

## Phase 5: PostgreSQL Integration (Week 5-6)

### Database Connection
- [ ] Create: `src/db_connection.cpp`
- [ ] Implement: `PGconn* connect_db(const char* conninfo)`
- [ ] Implement: `void disconnect_db(PGconn* conn)`
- [ ] Implement: `int64_t execute_scalar(PGconn* conn, const char* sql, ...)`
- [ ] Implement: Error handling and logging
- [ ] Test: Connect to HART-MCP database

### Atom Upsert
- [ ] Implement: `int64_t upsert_constant(PGconn* conn, HilbertIndex h, PointZM coords, ContentHash hash)`
- [ ] Implement: `int64_t upsert_composition(PGconn* conn, HilbertIndex h, int64_t* refs, size_t n_refs, int32_t* mults, ContentHash hash)`
- [ ] Use prepared statements for performance
- [ ] Use: `INSERT ... ON CONFLICT (content_hash) DO UPDATE SET id = atom.id RETURNING id`
- [ ] Test: Create 1000 constants, verify deduplication works

### Bulk Insert Optimization
- [ ] Implement: `bulk_insert_constants(PGconn* conn, AtomData* atoms, size_t count)`
- [ ] Use PostgreSQL COPY protocol for speed
- [ ] Target: >= 10K atoms/sec insertion rate
- [ ] Test: Insert 100K constants, measure time

---

## Phase 6: Text Ingestion Pipeline (Week 7-8)

### UTF-8 Processing
- [ ] Implement: `std::vector<uint32_t> utf8_to_codepoints(const char* text, size_t len)`
- [ ] Handle multi-byte UTF-8 sequences correctly
- [ ] Test: All Unicode planes (BMP, SMP, etc.)

### RLE Encoding
- [ ] Implement: `struct RLESequence { std::vector<int64_t> refs; std::vector<int32_t> mults }`
- [ ] Implement: `RLESequence rle_encode(const std::vector<int64_t>& atoms)`
- [ ] Implement: `std::vector<int64_t> rle_decode(const RLESequence& encoded)`
- [ ] Test: "Hellooooo" → [H,e,l,o] with mults [1,1,2,5]

### Basic Text Ingestion
- [ ] Implement: `int64_t ingest_text_basic(const char* text, size_t len, const char* conninfo)`
- [ ] Pipeline: UTF-8 → codepoints → constants → RLE → composition
- [ ] Return document root atom ID
- [ ] Test: Ingest "Hello, World!" → verify all atoms created
- [ ] Test: Ingest same text twice → verify deduplication (same IDs)

### Reconstruction
- [ ] Implement: `void reconstruct_text(int64_t atom_id, char** text_out, size_t* len_out, PGconn* conn)`
- [ ] Traverse refs recursively (or stack-based for deep trees)
- [ ] Collect leaf constants → codepoints → UTF-8
- [ ] Test: Round-trip ingest → reconstruct (100% match)

---

## Phase 7: CPE Implementation (Week 9-10)

### Vocabulary Training
- [ ] Implement: `int64_t cpe_train(const char** texts, size_t n_texts, size_t vocab_size, size_t min_freq, PGconn* conn)`
- [ ] Count all adjacent atom pairs
- [ ] Find most frequent pair
- [ ] Create composition atom for pair
- [ ] Replace all occurrences in working corpus
- [ ] Repeat until vocab_size reached
- [ ] Store vocabulary as composition atoms
- [ ] Return vocabulary root atom ID

### Encoding & Decoding
- [ ] Implement: `std::vector<int64_t> cpe_encode(const std::vector<int64_t>& atoms, int64_t vocab_id, PGconn* conn)`
- [ ] Apply merges in training order (deterministic)
- [ ] Implement: `std::vector<int64_t> cpe_decode(const std::vector<int64_t>& encoded, int64_t vocab_id, PGconn* conn)`
- [ ] Expand compositions recursively to leaf constants
- [ ] Test: Train on small corpus → encode → decode → verify reconstruction

### Integration with Text Ingestion
- [ ] Modify `ingest_text` to accept `cpe_vocab_id` parameter
- [ ] Apply CPE after RLE encoding
- [ ] Test: Ingest text with CPE → verify compression (fewer atoms)

---

## Phase 8: Embeddings & Weights (Week 11-12)

### Embedding Ingestion
- [ ] Implement: `int64_t ingest_embedding(const float* values, size_t dims, int64_t token_id, PGconn* conn)`
- [ ] For each dimension: value → point on hypersphere (θ, φ calculation)
- [ ] Build WKT: `LINESTRING ZM(x1 y1 z1 m1, x2 y2 z2 m2, ...)`
- [ ] Compute centroid for Hilbert index
- [ ] Hash raw float array for content_hash
- [ ] Create composition with refs=[token_id], geom=LineString
- [ ] Test: Ingest 768-dim embedding → verify LineString has 768 points

### Weight Ingestion
- [ ] Implement: `void ingest_weight_matrix(const float* weights, size_t rows, cols, int64_t* input_atoms, int64_t* output_atoms, double threshold, PGconn* conn)`
- [ ] Find max absolute weight (for normalization)
- [ ] For each weight: normalize, threshold (>0.5), quantize to multiplicity
- [ ] Store sparse edges: refs=[input, output], multiplicities=[1, magnitude]
- [ ] Test: Ingest 1000×1000 matrix → verify sparsity (90% discarded)
- [ ] Test: Query connection strength → SUM(multiplicities[2])

---

## Phase 9: P/Invoke Integration (Week 13-14)

### C# Wrapper
- [ ] Create: `src/Hart.MCP.Core/Native/HartonomousNative.cs`
- [ ] Add `[DllImport("hartonomous_native")]` for all C++ functions
- [ ] Handle memory management (Marshal.AllocHGlobal, FreeHGlobal)
- [ ] Handle string marshaling (UTF8, LPUTF8Str)
- [ ] Test: Call C++ functions from C# successfully

### Integration Tests
- [ ] C# → ingest_text → PostgreSQL → verify atoms created
- [ ] C# → reconstruct_text → verify matches original
- [ ] C# → spatial query → verify results
- [ ] End-to-end: Ingest via C++, query via EF Core

---

## Phase 10: EF Core Schema Pivot (Week 15-16)

### Delete Old Schema
- [ ] Remove all files in `src/Hart.MCP.Core/Entities/` except new `Atom.cs`
- [ ] Delete `src/Hart.MCP.Core/Data/SpatialKnowledgeContext.cs`
- [ ] Remove all files in `src/Hart.MCP.Api/Migrations/`
- [ ] Remove all files in `src/Hart.MCP.Api/Controllers/` (will recreate)

### Create New Schema
- [ ] Create: `src/Hart.MCP.Core/Entities/Atom.cs` (single entity)
- [ ] Create: `src/Hart.MCP.Core/Data/AtomContext.cs`
- [ ] Configure geometry with SRID 0
- [ ] Configure array columns (refs, multiplicities)
- [ ] Configure indexes (GIST, B-tree, HASH, GIN)

### New Migration
- [ ] cd `src/Hart.MCP.Api`
- [ ] `dotnet ef migrations add InitialAtomSubstrate --context AtomContext`
- [ ] Review migration SQL carefully
- [ ] **Backup existing database:** `pg_dump HART-MCP > backup.sql`
- [ ] `dotnet ef database drop --force` (DESTRUCTIVE!)
- [ ] `dotnet ef database update` (creates new atom table)
- [ ] Verify: `psql` shows single `atom` table with correct schema

---

## Phase 11: New API Controllers (Week 17-18)

### Ingestion Controller
- [ ] Create: `src/Hart.MCP.Api/Controllers/IngestionController.cs`
- [ ] POST `/api/ingestion/text` → calls `HartonomousNative.ingest_text`
- [ ] POST `/api/ingestion/model` → calls `HartonomousNative.ingest_model`
- [ ] POST `/api/ingestion/file` → dispatches based on file type
- [ ] Test: Upload text file → verify root atom ID returned

### Query Controller
- [ ] Create: `src/Hart.MCP.Api/Controllers/QueryController.cs`
- [ ] POST `/api/query/knn` → KNN search via GiST `<->` operator
- [ ] POST `/api/query/hilbert-range` → Range scan on (hilbert_high, hilbert_low)
- [ ] POST `/api/query/trajectory-match` → ST_FrechetDistance for embeddings
- [ ] GET `/api/query/weight-strength?input={}&output={}` → SUM(multiplicities)
- [ ] Test: Query endpoints return correct results

### Reconstruct Controller
- [ ] Create: `src/Hart.MCP.Api/Controllers/ReconstructController.cs`
- [ ] GET `/api/reconstruct/text/{id}` → calls `HartonomousNative.reconstruct_text`
- [ ] GET `/api/reconstruct/embedding/{id}` → calls `HartonomousNative.reconstruct_embedding`
- [ ] Test: Reconstruct returns original content

### CPE Controller
- [ ] Create: `src/Hart.MCP.Api/Controllers/CPEController.cs`
- [ ] POST `/api/cpe/train` → calls `HartonomousNative.cpe_train`
- [ ] POST `/api/cpe/encode` → calls `HartonomousNative.cpe_encode`
- [ ] Test: Train vocabulary, use for encoding

---

## Phase 12: Model Parsers (Week 19-20)

### GGUF Parser
- [ ] Research GGUF format specification
- [ ] Implement: Parse vocabulary from GGUF file
- [ ] Implement: Parse embedding matrix
- [ ] Implement: Parse weight tensors (all layers)
- [ ] Test: Load small GGUF model (Llama 3B)

### SafeTensors Parser
- [ ] Research SafeTensors format
- [ ] Implement: Load tensors from SafeTensors file
- [ ] Map to vocabulary, embeddings, weights
- [ ] Test: Load small SafeTensors model

### Full Model Ingestion
- [ ] Implement: `int64_t ingest_model(const char* model_path, const char* format, double threshold, PGconn* conn)`
- [ ] Parse format (GGUF/SafeTensors)
- [ ] Ingest vocabulary (tokens → atoms)
- [ ] Ingest embeddings (vectors → LineStrings)
- [ ] Ingest weights (matrices → sparse edges with multiplicity)
- [ ] Create model root composition
- [ ] Test: Ingest Llama 3B → verify all atoms created
- [ ] Test: Query model structure spatially

---

## Phase 13: Blazor UI (Week 21-24)

### Atom Inspector
- [ ] Create: Blazor component showing atom details (ID, Hilbert, hash, refs, geom)
- [ ] Add: Reconstruct buttons (as text, as embedding)
- [ ] Add: View refs as tree (Merkle DAG explorer)
- [ ] Test: Navigate atom hierarchy

### Semantic Search
- [ ] Create: Search interface (text input → query)
- [ ] Backend: CPE encode query → KNN search
- [ ] Display: Results with semantic distance
- [ ] Add: Click to view in 3D space

### 3D Hypersphere Visualizer
- [ ] Integrate Three.js or Babylon.js
- [ ] Render: Atoms as points on sphere surface
- [ ] Render: Connections as lines (edges)
- [ ] Add: Camera controls (orbit, zoom, pan)
- [ ] Add: Selection (click atom → show details)
- [ ] Test: Visualize 1000 atoms + connections

### Model Comparison Tool
- [ ] Create: Select two models UI
- [ ] Backend: Query shared atoms, unique atoms
- [ ] Display: Venn diagram showing overlap
- [ ] Add: Extract difference button (creates new model atom)
- [ ] Test: Compare Llama variants

---

## Phase 14: MAUI App (Week 25-28)

### Offline Mode
- [ ] Add SQLite cache for frequently accessed atoms
- [ ] Implement sync service (background queue)
- [ ] Test: Work offline, sync when online

### Platform Features
- [ ] Camera integration (Mobile): OCR → text ingestion
- [ ] Voice input: Speech-to-text → ingestion
- [ ] Share extension: Share files to app → ingestion
- [ ] Test: All platforms (Windows, macOS, iOS, Android)

---

## Success Criteria

### C++ Native Library
- ✅ All unit tests pass
- ✅ Landmark projection satisfies hypersphere constraint
- ✅ Hilbert curve preserves locality
- ✅ BLAKE3 hashes are unique (tested on 1M samples)
- ✅ Text round-trip is lossless (ingest → reconstruct = original)
- ✅ Embedding ingestion creates valid LineStrings
- ✅ Weight ingestion achieves 80-90% sparsity

### Database & API
- ✅ Single `atom` table with correct schema (SRID 0, indexes)
- ✅ All API endpoints functional
- ✅ Spatial queries complete in <100ms
- ✅ Can ingest Llama 3B model completely
- ✅ Model comparison shows meaningful differences

### UI & User Experience
- ✅ Blazor visualizer renders semantic space in 3D
- ✅ Atom inspector shows complete Merkle DAG
- ✅ Semantic search returns relevant results
- ✅ MAUI app works on all platforms

---

## ⚠️ Common Pitfalls to Avoid

1. **Don't skip the Python prototype** - Validate math before C++
2. **Don't implement all features at once** - Build incrementally
3. **Don't neglect unit tests** - Test each component thoroughly
4. **Don't delete old code until new code works** - Keep backup branch
5. **Don't optimize prematurely** - Get it working, then make it fast
6. **Don't ignore spatial index performance** - Monitor query times
7. **Don't forget content hash validation** - Verify deduplication works
8. **Don't assume embedding reconstruction is lossless** - It's approximate (quantization error)
9. **Don't treat weights as value atoms** - They're multiplicities on edges
10. **Don't use SRID 4326** - This is semantic space (SRID 0), not geography

---

**Check off items as you complete them. Update this file with notes/blockers.**

**Good luck building the substrate!** 🚀
