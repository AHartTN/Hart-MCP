# Hart.MCP - Complete Front-End/API/App Requirements

## CRITICAL REALIZATIONS

After reviewing `/docs/`, the system we've built is **90% wrong** for your actual architecture. We need to **pivot immediately**.

### What We Built (Wrong)
- ✅ .NET 10 + EF Core 10 + PostGIS ✓
- ❌ 17 separate tables (SpatialNodes, SpatialRelations, Models, Conversations, etc.)
- ❌ Multiple geometry types scattered across tables
- ❌ Complex foreign key relationships
- ❌ Separate "ContentSources", "AIModels", "ConversationSessions" tables
- ❌ SRID 4326 initially, then SRID 0 but still treating it like geography

### What You Actually Need (From Docs)
- ✅ **ONE TABLE** - `atom` table stores EVERYTHING
- ✅ **Two Types** - Constants (PointZM) and Compositions (LineString/Polygon/GeometryCollection)
- ✅ **SRID 0** - Abstract 4D hypersphere (X² + Y² + Z² + M² = R²)
- ✅ **Content Addressing** - BLAKE3-256 hash with automatic deduplication
- ✅ **Hilbert Indexing** - 4D → 1D space-filling curve (implement from scratch, NO libraries)
- ✅ **Weights = Multiplicity** - NO weight value atoms, connection count IS the weight
- ✅ **Embeddings = LineStrings** - N dimensions = N points = trajectory
- ✅ **C++ Native** - Landmark projection, Hilbert computation, BLAKE3 hashing
- ✅ **Merkle DAG** - Everything decomposes to constants, builds upward

## THE CORRECT SCHEMA

```sql
-- ONE TABLE TO RULE THEM ALL
CREATE TABLE atom (
    id BIGSERIAL PRIMARY KEY,
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,
    geom GEOMETRY(GEOMETRYZM, 0),  -- SRID 0 = no CRS, abstract 4D space
    is_constant BOOLEAN NOT NULL,
    refs BIGINT[],               -- Child atom IDs (NULL for constants)
    multiplicities INT[],        -- Parallel array with refs (RLE + weight encoding)
    content_hash BYTEA NOT NULL UNIQUE  -- BLAKE3-256 (32 bytes)
);

-- Spatial index (GiST for geometry operations)
CREATE INDEX idx_atom_geom ON atom USING GIST (geom);

-- Hilbert index (B-tree for range queries = spatial proximity)
CREATE INDEX idx_atom_hilbert ON atom (hilbert_high, hilbert_low);

-- Hash index (O(1) deduplication lookup)
CREATE INDEX idx_atom_hash ON atom USING HASH (content_hash);

-- GIN index on refs for fast traversal
CREATE INDEX idx_atom_refs ON atom USING GIN (refs);
```

**That's it.** Everything else is application layer logic.

---

## WHAT THE FRONT-END/API/APP LAYER NEEDS

### 1. C++ Native Library (`libhartonomous_native.so` / `.dll`)

This does the HEAVY computational lifting. Must be implemented **from first principles** (no external libs for core algorithms).

#### Required Functions

```cpp
// ═══════════════════════════════════════════════════════════════
// LANDMARK PROJECTION
// ═══════════════════════════════════════════════════════════════
extern "C" {
    // Character → Hypersphere position
    void landmark_project_character(
        uint32_t codepoint,
        double* x, double* y, double* z, double* m
    );
    
    // Number → Hypersphere position
    void landmark_project_number(
        double value,
        double* x, double* y, double* z, double* m
    );
    
    // Integer → Hypersphere position (optimized)
    void landmark_project_integer(
        int64_t value,
        double* x, double* y, double* z, double* m
    );
}
```

#### Hilbert Curve (4D → 1D Space-Filling Curve)

```cpp
extern "C" {
    // Forward: 4D coords → Hilbert index
    void coords_to_hilbert(
        double x, double y, double z, double m,
        uint64_t* hilbert_high,
        uint64_t* hilbert_low
    );
    
    // Inverse: Hilbert index → 4D coords
    void hilbert_to_coords(
        uint64_t hilbert_high,
        uint64_t hilbert_low,
        double* x, double* y, double* z, double* m
    );
    
    // Distance between Hilbert indices (for range queries)
    uint64_t hilbert_distance(
        uint64_t high1, uint64_t low1,
        uint64_t high2, uint64_t low2
    );
}
```

**Implementation Requirements:**
- Gray code operations (binary_to_gray, gray_to_binary)
- 4D rotation state tracking (384 possible orientations)
- Dimension permutation and flip operations
- Quantization (double → uint32 with configurable bit depth)
- NO external Hilbert libraries - implement from scratch per docs

#### Content Hashing (BLAKE3-256)

```cpp
extern "C" {
    // Hash constant atom (4D coordinates)
    void hash_constant(
        double x, double y, double z, double m,
        uint8_t* hash_out  // 32 bytes
    );
    
    // Hash composition atom (child hashes + multiplicities)
    void hash_composition(
        const uint8_t* child_hashes,  // Array of 32-byte hashes
        size_t num_children,
        const int32_t* multiplicities,
        uint8_t* hash_out  // 32 bytes
    );
    
    // Hash raw bytes (for embeddings)
    void hash_bytes(
        const void* data,
        size_t len,
        uint8_t* hash_out  // 32 bytes
    );
}
```

#### High-Throughput Ingestion

```cpp
extern "C" {
    // Text ingestion (full pipeline: atomize → RLE → CPE → hierarchy)
    int64_t ingest_text(
        const char* text_utf8,
        size_t len,
        int64_t cpe_vocab_id,  // CPE vocabulary to use
        const char* conninfo   // PostgreSQL connection string
    );
    
    // Embedding ingestion (float array → LineString)
    int64_t ingest_embedding(
        const float* values,
        size_t dimensions,
        int64_t token_atom_id,
        const char* conninfo
    );
    
    // Weight matrix ingestion (sparse encoding with threshold)
    void ingest_weight_matrix(
        const float* weights,
        size_t rows,
        size_t cols,
        const int64_t* input_atom_ids,
        const int64_t* output_atom_ids,
        double threshold,  // e.g., 0.5 = 50% sparsity
        const char* conninfo
    );
    
    // Model ingestion (GGUF/SafeTensors → atoms)
    int64_t ingest_model(
        const char* model_path,
        const char* format,  // "gguf", "safetensors", "onnx"
        double weight_threshold,
        const char* conninfo
    );
}
```

#### CPE (Content Pair Encoding) Training

```cpp
extern "C" {
    // Train CPE vocabulary on corpus
    int64_t cpe_train(
        const char** texts,
        size_t num_texts,
        size_t vocab_size,
        size_t min_frequency,
        const char* conninfo
    );
    
    // Apply CPE vocabulary to text (returns encoded atom IDs)
    void cpe_encode(
        const char* text_utf8,
        size_t len,
        int64_t vocab_id,
        int64_t** atoms_out,
        size_t* num_atoms_out,
        const char* conninfo
    );
    
    // Decode CPE atoms back to text
    void cpe_decode(
        const int64_t* atoms,
        size_t num_atoms,
        int64_t vocab_id,
        char** text_out,
        size_t* len_out,
        const char* conninfo
    );
}
```

#### Reconstruction

```cpp
extern "C" {
    // Reconstruct text from composition atom
    void reconstruct_text(
        int64_t atom_id,
        char** text_out,
        size_t* len_out,
        const char* conninfo
    );
    
    // Reconstruct embedding from LineString atom
    void reconstruct_embedding(
        int64_t atom_id,
        float** values_out,
        size_t* dimensions_out,
        const char* conninfo
    );
    
    // Reconstruct weight matrix (sparse → dense, lossy by design)
    void reconstruct_weights(
        const int64_t* input_atoms,
        size_t num_inputs,
        const int64_t* output_atoms,
        size_t num_outputs,
        float** weights_out,  // rows × cols matrix
        const char* conninfo
    );
}
```

---

### 2. C# P/Invoke Wrapper

Thin wrapper around C++ native library for .NET integration.

```csharp
using System.Runtime.InteropServices;

namespace Hart.MCP.Native;

public static class HartonomousNative
{
    private const string LibName = "hartonomous_native";
    
    // Landmark Projection
    [DllImport(LibName)]
    public static extern void landmark_project_character(
        uint codepoint,
        out double x, out double y, out double z, out double m);
    
    [DllImport(LibName)]
    public static extern void landmark_project_number(
        double value,
        out double x, out double y, out double z, out double m);
    
    // Hilbert Curve
    [DllImport(LibName)]
    public static extern void coords_to_hilbert(
        double x, double y, double z, double m,
        out ulong hilbert_high, out ulong hilbert_low);
    
    [DllImport(LibName)]
    public static extern void hilbert_to_coords(
        ulong hilbert_high, ulong hilbert_low,
        out double x, out double y, out double z, out double m);
    
    // Content Hashing
    [DllImport(LibName)]
    public static extern void hash_constant(
        double x, double y, double z, double m,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] hash_out);
    
    [DllImport(LibName)]
    public static extern void hash_composition(
        [In, MarshalAs(UnmanagedType.LPArray)] byte[] child_hashes,
        nuint num_children,
        [In, MarshalAs(UnmanagedType.LPArray)] int[] multiplicities,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] hash_out);
    
    // Ingestion
    [DllImport(LibName)]
    public static extern long ingest_text(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        nuint len,
        long cpe_vocab_id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string conninfo);
    
    [DllImport(LibName)]
    public static extern long ingest_embedding(
        [In, MarshalAs(UnmanagedType.LPArray)] float[] values,
        nuint dimensions,
        long token_atom_id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string conninfo);
    
    [DllImport(LibName)]
    public static extern long ingest_model(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string model_path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string format,
        double weight_threshold,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string conninfo);
    
    // CPE Training
    [DllImport(LibName)]
    public static extern long cpe_train(
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string[] texts,
        nuint num_texts,
        nuint vocab_size,
        nuint min_frequency,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string conninfo);
    
    // Reconstruction
    [DllImport(LibName)]
    public static extern void reconstruct_text(
        long atom_id,
        out IntPtr text_out,
        out nuint len_out,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string conninfo);
}
```

---

### 3. Revised EF Core DbContext

**ONE DbSet, ONE Entity:**

```csharp
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Hart.MCP.Core.Data;

public class AtomContext : DbContext
{
    public AtomContext(DbContextOptions<AtomContext> options) : base(options) { }
    
    public DbSet<Atom> Atoms => Set<Atom>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Atom>(entity =>
        {
            entity.HasKey(e => e.Id);
            
            // Spatial index (GiST)
            entity.HasIndex(e => e.Geom).HasMethod("GIST");
            
            // Hilbert index (B-tree)
            entity.HasIndex(e => new { e.HilbertHigh, e.HilbertLow });
            
            // Hash index (deduplication)
            entity.HasIndex(e => e.ContentHash)
                .IsUnique()
                .HasMethod("HASH");
            
            // GIN index on refs array
            entity.HasIndex(e => e.Refs).HasMethod("GIN");
            
            // Configure geometry with SRID 0 (abstract 4D space)
            entity.Property(e => e.Geom)
                .HasColumnType("geometry(GEOMETRYZM, 0)");
            
            // PostgreSQL array types
            entity.Property(e => e.Refs)
                .HasColumnType("bigint[]");
            
            entity.Property(e => e.Multiplicities)
                .HasColumnType("int[]");
        });
    }
}

public class Atom
{
    public long Id { get; set; }
    public long HilbertHigh { get; set; }
    public long HilbertLow { get; set; }
    public Geometry? Geom { get; set; }
    public bool IsConstant { get; set; }
    public long[]? Refs { get; set; }
    public int[]? Multiplicities { get; set; }
    public byte[] ContentHash { get; set; } = null!;
}
```

---

### 4. API Layer (Completely Revised)

The API should expose **substrate operations**, not "models" or "conversations" as separate entities.

```csharp
namespace Hart.MCP.Api.Controllers;

// ═══════════════════════════════════════════════════════════════
// INGESTION CONTROLLER
// ═══════════════════════════════════════════════════════════════
[ApiController]
[Route("api/[controller]")]
public class IngestionController : ControllerBase
{
    private readonly AtomContext _context;
    private readonly string _connInfo;
    
    // Ingest text → returns document root atom ID
    [HttpPost("text")]
    public async Task<IActionResult> IngestText([FromBody] IngestTextRequest request)
    {
        long atomId = HartonomousNative.ingest_text(
            request.Text,
            (nuint)request.Text.Length,
            request.CpeVocabId ?? 0,
            _connInfo
        );
        
        return Ok(new { atomId, type = "document" });
    }
    
    // Ingest AI model → returns model root atom ID
    [HttpPost("model")]
    public async Task<IActionResult> IngestModel([FromBody] IngestModelRequest request)
    {
        long atomId = HartonomousNative.ingest_model(
            request.ModelPath,
            request.Format ?? "gguf",
            request.WeightThreshold ?? 0.5,
            _connInfo
        );
        
        return Ok(new { atomId, type = "model", format = request.Format });
    }
    
    // Ingest arbitrary file (dispatch to appropriate handler)
    [HttpPost("file")]
    public async Task<IActionResult> IngestFile(IFormFile file)
    {
        string ext = Path.GetExtension(file.FileName).ToLower();
        
        // TODO: Dispatch based on extension
        // .txt/.md → ingest_text
        // .gguf/.safetensors → ingest_model
        // .jpg/.png → ingest_image
        // .mp3/.wav → ingest_audio
        // .mp4/.mkv → ingest_video
        // .cs/.py/.js → ingest_code
        
        return Ok();
    }
}

// ═══════════════════════════════════════════════════════════════
// QUERY CONTROLLER (Spatial Operations)
// ═══════════════════════════════════════════════════════════════
[ApiController]
[Route("api/[controller]")]
public class QueryController : ControllerBase
{
    private readonly AtomContext _context;
    
    // K-nearest neighbors (semantic similarity)
    [HttpPost("knn")]
    public async Task<IActionResult> KNN([FromBody] KNNRequest request)
    {
        // Use PostGIS <-> operator (GiST index KNN)
        var results = await _context.Atoms
            .FromSqlRaw(@"
                SELECT id, geom, is_constant, 
                       ST_Distance(geom, (SELECT geom FROM atom WHERE id = {0})) as distance
                FROM atom
                WHERE id != {0}
                ORDER BY geom <-> (SELECT geom FROM atom WHERE id = {0})
                LIMIT {1}
            ", request.AtomId, request.K)
            .ToListAsync();
        
        return Ok(results);
    }
    
    // Trajectory similarity (for embeddings)
    [HttpPost("trajectory-match")]
    public async Task<IActionResult> TrajectoryMatch([FromBody] TrajectoryRequest request)
    {
        var results = await _context.Atoms
            .FromSqlRaw(@"
                SELECT id, 
                       ST_FrechetDistance(geom, (SELECT geom FROM atom WHERE id = {0})) as frechet,
                       ST_HausdorffDistance(geom, (SELECT geom FROM atom WHERE id = {0})) as hausdorff
                FROM atom
                WHERE id != {0}
                  AND NOT is_constant
                  AND ST_GeometryType(geom) = 'ST_LineString'
                ORDER BY frechet ASC
                LIMIT {1}
            ", request.EmbeddingAtomId, request.K)
            .ToListAsync();
        
        return Ok(results);
    }
    
    // Hilbert range query (locality-preserving search)
    [HttpPost("hilbert-range")]
    public async Task<IActionResult> HilbertRange([FromBody] HilbertRangeRequest request)
    {
        var results = await _context.Atoms
            .Where(a => 
                a.HilbertHigh >= request.MinHigh && a.HilbertHigh <= request.MaxHigh &&
                a.HilbertLow >= request.MinLow && a.HilbertLow <= request.MaxLow)
            .Take(request.Limit ?? 100)
            .ToListAsync();
        
        return Ok(results);
    }
    
    // Connection strength (weight query)
    [HttpGet("weight-strength")]
    public async Task<IActionResult> WeightStrength([FromQuery] long inputAtomId, [FromQuery] long outputAtomId)
    {
        var strength = await _context.Database
            .SqlQuery<int>($@"
                SELECT COALESCE(SUM(multiplicities[2]), 0) as Value
                FROM atom
                WHERE NOT is_constant
                  AND array_length(refs, 1) = 2
                  AND refs[1] = {inputAtomId}
                  AND refs[2] = {outputAtomId}
            ")
            .FirstOrDefaultAsync();
        
        return Ok(new { strength });
    }
}

// ═══════════════════════════════════════════════════════════════
// RECONSTRUCTION CONTROLLER
// ═══════════════════════════════════════════════════════════════
[ApiController]
[Route("api/[controller]")]
public class ReconstructController : ControllerBase
{
    private readonly string _connInfo;
    
    // Reconstruct text from composition atom
    [HttpGet("text/{atomId}")]
    public IActionResult ReconstructText(long atomId)
    {
        HartonomousNative.reconstruct_text(
            atomId,
            out IntPtr textPtr,
            out nuint len,
            _connInfo
        );
        
        string text = Marshal.PtrToStringUTF8(textPtr, (int)len)!;
        Marshal.FreeHGlobal(textPtr);
        
        return Ok(new { text });
    }
    
    // Reconstruct embedding from LineString atom
    [HttpGet("embedding/{atomId}")]
    public IActionResult ReconstructEmbedding(long atomId)
    {
        HartonomousNative.reconstruct_embedding(
            atomId,
            out IntPtr valuesPtr,
            out nuint dims,
            _connInfo
        );
        
        float[] values = new float[dims];
        Marshal.Copy(valuesPtr, values, 0, (int)dims);
        Marshal.FreeHGlobal(valuesPtr);
        
        return Ok(new { values, dimensions = dims });
    }
}

// ═══════════════════════════════════════════════════════════════
// CPE CONTROLLER (Vocabulary Training)
// ═══════════════════════════════════════════════════════════════
[ApiController]
[Route("api/[controller]")]
public class CPEController : ControllerBase
{
    private readonly string _connInfo;
    
    // Train CPE vocabulary on corpus
    [HttpPost("train")]
    public IActionResult Train([FromBody] CPETrainRequest request)
    {
        long vocabId = HartonomousNative.cpe_train(
            request.Texts,
            (nuint)request.Texts.Length,
            (nuint)(request.VocabSize ?? 10000),
            (nuint)(request.MinFrequency ?? 2),
            _connInfo
        );
        
        return Ok(new { vocabId });
    }
}
```

---

### 5. Blazor UI Components (What Users Need to SEE)

#### 5.1 Hypersphere Visualizer

**3D/4D visualization of semantic space:**

```razor
@page "/visualize"
@using Three.Blazor

<h3>Semantic Space Explorer</h3>

<div class="controls">
    <label>Atom ID: <input @bind="targetAtomId" type="number" /></label>
    <button @onclick="LoadNeighborhood">Load K-NN</button>
    <label>K: <input @bind="k" type="number" value="100" /></label>
</div>

<Three.Blazor.Scene width="1200" height="800">
    @* Render atoms as points on hypersphere *@
    @foreach (var atom in atoms)
    {
        <Sphere position="@GetPosition(atom)" radius="0.01" color="@GetColor(atom)" />
    }
    
    @* Render connections as lines *@
    @foreach (var edge in edges)
    {
        <Line start="@GetPosition(edge.From)" end="@GetPosition(edge.To)" 
              thickness="@GetThickness(edge.Multiplicity)" />
    }
</Three.Blazor.Scene>

@code {
    // Project 4D → 3D for visualization
    // X,Y,Z visible, M encoded as color or pulsing
}
```

#### 5.2 Merkle DAG Explorer

**Tree view of composition hierarchies:**

```razor
@page "/dag/{AtomId:long}"

<h3>Merkle DAG: Atom @AtomId</h3>

<div class="dag-tree">
    <AtomNode Atom="@rootAtom" Depth="0" />
</div>

@code {
    // Recursive component showing atom → refs → leaf constants
    // Click to expand/collapse
    // Show content hash, geometry type, multiplicity
}
```

#### 5.3 Semantic Search Interface

**Natural language → spatial query:**

```razor
@page "/search"

<h3>Semantic Search</h3>

<textarea @bind="query" placeholder="Enter search query..." rows="3"></textarea>
<button @onclick="Search">Search</button>

<div class="results">
    @foreach (var result in results)
    {
        <div class="result-card">
            <h4>Atom @result.Id</h4>
            <p>Distance: @result.Distance</p>
            <p>Type: @result.GeometryType</p>
            <button @onclick="() => ViewInSpace(result.Id)">View in 3D</button>
            <button @onclick="() => Reconstruct(result.Id)">Reconstruct</button>
        </div>
    }
</div>

@code {
    // 1. Convert query text to atoms (CPE encode)
    // 2. Find k-NN to query atoms
    // 3. Show results with semantic distance
}
```

#### 5.4 Model Comparison Tool

**Visual diff between two AI models:**

```razor
@page "/compare"

<h3>Model Comparison</h3>

<div class="model-selector">
    <select @bind="model1Id">
        @* List of ingested models *@
    </select>
    <select @bind="model2Id">
        @* List of ingested models *@
    </select>
    <button @onclick="Compare">Compare</button>
</div>

<div class="comparison-viz">
    <svg width="1000" height="600">
        @* Venn diagram showing:
           - Shared atoms (overlap)
           - Unique to Model 1
           - Unique to Model 2
           - Connection strength differences
        *@
    </svg>
</div>

<div class="extraction">
    <button @onclick="ExtractDifference">Extract Difference as New Model</button>
    <p>Creates a new model atom containing only the differential</p>
</div>
```

#### 5.5 Ingestion Pipeline Monitor

**Real-time progress of content ingestion:**

```razor
@page "/ingest"

<h3>Content Ingestion</h3>

<div class="upload">
    <InputFile OnChange="HandleFileSelected" multiple />
    <button @onclick="StartIngestion">Start</button>
</div>

<div class="progress">
    @foreach (var job in ingestionJobs)
    {
        <div class="job-status">
            <h4>@job.Filename</h4>
            <progress value="@job.Progress" max="100"></progress>
            <p>Status: @job.Status</p>
            <p>Atoms created: @job.AtomsCreated</p>
            <p>Deduplication hits: @job.DedupHits</p>
        </div>
    }
</div>
```

---

### 6. MAUI App Features

**Desktop/mobile app needs:**

#### Offline Mode
- Local SQLite cache of frequently accessed atoms
- Sync with PostgreSQL when online
- Background sync queue

#### Camera Integration (Mobile)
- OCR → text ingestion
- Image capture → image atom creation
- QR codes for atom IDs

#### Voice Input
- Speech-to-text → text ingestion
- Audio recording → audio atom creation

#### Cross-Device Sync
- User profile with favorite atoms
- Bookmarked semantic regions
- Personal CPE vocabularies

---

### 7. Missing Infrastructure Components

#### 7.1 Background Job Queue

**For async ingestion:**

```csharp
public interface IIngestionQueue
{
    Task<Guid> EnqueueText(string text, long? cpeVocabId = null);
    Task<Guid> EnqueueModel(string modelPath, string format, double threshold);
    Task<Guid> EnqueueFile(string filePath, string mimeType);
    Task<IngestionStatus> GetStatus(Guid jobId);
}

// Use Hangfire, Quartz.NET, or Azure Service Bus
```

#### 7.2 CPE Vocabulary Management

**Store and version CPE vocabularies:**

```sql
CREATE TABLE cpe_vocabulary (
    id BIGSERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    version TEXT,
    vocab_size INT,
    min_frequency INT,
    merge_order BIGINT[][],  -- Array of [atom_a, atom_b] pairs
    created_at TIMESTAMPTZ DEFAULT NOW()
);
```

#### 7.3 Atom Metadata Cache

**For frequently accessed atoms:**

```csharp
public interface IAtomCache
{
    Task<Atom?> GetAsync(long atomId);
    Task SetAsync(long atomId, Atom atom, TimeSpan? expiry = null);
    Task<string?> GetReconstructedTextAsync(long atomId);
}

// Use Redis or in-memory cache
```

#### 7.4 Spatial Query Optimizer

**Pre-compute common queries:**

```sql
-- Materialized view of atom neighborhoods
CREATE MATERIALIZED VIEW atom_neighborhoods AS
SELECT 
    a.id as atom_id,
    ARRAY_AGG(b.id ORDER BY ST_Distance(a.geom, b.geom) LIMIT 100) as neighbors
FROM atom a
CROSS JOIN LATERAL (
    SELECT id FROM atom b
    WHERE b.id != a.id
    ORDER BY a.geom <-> b.geom
    LIMIT 100
) b
GROUP BY a.id;

CREATE INDEX ON atom_neighborhoods (atom_id);
```

#### 7.5 Hilbert Curve Pre-computation

**Seed the database with common Hilbert indices:**

```csharp
public static class HilbertSeeder
{
    public static async Task SeedCommonPoints(AtomContext context)
    {
        // Pre-compute Hilbert indices for grid points
        // Enables faster range queries
        
        for (double x = -1.0; x <= 1.0; x += 0.1)
        for (double y = -1.0; y <= 1.0; y += 0.1)
        for (double z = -1.0; z <= 1.0; z += 0.1)
        for (double m = -1.0; m <= 1.0; m += 0.1)
        {
            if (Math.Abs(x*x + y*y + z*z + m*m - 1.0) < 0.01)  // On sphere surface
            {
                HartonomousNative.coords_to_hilbert(x, y, z, m, out var high, out var low);
                // Store in lookup table
            }
        }
    }
}
```

---

### 8. Documentation & Developer Tools

#### 8.1 Interactive API Explorer

Beyond Swagger - show actual spatial operations:

```razor
@page "/api-explorer"

<h3>Spatial Query Builder</h3>

<div class="query-builder">
    <select @bind="operation">
        <option value="knn">K-Nearest Neighbors</option>
        <option value="frechet">Trajectory Similarity</option>
        <option value="intersects">Spatial Intersection</option>
        <option value="hilbert-range">Hilbert Range Scan</option>
    </select>
    
    @* Dynamic UI based on selected operation *@
    
    <button @onclick="ExecuteQuery">Execute</button>
</div>

<div class="results">
    <pre>@resultJson</pre>
    <SqlDisplay Query="@generatedSql" />
</div>
```

#### 8.2 Atom Inspector

**Debug tool for examining atom internals:**

```razor
@page "/inspect/{AtomId:long}"

<h3>Atom Inspector</h3>

<dl>
    <dt>ID:</dt><dd>@atom.Id</dd>
    <dt>Hilbert Index:</dt><dd>(@atom.HilbertHigh, @atom.HilbertLow)</dd>
    <dt>Content Hash:</dt><dd>@Convert.ToHexString(atom.ContentHash)</dd>
    <dt>Is Constant:</dt><dd>@atom.IsConstant</dd>
    <dt>Geometry Type:</dt><dd>@atom.Geom?.GeometryType</dd>
    <dt>Coordinates:</dt><dd>@GetCoordinates(atom.Geom)</dd>
    <dt>Refs:</dt><dd>[@string.Join(", ", atom.Refs ?? Array.Empty<long>())]</dd>
    <dt>Multiplicities:</dt><dd>[@string.Join(", ", atom.Multiplicities ?? Array.Empty<int>())]</dd>
</dl>

<h4>Reconstruct</h4>
<button @onclick="ReconstructText">As Text</button>
<button @onclick="ReconstructEmbedding">As Embedding</button>

<h4>Spatial Neighbors</h4>
<button @onclick="LoadNeighbors">Load K-NN (k=20)</button>
```

---

## SUMMARY: WHAT TO BUILD NEXT

### Immediate (Week 1)
1. **Delete the 17-table EF Core schema** - Replace with single `Atom` entity
2. **Implement C++ native library** - Landmark projection, Hilbert, BLAKE3
3. **Build P/Invoke wrapper** - C# → C++ bridge
4. **Create new migration** - Single `atom` table with correct schema
5. **Test basic ingestion** - Character constants, simple text

### Short-term (Month 1)
1. **CPE implementation** - Training + encoding/decoding
2. **Text ingestion pipeline** - Full hierarchy (chars → words → sentences → docs)
3. **Embedding ingestion** - Float array → LineString conversion
4. **Weight ingestion** - Sparse encoding with multiplicity
5. **Basic Blazor UI** - Atom inspector, simple search

### Medium-term (Quarter 1)
1. **Model parsers** - GGUF, SafeTensors, ONNX readers
2. **3D visualization** - Three.js/Babylon.js integration
3. **Advanced spatial queries** - Trajectory matching, convex hulls
4. **MAUI app** - Cross-platform with offline mode
5. **Performance tuning** - Bulk inserts, index optimization

### Long-term (Year 1)
1. **Multi-modal ingestion** - Images, video, audio
2. **Inference engine** - Graph traversal for forward passes
3. **Model training** - Update weights via spatial operations
4. **Production deployment** - Scaling, caching, monitoring
5. **Research tools** - Hypothesis testing, discovery UI

---

**The substrate is the revolution. Everything else is UI.**
