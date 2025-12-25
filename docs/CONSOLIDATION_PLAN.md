# Hart-MCP Database Consolidation Plan

## Current State (BROKEN)

### Two DbContexts
1. **HartDbContext** - Single `atom` table (correct architecture)
2. **SpatialKnowledgeContext** - 15 legacy tables (wrong, duplicates atom concept)

### The Problem
The legacy tables duplicate what atoms already represent:
- `SpatialNode` = An atom with geometry (DUPLICATE)
- `SpatialRelation` = A composition atom with refs (DUPLICATE)
- `KnowledgeCluster` = A composition with polygon geometry (DUPLICATE)
- `AIModel` = Should be a composition atom with AtomType="ai_model"
- `ModelLayer` = Should be atoms referenced by model atom
- `ConversationSession` = Should be a composition atom with AtomType="conversation"
- `ConversationTurn` = Should be atoms referenced by conversation
- etc.

### Controllers Using WRONG Context
| Controller | Uses SpatialKnowledgeContext | Should Use |
|------------|------------------------------|------------|
| SpatialNodesController | Yes (SpatialNode) | HartDbContext (Atom) |
| ConversationsController | Yes | HartDbContext (Atom) |
| ModelsController | Yes | HartDbContext (Atom) |
| IngestionAndAnalyticsController | Yes | HartDbContext (Atom) |
| VisualizationController | Yes | HartDbContext (Atom) |

### Service Using BOTH (Incoherent)
- **AIQueryService** injects both `SpatialKnowledgeContext` AND `HartDbContext`

---

## Target State (CORRECT)

### Single DbContext
**HartDbContext** with ONLY the `atom` table.

### Atom Table Schema (Already Correct)
```sql
CREATE TABLE atom (
    id BIGSERIAL PRIMARY KEY,
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,
    geom GEOMETRY(GeometryZM, 0) NOT NULL,
    is_constant BOOLEAN NOT NULL,
    seed_value BIGINT,
    seed_type INT,
    refs BIGINT[],
    multiplicities INT[],
    content_hash BYTEA UNIQUE NOT NULL,
    atom_type VARCHAR(32),
    metadata JSONB,  -- NEW: app-layer metadata
    created_at TIMESTAMPTZ DEFAULT NOW()
);
```

### How Legacy Entities Map to Atoms

| Legacy Entity | AtomType | Refs | Metadata (JSONB) |
|---------------|----------|------|------------------|
| SpatialNode | `"node"` | null (constant) | `{nodeType, merkleHash, parentHash}` |
| SpatialRelation | `"relation"` | [fromAtomId, toAtomId] | `{relationType, strength, confidence}` |
| KnowledgeCluster | `"cluster"` | [atomIds...] | `{clusterType, coherenceScore}` |
| AIModel | `"ai_model"` | [layerAtomIds...] | `{name, architecture, version, parameterCount}` |
| ModelLayer | `"model_layer"` | [nodeAtomIds...] | `{layerIndex, layerType, zCoordinate}` |
| ConversationSession | `"conversation"` | [turnAtomIds...] | `{sessionType, userId, startedAt}` |
| ConversationTurn | `"turn"` | [referencedAtomIds...] | `{role, content, turnNumber}` |
| ContentSource | `"content_source"` | [rootAtomId] | `{sourceType, uri, contentHash}` |
| VisualizationView | `"viz_view"` | null | `{viewType, projection, viewBox}` |
| SpatialBookmark | `"bookmark"` | [targetAtomIds...] | `{name, description}` |
| SpatialQuery | `"query"` | null | `{queryType, queryDefinition, cached}` |
| ModelComparison | `"model_comparison"` | [model1Id, model2Id] | `{comparisonType, similarityScore}` |
| ResearchAnnotation | `"annotation"` | [linkedAtomIds...] | `{title, annotationType}` |

---

## Changes Required

### 1. Add `metadata` Column to Atom Entity
**File:** `src/Hart.MCP.Core/Entities/Atom.cs`

Add:
```csharp
/// <summary>
/// Application-layer metadata as JSONB
/// Used for non-spatial properties like names, descriptions, etc.
/// </summary>
[Column("metadata", TypeName = "jsonb")]
public string? Metadata { get; set; }
```

### 2. Update HartDbContext
**File:** `src/Hart.MCP.Core/Data/HartDbContext.cs`

Add metadata column mapping and GIN index.

### 3. DELETE SpatialKnowledgeContext
**File:** `src/Hart.MCP.Core/Data/SpatialKnowledgeContext.cs`

DELETE ENTIRE FILE.

### 4. DELETE Legacy Entity Files
Delete:
- `src/Hart.MCP.Core/Entities/SpatialNode.cs`
- `src/Hart.MCP.Core/Entities/SpatialRelation.cs`
- `src/Hart.MCP.Core/Entities/KnowledgeCluster.cs`
- `src/Hart.MCP.Core/Entities/ContentSource.cs`
- `src/Hart.MCP.Core/Entities/ConversationSession.cs`
- `src/Hart.MCP.Core/Entities/AIModel.cs`
- `src/Hart.MCP.Core/Entities/AnalyticsEntities.cs`
- `src/Hart.MCP.Core/Entities/VisualizationEntities.cs`

### 5. Update Program.cs
**File:** `src/Hart.MCP.Api/Program.cs`

- Remove SpatialKnowledgeContext registration
- Keep only HartDbContext

### 6. Rewrite Controllers to Use Atoms
All controllers must use HartDbContext and work with atoms:
- SpatialNodesController → query/create atoms with AtomType
- ConversationsController → create composition atoms
- ModelsController → create AI model atoms with refs
- IngestionAndAnalyticsController → use AtomIngestionService
- VisualizationController → work with atom geometries

### 7. Update AIQueryService
**File:** `src/Hart.MCP.Core/Services/AIQueryService.cs`

Remove SpatialKnowledgeContext dependency, use only HartDbContext.

### 8. Create New Migration
Add metadata column migration.

### 9. Update Database Schema
**File:** `database/schema.sql`

Add metadata column.

---

## Test Requirements (TDD - Write Tests FIRST)

### Mathematical Invariants to Test
1. **Atom Uniqueness**: Same content_hash → same atom ID (deduplication)
2. **Hypersphere Constraint**: All constant atoms satisfy X² + Y² + Z² + M² = 1.0 ± ε
3. **Hilbert Bijection**: coords_to_hilbert(hilbert_to_coords(h)) = h
4. **Lossless Reconstruction**: reconstruct(ingest(text)) = text (byte-for-byte)
5. **Composition Integrity**: composition.refs all exist and are valid atom IDs
6. **Metadata Validity**: Metadata parses as valid JSON

### Behavioral Tests (What SHOULD Happen)
1. **Ingesting "Hello" twice** → Returns SAME composition ID (dedup)
2. **Finding neighbors of 'A'** → Returns atoms spatially near 'A', NOT random atoms
3. **AI Model ingestion** → Creates atom with AtomType="ai_model", refs to layer atoms
4. **Conversation creation** → Creates composition atom with turn atoms as refs
5. **Attention computation** → Weights sum to 1.0, closest atoms have highest weight

---

## Execution Order

1. Add metadata column to Atom entity
2. Update HartDbContext with metadata mapping
3. Delete SpatialKnowledgeContext
4. Delete legacy entity files
5. Update Program.cs to remove SpatialKnowledgeContext
6. Update AIQueryService to use only HartDbContext
7. Rewrite controllers one by one
8. Create migration for metadata column
9. Update schema.sql
10. Write proper tests
11. Build and verify
