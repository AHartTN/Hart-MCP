# PIVOT PLAN: From 17 Tables to The One Table

## Current State vs Target State

### What We Have (WRONG)
```
Hart-MCP Database:
├── SpatialNodes           ❌ Should be atoms with is_constant=TRUE
├── SpatialRelations       ❌ Should be atoms with is_constant=FALSE, array_length(refs)=2
├── KnowledgeClusters      ❌ Should be computed via ST_ConvexHull, not stored
├── ContentSources         ❌ Application layer tracking, not substrate
├── ConversationSessions   ❌ Application layer, not substrate
├── ConversationTurns      ❌ Application layer, not substrate
├── TurnNodeReferences     ❌ Application layer, not substrate
├── AIModels               ❌ Application layer metadata, not substrate
├── ModelLayers            ❌ Application layer, not substrate
├── ModelComparisons       ❌ Computed on demand, not stored
├── ResearchAnnotations    ❌ Application layer, not substrate
├── AnnotationNodeLinks    ❌ Application layer, not substrate
├── VisualizationViews     ❌ Application layer, not substrate
├── SpatialBookmarks       ❌ Application layer, not substrate
└── spatial_ref_sys        ✓ PostGIS system table (keep)

EF Core Projects:
├── Hart.MCP.Core          ❌ 8+ entity classes (wrong model)
├── Hart.MCP.Shared        ❌ DTOs for wrong entities
├── Hart.MCP.Api           ❌ Controllers for wrong entities
├── Hart.MCP.Web           ✓ Blazor WebAssembly (keep)
├── Hart.MCP.Maui          ✓ MAUI Hybrid (keep)
```

### What We Need (CORRECT)
```
HART-MCP Database:
└── atom                   ✅ ONE TABLE - constants + compositions
    ├── id BIGSERIAL
    ├── hilbert_high BIGINT
    ├── hilbert_low BIGINT
    ├── geom GEOMETRY(GEOMETRYZM, 0)
    ├── is_constant BOOLEAN
    ├── refs BIGINT[]
    ├── multiplicities INT[]
    └── content_hash BYTEA UNIQUE

Projects:
├── Hart.MCP.Native (C++)  ⚠️ DOESN'T EXIST YET - Critical!
├── Hart.MCP.Core          🔄 Single Atom entity + AtomContext
├── Hart.MCP.Shared        🔄 Minimal DTOs for substrate ops
├── Hart.MCP.Api           🔄 Ingestion/Query/Reconstruct controllers
├── Hart.MCP.Web           ✓ Keep, add spatial viz
├── Hart.MCP.Maui          ✓ Keep, add offline mode
```

---

## Migration Strategy

### Phase 1: Preserve Current Work (Backup)
```powershell
# Create backup branch
git checkout -b backup/17-table-implementation
git add -A
git commit -m "Backup: 17-table EF Core implementation before pivot"
git push origin backup/17-table-implementation

# Create new feature branch
git checkout main
git checkout -b feature/single-atom-table-substrate
```

### Phase 2: C++ Native Library (CRITICAL PATH)

**This is the foundation. Nothing else works without it.**

#### 2.1 Create Native Project

```powershell
# Create C++ project structure
New-Item -Path "src\Hart.MCP.Native" -ItemType Directory
New-Item -Path "src\Hart.MCP.Native\src" -ItemType Directory
New-Item -Path "src\Hart.MCP.Native\include" -ItemType Directory
New-Item -Path "src\Hart.MCP.Native\tests" -ItemType Directory

# Create CMakeLists.txt
@"
cmake_minimum_required(VERSION 3.20)
project(hartonomous_native CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find PostgreSQL
find_package(PostgreSQL REQUIRED)

include_directories(include ${PostgreSQL_INCLUDE_DIRS})

# Source files
set(SOURCES
    src/landmark_projection.cpp
    src/hilbert_curve.cpp
    src/blake3_hash.cpp
    src/ingestion.cpp
    src/cpe.cpp
    src/reconstruction.cpp
)

# Shared library
add_library(hartonomous_native SHARED ${SOURCES})
target_link_libraries(hartonomous_native ${PostgreSQL_LIBRARIES})

# Tests
enable_testing()
add_subdirectory(tests)
"@ | Out-File -FilePath "src\Hart.MCP.Native\CMakeLists.txt"
```

#### 2.2 Implementation Priority

1. **landmark_projection.cpp** (Week 1)
   - Character category segmentation
   - Fibonacci spiral placement
   - Hypersphere coordinate computation
   - Clustering for related chars (A/a, è/e)

2. **hilbert_curve.cpp** (Week 1-2)
   - 4D Gray code operations
   - Rotation state tracking
   - Quantization/dequantization
   - Coords ↔ Hilbert index transforms

3. **blake3_hash.cpp** (Week 1)
   - BLAKE3 implementation (or use BLAKE3 reference C impl)
   - Hash constants (4D coords)
   - Hash compositions (child hashes + multiplicities)

4. **ingestion.cpp** (Week 2-3)
   - Text atomization
   - RLE encoding
   - CPE application
   - Hierarchical composition building
   - PostgreSQL connection via libpq
   - Bulk insert optimization

5. **cpe.cpp** (Week 3)
   - Pair frequency counting
   - Merge iteration
   - Vocabulary storage
   - Encode/decode functions

6. **reconstruction.cpp** (Week 3)
   - Merkle DAG traversal
   - Text reconstruction
   - Embedding reconstruction
   - Weight matrix reconstruction

### Phase 3: Revised Core Library

#### 3.1 Delete Existing Entities

```powershell
Remove-Item "src\Hart.MCP.Core\Entities\*.cs"
```

#### 3.2 Create Single Atom Entity

File: `src\Hart.MCP.Core\Entities\Atom.cs`
```csharp
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Entities;

/// <summary>
/// The universal atom - represents both constants and compositions.
/// </summary>
public class Atom
{
    /// <summary>
    /// Unique identifier (PostgreSQL BIGSERIAL)
    /// </summary>
    public long Id { get; set; }
    
    /// <summary>
    /// Upper 64 bits of Hilbert index (for B-tree range queries)
    /// </summary>
    public long HilbertHigh { get; set; }
    
    /// <summary>
    /// Lower 64 bits of Hilbert index
    /// </summary>
    public long HilbertLow { get; set; }
    
    /// <summary>
    /// Geometry on 4D hypersphere (SRID 0 = abstract semantic space)
    /// - PointZM for constants (characters, numbers)
    /// - LineString for sequences (embeddings, text after CPE)
    /// - Polygon for regions (computed via ST_ConvexHull)
    /// - NULL for edges (geometry computed on demand)
    /// </summary>
    public Geometry? Geom { get; set; }
    
    /// <summary>
    /// TRUE = constant (character, number, single-char token)
    /// FALSE = composition (n-gram, embedding, weight edge, document)
    /// </summary>
    public bool IsConstant { get; set; }
    
    /// <summary>
    /// Child atom IDs (NULL for constants)
    /// For compositions: ordered list of constituent atoms
    /// For edges: [input_atom, output_atom]
    /// </summary>
    public long[]? Refs { get; set; }
    
    /// <summary>
    /// Parallel array with Refs
    /// For RLE: repeat counts
    /// For edges: [1, weight_multiplicity]
    /// </summary>
    public int[]? Multiplicities { get; set; }
    
    /// <summary>
    /// BLAKE3-256 hash (32 bytes) for content addressing
    /// Enables automatic deduplication
    /// </summary>
    public byte[] ContentHash { get; set; } = null!;
}
```

#### 3.3 Revised DbContext

File: `src\Hart.MCP.Core\Data\AtomContext.cs`
```csharp
using Microsoft.EntityFrameworkCore;
using Hart.MCP.Core.Entities;

namespace Hart.MCP.Core.Data;

public class AtomContext : DbContext
{
    public AtomContext(DbContextOptions<AtomContext> options) : base(options) { }
    
    public DbSet<Atom> Atoms => Set<Atom>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<Atom>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            // GiST spatial index
            entity.HasIndex(e => e.Geom)
                .HasMethod("GIST")
                .HasDatabaseName("idx_atom_geom");
            
            // B-tree Hilbert index
            entity.HasIndex(e => new { e.HilbertHigh, e.HilbertLow })
                .HasDatabaseName("idx_atom_hilbert");
            
            // Hash index for deduplication
            entity.HasIndex(e => e.ContentHash)
                .IsUnique()
                .HasMethod("HASH")
                .HasDatabaseName("idx_atom_hash");
            
            // GIN index on refs for graph traversal
            entity.HasIndex(e => e.Refs)
                .HasMethod("GIN")
                .HasDatabaseName("idx_atom_refs");
            
            // Configure SRID 0 (abstract 4D space)
            entity.Property(e => e.Geom)
                .HasColumnType("geometry(GEOMETRYZM, 0)");
            
            // PostgreSQL array types
            entity.Property(e => e.Refs)
                .HasColumnType("bigint[]");
            
            entity.Property(e => e.Multiplicities)
                .HasColumnType("int[]");
            
            // Content hash as bytea
            entity.Property(e => e.ContentHash)
                .HasColumnType("bytea")
                .HasMaxLength(32)  // BLAKE3-256 = 32 bytes
                .IsRequired();
        });
    }
}
```

### Phase 4: New Migration

```powershell
# Remove old migrations
Remove-Item "src\Hart.MCP.Api\Migrations\*" -Recurse -Force

# Create new migration with single atom table
cd src\Hart.MCP.Api
dotnet ef migrations add InitialAtomSubstrate --context AtomContext

# Review migration before applying
# Should create:
# - atom table with 8 columns
# - 4 indexes (GiST, B-tree, HASH, GIN)

# Apply to database (WARNING: Will drop old tables!)
dotnet ef database update --context AtomContext
```

**IMPORTANT:** This DROPS the existing `HART-MCP` database! Backup first if needed:
```powershell
pg_dump -h localhost -p 5432 -U hartonomous HART-MCP > backup_17tables.sql
```

### Phase 5: Revised API Controllers

#### 5.1 Delete Old Controllers
```powershell
Remove-Item "src\Hart.MCP.Api\Controllers\*Controller.cs"
```

#### 5.2 Create New Controllers

See `FRONT_END_REQUIREMENTS.md` for full implementations:
- `IngestionController` - Text/model/file ingestion via C++ native
- `QueryController` - Spatial operations (KNN, Hilbert, trajectories)
- `ReconstructController` - Text/embedding/weight reconstruction
- `CPEController` - Vocabulary training and management

### Phase 6: P/Invoke Wrapper

File: `src\Hart.MCP.Core\Native\HartonomousNative.cs`

See `FRONT_END_REQUIREMENTS.md` for complete implementation.

### Phase 7: Testing Strategy

#### 7.1 Unit Tests (C++)
```cpp
// Test landmark projection determinism
TEST(LandmarkProjection, CharacterDeterminism) {
    double x1, y1, z1, m1;
    landmark_project_character('A', &x1, &y1, &z1, &m1);
    
    double x2, y2, z2, m2;
    landmark_project_character('A', &x2, &y2, &z2, &m2);
    
    ASSERT_DOUBLE_EQ(x1, x2);
    ASSERT_DOUBLE_EQ(y1, y2);
    ASSERT_DOUBLE_EQ(z1, z2);
    ASSERT_DOUBLE_EQ(m1, m2);
    
    // Verify hypersphere constraint
    double r_squared = x1*x1 + y1*y1 + z1*z1 + m1*m1;
    ASSERT_NEAR(r_squared, 1.0, 1e-9);
}

// Test Hilbert curve locality preservation
TEST(HilbertCurve, LocalityPreservation) {
    // Points close in 4D should have close Hilbert indices
}

// Test BLAKE3 collision resistance (smoke test)
TEST(ContentHash, NoDuplicates) {
    // Hash 1 million random atoms, verify all unique
}
```

#### 7.2 Integration Tests (C#)
```csharp
[Fact]
public async Task IngestText_CreatesAtoms()
{
    // Arrange
    string text = "Hello, World!";
    
    // Act
    long atomId = HartonomousNative.ingest_text(text, (nuint)text.Length, 0, _connInfo);
    
    // Assert
    var atom = await _context.Atoms.FindAsync(atomId);
    Assert.NotNull(atom);
    Assert.False(atom.IsConstant);  // Document is composition
    Assert.NotNull(atom.Refs);
    Assert.True(atom.Refs!.Length > 0);
}

[Fact]
public async Task ReconstructText_MatchesOriginal()
{
    // Round-trip test
    string original = "The quick brown fox";
    long atomId = HartonomousNative.ingest_text(original, (nuint)original.Length, 0, _connInfo);
    
    HartonomousNative.reconstruct_text(atomId, out var textPtr, out var len, _connInfo);
    string reconstructed = Marshal.PtrToStringUTF8(textPtr, (int)len)!;
    
    Assert.Equal(original, reconstructed);
}
```

#### 7.3 Spatial Query Tests
```csharp
[Fact]
public async Task KNN_ReturnsNearestNeighbors()
{
    // Create test atoms with known positions
    // Query k-NN
    // Verify results ordered by distance
}

[Fact]
public async Task HilbertRange_PreservesLocality()
{
    // Create atoms in same spatial region
    // Query Hilbert range
    // Verify all returned atoms are spatially close
}
```

---

## Execution Timeline

### Week 1 (Foundation)
- [ ] Create backup branch
- [ ] Set up C++ project structure
- [ ] Implement `landmark_projection.cpp` (characters only)
- [ ] Implement `blake3_hash.cpp`
- [ ] Write unit tests for above

### Week 2 (Core Algorithms)
- [ ] Implement `hilbert_curve.cpp` (4D forward transform)
- [ ] Implement inverse Hilbert transform
- [ ] Add number projection to `landmark_projection.cpp`
- [ ] Write unit tests

### Week 3 (Ingestion Pipeline)
- [ ] Implement `ingestion.cpp` basic text pipeline
- [ ] Add PostgreSQL connection (libpq)
- [ ] Implement RLE encoding
- [ ] Test character constant creation

### Week 4 (EF Core Pivot)
- [ ] Delete old entities/migrations
- [ ] Create single `Atom` entity
- [ ] Create new `AtomContext`
- [ ] Create new migration
- [ ] Drop and recreate database

### Week 5 (P/Invoke & API)
- [ ] Implement `HartonomousNative` P/Invoke wrapper
- [ ] Create `IngestionController` (text only)
- [ ] Create `QueryController` (KNN only)
- [ ] Test end-to-end: C++ → C# → PostgreSQL

### Week 6 (CPE Implementation)
- [ ] Implement `cpe.cpp` training
- [ ] Implement CPE encode/decode
- [ ] Add `CPEController`
- [ ] Test on small corpus

### Week 7 (Reconstruction)
- [ ] Implement `reconstruction.cpp` for text
- [ ] Add `ReconstructController`
- [ ] Test round-trip: ingest → reconstruct

### Week 8 (Embeddings)
- [ ] Implement embedding → LineString conversion
- [ ] Add `ingest_embedding` to native lib
- [ ] Test trajectory similarity queries

### Week 9-10 (Weights)
- [ ] Implement sparse weight ingestion
- [ ] Test multiplicity-based weight queries
- [ ] Implement weight reconstruction

### Week 11-12 (Model Parsers)
- [ ] GGUF format parser
- [ ] SafeTensors format parser
- [ ] End-to-end model ingestion test

---

## Risk Mitigation

### Risk 1: C++ Complexity
**Mitigation:** 
- Start with Python prototype for algorithms
- Use BLAKE3 reference implementation (don't write from scratch)
- Extensive unit tests before integration

### Risk 2: Performance
**Mitigation:**
- Benchmark Hilbert computation early
- Profile PostgreSQL bulk inserts
- Add batching/parallel processing if needed

### Risk 3: Data Loss
**Mitigation:**
- Keep backup branch with 17-table implementation
- Export any test data before migration
- Version migrations carefully

### Risk 4: Scope Creep
**Mitigation:**
- Build incrementally (text → embeddings → weights → models)
- Don't start Blazor UI until backend is stable
- Focus on substrate first, applications later

---

## Success Criteria

### Milestone 1: Character Constants
- [ ] Can create constant atoms for all ASCII characters
- [ ] Deduplication works (same char = same atom ID)
- [ ] Spatial queries return correct neighborhoods

### Milestone 2: Text Ingestion
- [ ] Can ingest plain text documents
- [ ] Hierarchy builds correctly (chars → words → sentences → doc)
- [ ] Reconstruction matches original text

### Milestone 3: CPE Vocabulary
- [ ] Can train CPE on corpus
- [ ] Common pairs merge into compositions
- [ ] Encode/decode works correctly

### Milestone 4: Embeddings
- [ ] Can ingest N-dimensional embeddings as LineStrings
- [ ] Trajectory similarity queries work
- [ ] Reconstruction returns original float array

### Milestone 5: Weights
- [ ] Can ingest sparse weight matrices
- [ ] Multiplicity encodes magnitude correctly
- [ ] Connection strength queries work

### Milestone 6: Full Model
- [ ] Can ingest complete GGUF/SafeTensors model
- [ ] Vocabulary, embeddings, and weights all stored
- [ ] Can query model structure spatially

---

## Next Steps (Immediate Actions)

1. **Review this plan with stakeholders**
2. **Create backup branch NOW**
3. **Set up C++ build environment** (CMake, libpq, BLAKE3)
4. **Write landmark projection prototype in Python** (validate math)
5. **Implement first C++ functions** (character projection + BLAKE3)
6. **Write comprehensive unit tests**
7. **Only then** start touching EF Core

**DO NOT DELETE ANY CODE UNTIL C++ LIBRARY IS WORKING.**

The 17-table implementation was a valuable learning exercise. We now understand the problem space. Time to build the correct solution.
