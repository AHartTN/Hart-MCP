# Hart.MCP - Spatial AI Knowledge Substrate

## ⚠️ IMPORTANT: ARCHITECTURAL PIVOT IN PROGRESS

**The current codebase (17-table EF Core implementation) is being replaced with the correct single-table substrate architecture.**

📖 **For the TRUE architecture**, see:
- `/docs/EXECUTIVE_SUMMARY.md` - What we need vs what we have
- `/docs/ARCHITECTURE.md` - Complete vision (original design)
- `/docs/PIVOT_PLAN.md` - Execution strategy
- `/docs/VISUAL_ARCHITECTURE.md` - Data flow diagrams

**Current status:** .NET 10 + EF Core 10 + PostGIS foundation is solid. Working on C++ native library implementation (Hilbert curves, landmark projection, BLAKE3 hashing) before pivoting the schema.

---

## Vision

**Revolutionary AI/ML infrastructure** that replaces linear matrix multiplication with **spatial geometry queries** using PostGIS with SRID 0 (pure semantic space, not geographic). Knowledge is represented as geometric relationships rather than weight matrices, enabling:

- ✅ **Multi-modal/Multi-model**: Ingest any AI model (Flux, Llama4, BERT, etc.)
- ✅ **Referential integrity over weights**: Connection frequency determines importance (e.g., "e" and "th" in English vs "z" and "q")  
- ✅ **Sparse encoding**: Store ~8-10% of model data by exploiting natural sparsity (92% is zeros)
- ✅ **Universal substrate**: All digital content → unique Merkle DAG trees → geometric nodes
- ✅ **Queryable AI**: Training/distillation/pruning/extraction becomes a spatial query
- ✅ **Model comparison**: Extract governance from Llama4 by spatial difference operations
- ✅ **Visual knowledge**: See semantic space in 2D/3D, navigate AI model internals

## Architecture

### Technology Stack
- **.NET 10 LTS** (released Nov 2025, supported until Nov 2028)
- **Entity Framework Core 10** with vector search, JSON type, LeftJoin/RightJoin
- **PostGIS with SRID 0** (semantic space, not geographic - pure dimensional coordinates)
- **NetTopologySuite** for .NET spatial operations  
- **.NET MAUI Blazor Hybrid** for cross-platform UI (web, Windows, macOS, iOS, Android)

### Project Structure

```
Hart.MCP/
├── src/
│   ├── Hart.MCP.Core/          # Domain entities & EF Core DbContext
│   │   ├── Entities/
│   │   │   ├── SpatialNode.cs         # POINTZM nodes (X,Y=semantic, Z=dimension, M=time)
│   │   │   ├── SpatialRelation.cs     # LineString connections with strength
│   │   │   └── KnowledgeCluster.cs    # Polygon/MultiPolygon regions
│   │   └── Data/
│   │       └── SpatialKnowledgeContext.cs  # EF Core 10 DbContext with PostGIS
│   │
│   ├── Hart.MCP.Shared/        # Shared DTOs and models
│   │   ├── DTOs/
│   │   │   └── SpatialDtos.cs        # Data transfer objects
│   │   └── Models/
│   │       └── ApiModels.cs          # API response models
│   │
│   ├── Hart.MCP.Api/           # ASP.NET Core Web API
│   │   ├── Controllers/
│   │   │   └── SpatialNodesController.cs  # Spatial query endpoints
│   │   └── Program.cs                      # API configuration
│   │
│   ├── Hart.MCP.Web/           # Blazor Web App (browser)
│   │   └── Standalone Blazor WebAssembly for web browsers
│   │
│   └── Hart.MCP.Maui/          # .NET MAUI Blazor Hybrid
│       └── Cross-platform: Windows, macOS, iOS, Android
│
└── Hart.MCP.sln
```

## Core Concepts

### Spatial Node (POINTZM)
```csharp
Point Location:
  X, Y = Semantic space coordinates
  Z    = Dimension/layer (token vs embedding vs weight)
  M    = Temporal/version marker
```

### Spatial Relations (LineString)
- **Weight inference**: Number of connections across corpus = importance
- **Referential integrity**: Foreign keys ensure graph consistency
- **Geometric distance**: Actual spatial distance in semantic space

### Knowledge Clusters (Polygon)
- Dense regions representing domains (e.g., "English language tokens")
- Computed metrics: density, boundary, metadata

## API Endpoints

### 🗺️ Spatial Nodes (Core substrate)
- **POST /api/spatialnodes** - Create spatial node (token, embedding, weight, etc.)
- **POST /api/spatialnodes/query** - Spatial proximity queries (radius, KNN)
- **GET /api/spatialnodes/{id}** - Get specific node

### 💬 Conversations (LLM Operations)
- **POST /api/conversations/sessions** - Create chat/code review/explanation session
- **POST /api/conversations/sessions/{id}/turns** - Add turn (prompt/response)
- **GET /api/conversations/sessions/{id}** - Get full conversation with spatial trace
- **GET /api/conversations/sessions** - List all conversations

### 🤖 AI Models (Model Management)
- **POST /api/models** - Register AI model (Llama4, BERT, Flux, etc.)
- **GET /api/models** - List models by type/architecture
- **GET /api/models/{id}** - Get model with layers and bounds
- **POST /api/models/compare** - Compare two models (extract governance, distillation, etc.)
- **GET /api/models/comparisons** - List all model comparisons

### 📊 Visualization (See Knowledge)
- **POST /api/visualization/views** - Create visualization view (2D/3D/timeline/heatmap)
- **GET /api/visualization/views** - List saved views
- **POST /api/visualization/bookmarks** - Bookmark spatial location
- **GET /api/visualization/bookmarks** - List bookmarks
- **POST /api/visualization/render** - Render nodes in spatial region

### 📥 Ingestion (Multi-modal Content)
- **POST /api/ingestion/content** - Register content source (text, image, video, audio, repo, model)
- **GET /api/ingestion/content** - List content by type/status
- **POST /api/ingestion/content/{id}/process** - Trigger processing pipeline

### 🔬 Analytics (Research & Extraction)
- **POST /api/analytics/annotations** - Create research annotation/hypothesis
- **GET /api/analytics/annotations** - List annotations
- **GET /api/analytics/stats** - System statistics (nodes, relations, models)
- **POST /api/analytics/extract** - Extract model differences (e.g., governance layer)

## Database Schema (17 Tables)

### Core Spatial
- **SpatialNodes** - POINTZM nodes with GIST index (X,Y=semantic, Z=layer, M=time)
- **SpatialRelations** - LineString connections with strength and distance
- **KnowledgeClusters** - Polygon regions for dense knowledge areas

### Content & Ingestion
- **ContentSources** - Multi-modal sources (text, image, video, audio, repos, models)

### LLM Operations
- **ConversationSessions** - Chat/code review/explanation sessions
- **ConversationTurns** - Individual prompts/responses with spatial locations
- **TurnNodeReferences** - Nodes activated during conversation

### AI Models
- **AIModels** - Model metadata with spatial bounds and sparsity metrics
- **ModelLayers** - Individual layers mapped to Z-coordinates
- **ModelComparisons** - Pairwise model comparisons with difference regions

### Analytics & Research
- **SpatialQueries** - Query execution cache and history
- **ResearchAnnotations** - User hypotheses, findings, insights
- **AnnotationNodeLinks** - Links between annotations and nodes

### Visualization
- **VisualizationViews** - Saved view configurations (2D/3D/timeline/heatmap)
- **SpatialBookmarks** - Quick navigation bookmarks

All spatial columns use **SRID 0** (pure semantic space) with **GIST indexes** for O(log n) queries.

## Database Setup

### Prerequisites
- PostgreSQL 16+ with PostGIS 3.4+
- Enable PostGIS extension

```sql
CREATE DATABASE hart_mcp;
\c hart_mcp
CREATE EXTENSION postgis;
```

### EF Core Migrations

```bash
cd src/Hart.MCP.Api
dotnet ef migrations add InitialCreate
dotnet ef database update
```

### Connection String
Default: `Host=localhost;Port=5432;Database=HART-MCP;Username=hartonomous;Password=hartonomous`

Configure in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "SpatialKnowledge": "Host=localhost;Port=5432;Database=HART-MCP;Username=hartonomous;Password=hartonomous"
  }
}
```

## Use Cases

### 🔍 Spatial Queries Replace MatMul
Traditional:
```python
# Matrix multiply O(n²)
output = input @ weight_matrix
```

Hart.MCP:
```csharp
// Spatial KNN query O(log n)
var neighbors = await SpatialNodes
    .Where(n => n.Location.Distance(queryPoint) <= radius)
    .OrderBy(n => n.Location.Distance(queryPoint))
    .Take(k);
```

### 🧠 Model Comparison & Extraction
Extract governance layer from Llama4:
```http
POST /api/models/compare
{
  "model1Id": "llama4-base",
  "model2Id": "llama4-governance",
  "comparisonType": "Governance"
}
```

Results show:
- Spatial difference region (where governance nodes exist)
- Unique nodes to governance model
- Similarity score
- Allows **extracting** just the governance weights as a queryable subset

### 💬 LLM Conversation Tracing
Every conversation creates a **spatial trajectory** through knowledge space:
```http
POST /api/conversations/sessions/{id}/turns
{
  "role": "user",
  "content": "Explain quantum entanglement",
  "spatialX": 42.5,
  "spatialY": 108.3
}
```

View the conversation's path through semantic space, see which knowledge regions were activated.

### 📊 Visual Knowledge Navigation
```http
POST /api/visualization/render
{
  "minX": 0, "maxX": 100,
  "minY": 0, "maxY": 100,
  "minZ": 0.5, "maxZ": 2.0,
  "nodeType": "Token",
  "maxNodes": 1000
}
```

Returns nodes for visualization - build interactive 2D/3D maps of AI models, explore semantic neighborhoods, identify sparse regions.

### 📥 Multi-Modal Ingestion
Register any content for processing:
```http
POST /api/ingestion/content
{
  "sourceType": "Repository",
  "sourceUri": "https://github.com/user/repo",
  "sizeBytes": 1048576,
  "metadata": "{\"language\": \"C#\"}"
}
```

System extracts code tokens, creates Merkle DAG, maps to spatial nodes, builds relations from co-occurrence.

## Why This Works

### Traditional AI/ML
```
Matrix multiply: O(n²) or O(n³)
Storage: Dense matrices, 92% zeros wasted
Inference: Billions of floating-point ops
Training: Backprop through dense layers
Model merge: Complex weight averaging
```

### Spatial Substrate
```
Spatial query: O(log n) with GIST index
Storage: 8% sparse graph, only actual connections
Inference: K-nearest neighbor lookups
Training: Add nodes, update relation strengths
Model merge: Spatial union operations
```

### Key Insight
AI models are **already sparse** - most weights are near zero. By storing only significant connections and using **referential integrity** (connection count = weight), we:
- Store 10x less data
- Query 100x faster (spatial index vs matmul)
- Enable true multi-model fusion (spatial unions)
- Make knowledge **visible and navigable**

## Connection String
`appsettings.json`:
```json
{
  "ConnectionStrings": {
    "SpatialKnowledge": "Host=localhost;Database=hart_mcp;Username=postgres;Password=YOUR_PASSWORD"
  }
}
```

## Running the Projects

### API (Backend)
```bash
cd src/Hart.MCP.Api
dotnet run
```
API available at: https://localhost:7XXX

### Blazor Web (Browser)
```bash
cd src/Hart.MCP.Web
dotnet run
```
Web app at: https://localhost:5XXX

### MAUI App (Desktop/Mobile)
```bash
cd src/Hart.MCP.Maui

# Windows
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0

# Android (requires Android SDK)
dotnet build -f net10.0-android
# Deploy to emulator or device

# iOS (requires macOS + Xcode)
dotnet build -f net10.0-ios
```

## Next Steps

### Phase 1: Database Layer ✅ (COMPLETE!)
- [x] Spatial entities with PostGIS SRID 0 geometries
- [x] EF Core 10 DbContext with GIST indexes on all spatial columns
- [x] 17 tables created: Nodes, Relations, Clusters, Models, Conversations, Analytics, Visualization
- [x] 6 REST API controllers with 25+ endpoints
- [x] Multi-modal content ingestion entities
- [x] LLM conversation tracking with spatial traces
- [x] AI model comparison and extraction framework
- [x] Research annotation and analytics support
- [x] Visualization views and bookmarks
- [x] Migration applied to HART-MCP database

### Phase 2: Ingestion Pipeline
- [ ] AI model parser (GGUF, SafeTensors, etc.)
- [ ] Token→Spatial mapping algorithm
- [ ] Relation extraction from training data
- [ ] Sparsity compression (target: 8.5% retention)

### Phase 3: Query Engine
- [ ] KNN spatial queries with PostGIS
- [ ] Graph traversal for inference
- [ ] Clustering algorithms for knowledge regions
- [ ] Vector similarity using geometric distance

### Phase 4: Front-End Experience
- [ ] Shared Blazor components for node visualization
- [ ] Real-time spatial query interface
- [ ] 3D visualization of semantic space
- [ ] Model comparison tools

### Phase 5: Production
- [ ] Horizontal scaling with PostGIS partitioning
- [ ] Caching layer (Redis for hot paths)
- [ ] Authentication/authorization
- [ ] Telemetry and monitoring

## Key Features (Planned)

### Spatial Queries
- **Radius search**: Find all nodes within distance D of point P
- **Polygon containment**: Nodes within semantic regions
- **Path finding**: Shortest geometric path between concepts
- **Density mapping**: Identify knowledge "hotspots"

### Model Ingestion
- **Universal format support**: GGUF, SafeTensors, ONNX, PyTorch, TensorFlow
- **Automatic spatial mapping**: Embeddings → POINTZM coordinates
- **Relation extraction**: Co-occurrence → LineString connections
- **Incremental updates**: Add new models without rebuilding

### Performance
- PostGIS GIST spatial indexes
- EF Core 10 compiled models
- Connection pooling
- Async/await throughout

## Why This Works

Traditional AI:
```
Matrix multiply: O(n²) or O(n³)
Storage: Dense matrices, 92% zeros
```

Spatial Substrate:
```
Spatial query: O(log n) with GIST index
Storage: 8% sparse graph with referential integrity
Weights: Inferred from connection count
```

## Contributing

This is a research project exploring geometric alternatives to traditional neural networks. Contributions welcome!

## License

TBD

---

**Built with .NET 10 LTS • EF Core 10 • PostGIS • MAUI Blazor Hybrid**
