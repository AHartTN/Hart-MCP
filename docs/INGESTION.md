# Hartonomous Ingestion Specification

## Purpose

This document specifies how content flows from ingestion event to atomic storage, forming a Merkle DAG where:
- **Leaves** are constant atoms (characters, numbers)
- **Internal nodes** are composition atoms (n-grams, words, embeddings, weights)
- **The root** represents the ingested content (document, model, etc.)

The database IS the AI. Ingestion decomposes content into the universal structure. Query IS inference.

---

## Core Principles

### 1. Bottom-Up Construction

Merkle DAGs are built from leaves upward. You cannot create a parent until all children exist because the parent's content hash depends on child hashes.

```
Construction order:
  Characters → N-grams → Words → Sentences → Paragraphs → Document
                ↑
            CPE merges create intermediate compositions
```

### 2. Content Addressing

Every atom is identified by its content hash (BLAKE3-256). Same content = same hash = same atom ID everywhere. Automatic deduplication.

```
"the" in document A  ──┐
                       ├──→ Same atom ID
"the" in document B  ──┘
```

### 3. Sparse Encoding

AI model weights below threshold (0.5) represent noise, not signal. They are non-relationships. Do not store them.

```
Weight = 0.92 → Store connection (strong relationship)
Weight = 0.45 → Discard (below 0.5 threshold)
Weight = 0.03 → Discard (definitely noise)
```

### 4. Weights ARE Connection Counts

Weight values are not magical floating-point numbers. A weight is simply: "how strongly does A connect to B relative to other connections?"

In this substrate, weight magnitude becomes **edge multiplicity**:
- Normalize weights across layer
- Quantize to integer connection count
- Store as multiplicity on the [A, B] edge

```
Weight 0.95 → 95 connections (or normalized equivalent)
Weight 0.30 → 30 connections (or thresholded to 0 = no edge)

"Weight strength" = COUNT(*) or SUM(multiplicity)
```

This is referential integrity. Query weight strength with SQL aggregates, not floating-point comparison.

---

## Hierarchical Structure

### Text Content Hierarchy

```
Document (composition)
  └─ refs: [Paragraph₁, Paragraph₂, ...]

Paragraph (composition)
  └─ refs: [Sentence₁, Sentence₂, ...]

Sentence (composition)
  └─ refs: [Word₁, Punctuation, Word₂, ...]

Word (composition)
  └─ refs: [CPE_Token₁, CPE_Token₂, ...]  (or character atoms directly)

CPE Token (composition from training)
  └─ refs: [SubToken₁, SubToken₂]  (recursive until characters)

Character (constant)
  └─ geom: PointZM on hypersphere
  └─ refs: NULL
```

### AI Model Hierarchy

```
Model (composition)
  └─ refs: [VocabAtom, EmbeddingLayer, Layer₁, Layer₂, ...]

Layer (composition)
  └─ refs: [AttentionBlock, MLPBlock]

AttentionBlock (composition)
  └─ refs: [Q_weights, K_weights, V_weights, O_weights]

WeightMatrix (sparse edges in atom table)
  └─ Many 2-point edges [InputAtom, OutputAtom] with multiplicity
  └─ NO weight value atoms - magnitude IS the connection count
  └─ Edges ARE compositions (is_constant = FALSE)

EmbeddingLayer (composition)
  └─ refs: [Embedding₁, Embedding₂, ...] (one per token)

Embedding (IS a LineString)
  └─ geom: LineString ZM (N points, one per dimension)
  └─ X = embedding value, Y = i/N normalized, Z = 0, M = 0
  └─ refs: [TokenAtom]

Token (composition or constant)
  └─ If "cat": refs: [c_atom, a_atom, t_atom]
  └─ If "a": IS the character constant (no separate composition)
```

---

## Content Pair Encoding (CPE)

CPE is our adaptation of [BPE](https://github.com/karpathy/minbpe) for atom-based tokenization. Instead of creating byte sequences, we create composition atoms in a [Merkle DAG](https://docs.ipfs.tech/concepts/merkle-dag/).

### Training Algorithm

```cpp
struct CPEVocabulary {
    // (atom_a, atom_b) → merged_atom_id
    std::unordered_map<std::pair<int64_t, int64_t>, int64_t, PairHash> merges;

    // merged_atom_id → (atom_a, atom_b)
    std::unordered_map<int64_t, std::pair<int64_t, int64_t>> splits;

    // Order of merges (critical for deterministic encoding)
    std::vector<std::pair<int64_t, int64_t>> merge_order;
};

CPEVocabulary cpe_train(
    AtomStore& store,
    const std::vector<std::string>& corpus,
    size_t vocab_size,
    size_t min_frequency
) {
    CPEVocabulary vocab;

    // Step 1: Convert all corpus text to character atom sequences
    std::vector<std::vector<int64_t>> sequences;
    for (const auto& text : corpus) {
        std::vector<int64_t> seq;
        for (uint32_t cp : utf8_to_codepoints(text)) {
            seq.push_back(store.get_or_create_character_constant(cp));
        }
        sequences.push_back(std::move(seq));
    }

    // Step 2: Iteratively merge most frequent pairs
    while (vocab.merge_order.size() < vocab_size) {

        // Count all adjacent pairs across all sequences
        std::unordered_map<std::pair<int64_t, int64_t>, size_t, PairHash> pair_counts;
        for (const auto& seq : sequences) {
            for (size_t i = 0; i + 1 < seq.size(); i++) {
                pair_counts[{seq[i], seq[i + 1]}]++;
            }
        }

        // Find most frequent pair
        auto best_it = std::max_element(
            pair_counts.begin(), pair_counts.end(),
            [](const auto& a, const auto& b) { return a.second < b.second; }
        );

        if (best_it == pair_counts.end() || best_it->second < min_frequency) {
            break;  // No more frequent pairs
        }

        auto [atom_a, atom_b] = best_it->first;

        // Create composition atom for this pair
        // This is the Merkle DAG node creation
        int64_t merged_atom = store.create_composition(
            /*refs=*/{atom_a, atom_b},
            /*multiplicities=*/{1, 1}
        );

        // Record merge
        vocab.merges[{atom_a, atom_b}] = merged_atom;
        vocab.splits[merged_atom] = {atom_a, atom_b};
        vocab.merge_order.push_back({atom_a, atom_b});

        // Replace all occurrences in sequences
        for (auto& seq : sequences) {
            std::vector<int64_t> new_seq;
            for (size_t i = 0; i < seq.size(); i++) {
                if (i + 1 < seq.size() && seq[i] == atom_a && seq[i + 1] == atom_b) {
                    new_seq.push_back(merged_atom);
                    i++;  // Skip next
                } else {
                    new_seq.push_back(seq[i]);
                }
            }
            seq = std::move(new_seq);
        }
    }

    return vocab;
}
```

### Encoding (Text to Atoms)

```cpp
std::vector<int64_t> cpe_encode(
    AtomStore& store,
    const std::string& text,
    const CPEVocabulary& vocab
) {
    // Start with character atoms
    std::vector<int64_t> atoms;
    for (uint32_t cp : utf8_to_codepoints(text)) {
        atoms.push_back(store.get_or_create_character_constant(cp));
    }

    // Apply merges in training order (deterministic)
    for (const auto& [atom_a, atom_b] : vocab.merge_order) {
        int64_t merged = vocab.merges.at({atom_a, atom_b});

        std::vector<int64_t> new_atoms;
        for (size_t i = 0; i < atoms.size(); i++) {
            if (i + 1 < atoms.size() && atoms[i] == atom_a && atoms[i + 1] == atom_b) {
                new_atoms.push_back(merged);
                i++;
            } else {
                new_atoms.push_back(atoms[i]);
            }
        }
        atoms = std::move(new_atoms);
    }

    return atoms;
}
```

### Decoding (Atoms to Text)

```cpp
std::string cpe_decode(
    AtomStore& store,
    const std::vector<int64_t>& atoms,
    const CPEVocabulary& vocab
) {
    // Recursively expand compositions to character constants
    std::vector<int64_t> expanded;

    std::function<void(int64_t)> expand = [&](int64_t atom_id) {
        auto it = vocab.splits.find(atom_id);
        if (it != vocab.splits.end()) {
            // It's a merged composition - expand recursively
            expand(it->second.first);
            expand(it->second.second);
        } else {
            // It's a leaf (character constant)
            expanded.push_back(atom_id);
        }
    };

    for (int64_t atom : atoms) {
        expand(atom);
    }

    // Convert character atom IDs back to codepoints
    std::string result;
    for (int64_t char_atom : expanded) {
        uint32_t codepoint = store.get_codepoint(char_atom);
        append_utf8(result, codepoint);
    }

    return result;
}
```

### The Merkle DAG Structure

Each CPE merge creates a DAG node:

```
Example: "the" appears frequently

Initial: [t_id, h_id, e_id]

After merge (t,h) → th_id:
  th_id (composition)
    └─ refs: [t_id, h_id]
    └─ content_hash: BLAKE3(hash(t_id) || 1 || hash(h_id) || 1)

After merge (th,e) → the_id:
  the_id (composition)
    └─ refs: [th_id, e_id]
    └─ content_hash: BLAKE3(hash(th_id) || 1 || hash(e_id) || 1)

DAG structure:
     the_id
     ├── th_id
     │   ├── t_id (constant)
     │   └── h_id (constant)
     └── e_id (constant)
```

---

## Run-Length Encoding (RLE)

Applied BEFORE CPE to compress repeated atoms:

```cpp
struct RLESequence {
    std::vector<int64_t> refs;
    std::vector<int32_t> multiplicities;
};

RLESequence rle_encode(const std::vector<int64_t>& atoms) {
    RLESequence result;
    if (atoms.empty()) return result;

    int64_t current = atoms[0];
    int32_t count = 1;

    for (size_t i = 1; i < atoms.size(); i++) {
        if (atoms[i] == current) {
            count++;
        } else {
            result.refs.push_back(current);
            result.multiplicities.push_back(count);
            current = atoms[i];
            count = 1;
        }
    }
    result.refs.push_back(current);
    result.multiplicities.push_back(count);

    return result;
}

// "Hellooooo" = [H, e, l, l, o, o, o, o, o]
// RLE encoded: refs=[H, e, l, o], mults=[1, 1, 2, 5]
// Storage: 4 refs instead of 9
```

---

## Full Text Ingestion Pipeline

### C++ Side (Heavy Lifting)

```cpp
int64_t ingest_text(
    AtomStore& store,
    const std::string& text,
    const CPEVocabulary& cpe_vocab
) {
    // ═══════════════════════════════════════════════════════════════
    // PHASE 1: Character Atomization
    // ═══════════════════════════════════════════════════════════════

    std::vector<uint32_t> codepoints = utf8_to_codepoints(text);
    std::vector<int64_t> char_atoms;
    char_atoms.reserve(codepoints.size());

    for (uint32_t cp : codepoints) {
        // Compute hypersphere position
        PointZM coords = landmark_project_character(cp);

        // Compute Hilbert index
        HilbertIndex h = coords_to_hilbert(coords.x, coords.y, coords.z, coords.m);

        // Compute content hash
        ContentHash hash = hash_constant(coords);

        // Upsert constant atom (deduplication automatic)
        int64_t atom_id = store.upsert_constant(h, coords, hash);
        char_atoms.push_back(atom_id);
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 2: Run-Length Encoding
    // ═══════════════════════════════════════════════════════════════

    RLESequence rle = rle_encode(char_atoms);

    // ═══════════════════════════════════════════════════════════════
    // PHASE 3: Content Pair Encoding
    // ═══════════════════════════════════════════════════════════════

    std::vector<int64_t> cpe_atoms = cpe_apply(rle.refs, cpe_vocab);

    // ═══════════════════════════════════════════════════════════════
    // PHASE 4: Hierarchical Composition Building
    // ═══════════════════════════════════════════════════════════════

    // 4a: Build word compositions
    std::vector<WordBoundary> word_bounds = find_word_boundaries(text);
    std::vector<int64_t> word_atoms;

    size_t atom_idx = 0;
    for (const auto& wb : word_bounds) {
        std::vector<int64_t> word_refs;
        std::vector<int32_t> word_mults;

        // Collect atoms for this word (respecting RLE)
        size_t chars_needed = wb.end - wb.start;
        size_t chars_collected = 0;

        while (chars_collected < chars_needed && atom_idx < rle.refs.size()) {
            word_refs.push_back(cpe_atoms[atom_idx]);
            word_mults.push_back(rle.multiplicities[atom_idx]);
            chars_collected += rle.multiplicities[atom_idx];
            atom_idx++;
        }

        if (word_refs.size() == 1 && word_mults[0] == 1) {
            // Single atom word (e.g., "a", "I") - use atom directly
            word_atoms.push_back(word_refs[0]);
        } else {
            // Multi-atom word - create composition
            int64_t word_atom = store.create_composition(word_refs, word_mults);
            word_atoms.push_back(word_atom);
        }
    }

    // 4b: Build sentence compositions
    std::vector<SentenceBoundary> sent_bounds = find_sentence_boundaries(text);
    std::vector<int64_t> sentence_atoms;

    size_t word_idx = 0;
    for (const auto& sb : sent_bounds) {
        std::vector<int64_t> sent_refs;
        // Collect words for this sentence
        while (word_idx < word_atoms.size() && /* word in sentence bounds */) {
            sent_refs.push_back(word_atoms[word_idx++]);
        }

        int64_t sent_atom = store.create_composition(sent_refs);
        sentence_atoms.push_back(sent_atom);
    }

    // 4c: Build paragraph compositions
    std::vector<int64_t> para_atoms = build_paragraphs(store, sentence_atoms, text);

    // 4d: Build document composition (root of Merkle DAG)
    int64_t document_atom = store.create_composition(para_atoms);

    return document_atom;
}
```

### SQL Side (Set Operations)

```sql
-- Upsert constant atom
INSERT INTO atom (hilbert_high, hilbert_low, geom, is_constant, refs, multiplicities, content_hash)
VALUES ($1, $2, ST_SetSRID(ST_MakePoint($3, $4, $5, $6), 0), TRUE, NULL, NULL, $7)
ON CONFLICT (content_hash) DO UPDATE SET id = atom.id
RETURNING id;

-- Upsert composition atom
INSERT INTO atom (hilbert_high, hilbert_low, geom, is_constant, refs, multiplicities, content_hash)
VALUES ($1, $2, $3, FALSE, $4, $5, $6)
ON CONFLICT (content_hash) DO UPDATE SET id = atom.id
RETURNING id;

-- Batch insert for performance (PostgreSQL COPY or multi-row INSERT)
INSERT INTO atom (hilbert_high, hilbert_low, geom, is_constant, refs, multiplicities, content_hash)
SELECT * FROM unnest($1::bigint[], $2::bigint[], $3::geometry[], $4::boolean[], $5::bigint[][], $6::int[][], $7::bytea[])
ON CONFLICT (content_hash) DO NOTHING;
```

---

## AI Model Ingestion

### Overview

AI models consist of:
1. **Vocabulary** - Token strings → Atomized via CPE
2. **Embeddings** - N-dimensional vectors → LineStrings
3. **Weights** - Matrices connecting layers → Sparse 3-point LineStrings

### Vocabulary Ingestion

```cpp
std::unordered_map<size_t, int64_t> ingest_vocabulary(
    AtomStore& store,
    const ModelVocabulary& vocab
) {
    std::unordered_map<size_t, int64_t> token_to_atom;

    for (size_t i = 0; i < vocab.size(); i++) {
        std::string token = vocab.get_token(i);
        std::vector<uint32_t> codepoints = utf8_to_codepoints(token);

        if (codepoints.size() == 1) {
            // Single character token = character constant (NO separate composition)
            int64_t atom_id = store.get_or_create_character_constant(codepoints[0]);
            token_to_atom[i] = atom_id;
        } else {
            // Multi-character token = composition
            std::vector<int64_t> char_atoms;
            for (uint32_t cp : codepoints) {
                char_atoms.push_back(store.get_or_create_character_constant(cp));
            }

            // Apply RLE
            RLESequence rle = rle_encode(char_atoms);

            // Create token composition
            int64_t token_atom = store.create_composition(rle.refs, rle.multiplicities);
            token_to_atom[i] = token_atom;
        }
    }

    return token_to_atom;
}
```

### Embedding Ingestion

An embedding IS a LineString. Not "stored as" - it IS one.

```
768 floats → 768 points → LineString ZM
```

Each dimension becomes one point. The trajectory IS the meaning.

**Point Coordinate Scheme**:
Each dimension becomes one point ON the hypersphere surface (satisfies X² + Y² + Z² + M² = R²):

```cpp
constexpr double R = 1.0;
constexpr double PI = 3.14159265358979323846;

PointZM embedding_dim_to_point(double value, size_t dim_index, size_t total_dims) {
    double theta = 2.0 * PI * (static_cast<double>(dim_index) / total_dims);
    double phi = PI * sigmoid(value);  // Maps value to [0, π]
    
    PointZM p;
    p.x = R * sin(phi) * cos(theta);
    p.y = R * sin(phi) * sin(theta);
    p.z = R * cos(phi);
    p.m = R * sin(phi) * (value >= 0 ? 1 : -1) * std::abs(cos(theta) - sin(theta));
    
    return p;
}

int64_t ingest_embedding(
    AtomStore& store,
    int64_t token_atom_id,
    const std::vector<float>& embedding
) {
    // The embedding IS an N-point LineString (one point per dimension)
    std::ostringstream wkt;
    wkt << std::setprecision(17) << "LINESTRING ZM(";

    size_t N = embedding.size();
    for (size_t i = 0; i < N; i++) {
        PointZM p = embedding_dim_to_point(embedding[i], i, N);

        if (i > 0) wkt << ", ";
        wkt << p.x << " " << p.y << " " << p.z << " " << p.m;
    }
    wkt << ")";

    // Hilbert index from centroid (similar embeddings → similar centroids)
    PointZM centroid = compute_centroid(wkt.str());  // or use ST_Centroid after insert
    HilbertIndex h = coords_to_hilbert(centroid);

    ContentHash hash = hash_bytes(embedding.data(), embedding.size() * sizeof(float));

    return store.create_embedding_composition(h, wkt.str(), {token_atom_id}, hash);
}

// 768-dimensional embedding = 768-point trajectory
// Embedding similarity = trajectory similarity (ST_HausdorffDistance, ST_FrechetDistance)
```

### Weight Matrix Ingestion (Sparse Encoding)

**Weights are connection counts, not value atoms.**

A weight value is just "how strongly A connects to B relative to others." In this substrate:
- Normalize weights across the layer
- Quantize to integer multiplicity
- Store as 2-point edge [A, B] with multiplicity
- Discard below threshold (no edge = no relationship)

```cpp
constexpr double WEIGHT_THRESHOLD = 0.5;  // 50% threshold - below = noise
constexpr int32_t MAX_MULTIPLICITY = 100; // Normalized max connection count

void ingest_weight_matrix(
    AtomStore& store,
    const float* weights,
    size_t rows,              // Input dimension
    size_t cols,              // Output dimension
    const std::vector<int64_t>& input_atoms,   // Row atom IDs
    const std::vector<int64_t>& output_atoms   // Column atom IDs
) {
    // ═══════════════════════════════════════════════════════════════
    // STEP 1: Find normalization bounds
    // ═══════════════════════════════════════════════════════════════
    float max_abs = 0.0f;
    for (size_t i = 0; i < rows * cols; i++) {
        max_abs = std::max(max_abs, std::abs(weights[i]));
    }

    size_t stored = 0, discarded = 0;

    for (size_t i = 0; i < rows; i++) {
        for (size_t j = 0; j < cols; j++) {
            float w = weights[i * cols + j];
            float normalized = std::abs(w) / max_abs;  // [0, 1]

            // ═══════════════════════════════════════════════════════════
            // SPARSE ENCODING: Below threshold = no relationship
            // ═══════════════════════════════════════════════════════════
            if (normalized < WEIGHT_THRESHOLD) {
                discarded++;
                continue;  // Edge does not exist
            }

            // ═══════════════════════════════════════════════════════════
            // QUANTIZE: Weight magnitude → connection count (multiplicity)
            // ═══════════════════════════════════════════════════════════
            int32_t multiplicity = static_cast<int32_t>(
                normalized * MAX_MULTIPLICITY
            );
            multiplicity = std::max(1, multiplicity);  // At least 1 if above threshold

            stored++;

            // Store edge: refs only, no geometry (computed on demand)
            // Negative weights = spatial distance encodes repulsion
            store.create_edge(input_atoms[i], output_atoms[j], multiplicity);
        }
    }

    // Typical: 70-90% discarded
    // Weight strength = SUM(multiplicities[2]) in queries
}
```

**Weight strength IS referential integrity:**

```sql
-- "How strongly does A connect to B?"
SELECT SUM(multiplicities[2]) as strength
FROM atom
WHERE is_constant = FALSE
  AND array_length(refs, 1) = 2
  AND refs[1] = $A AND refs[2] = $B;

-- "What are A's strongest connections?"
SELECT refs[2] as output_atom, SUM(multiplicities[2]) as strength
FROM atom
WHERE is_constant = FALSE
  AND array_length(refs, 1) = 2
  AND refs[1] = $A
GROUP BY refs[2]
ORDER BY strength DESC;

-- Compute edge geometry on demand
SELECT ST_MakeLine(a.geom, b.geom)
FROM atom e
JOIN atom a ON a.id = e.refs[1]
JOIN atom b ON b.id = e.refs[2]
WHERE e.id = $edge_id;
```

**Negative weights = spatial distance**: Atoms that repel (negative weight in original model) are far apart on the hypersphere. Atoms that attract (positive weight) are close. The distance between atom positions encodes relationship type. Multiplicity encodes magnitude regardless of sign.

### Why This Works

Research on [neural network pruning](https://datature.io/blog/a-comprehensive-guide-to-neural-network-model-pruning) shows:
- Weights below 0.5 magnitude contribute negligibly to model behavior
- [Magnitude-based pruning](https://intellabs.github.io/distiller/pruning.html) can remove 80-90% of weights with minimal accuracy loss
- **The topology of connections carries the semantics**, not the exact float values

In this substrate:
- **Edge exists** = meaningful relationship
- **Edge absent** = no relationship (noise was discarded)
- **Edge multiplicity** = relative connection strength
- **Semantics emerge from structure** - which atoms connect to which, not random floats

---

## Complete Model Ingestion

```cpp
int64_t ingest_model(
    AtomStore& store,
    const std::string& model_path,
    double weight_threshold
) {
    // Load model (GGUF, safetensors, etc.)
    ModelFile model(model_path);

    std::vector<int64_t> model_components;

    // ═══════════════════════════════════════════════════════════════
    // 1. VOCABULARY
    // ═══════════════════════════════════════════════════════════════
    auto token_map = ingest_vocabulary(store, model.vocabulary());

    // ═══════════════════════════════════════════════════════════════
    // 2. EMBEDDINGS
    // ═══════════════════════════════════════════════════════════════
    std::vector<int64_t> embedding_atoms;
    for (size_t i = 0; i < model.vocab_size(); i++) {
        std::vector<float> emb = model.get_embedding(i);
        int64_t emb_atom = ingest_embedding(store, token_map[i], emb);
        embedding_atoms.push_back(emb_atom);
    }

    int64_t embedding_layer = store.create_composition(embedding_atoms);
    model_components.push_back(embedding_layer);

    // ═══════════════════════════════════════════════════════════════
    // 3. TRANSFORMER LAYERS
    // ═══════════════════════════════════════════════════════════════
    for (size_t layer = 0; layer < model.num_layers(); layer++) {
        std::vector<int64_t> layer_components;

        // Attention block (Q, K, V, O weight matrices)
        // Each matrix is [hidden_dim, hidden_dim] or similar
        auto q_weights = model.get_tensor(layer, "q_proj");
        auto k_weights = model.get_tensor(layer, "k_proj");
        auto v_weights = model.get_tensor(layer, "v_proj");
        auto o_weights = model.get_tensor(layer, "o_proj");

        // Ingest each weight matrix with sparse encoding
        ingest_weight_matrix(store, q_weights.data(), q_weights.rows(), q_weights.cols(),
                             get_input_atoms(layer), get_output_atoms(layer));
        // ... same for K, V, O

        // MLP block (gate_proj, up_proj, down_proj)
        // These are the largest matrices: [hidden_dim, intermediate_dim]
        auto gate_weights = model.get_tensor(layer, "gate_proj");
        auto up_weights = model.get_tensor(layer, "up_proj");
        auto down_weights = model.get_tensor(layer, "down_proj");

        ingest_weight_matrix(store, gate_weights.data(), ...);
        // ... same for up, down
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. MODEL ROOT COMPOSITION
    // ═══════════════════════════════════════════════════════════════
    int64_t model_atom = store.create_composition(model_components);

    return model_atom;
}
```

---

## Query Patterns

Spatial operations ARE semantic operations:
- `ST_Distance` = similarity
- `ST_Intersects` = relatedness
- `ST_FrechetDistance` = trajectory similarity
- `ST_ConvexHull` = semantic boundary
- `COUNT(*)` / `SUM(multiplicity)` = connection strength

### SQL: Set Operations & Spatial Queries

```sql
-- ═══════════════════════════════════════════════════════════════
-- RECONSTRUCT TEXT FROM COMPOSITION (Merkle DAG traversal)
-- ═══════════════════════════════════════════════════════════════
WITH RECURSIVE leaf_atoms AS (
    SELECT id, refs, multiplicities, is_constant, 0 as depth, ARRAY[id] as path
    FROM atom
    WHERE id = $document_id

    UNION ALL

    SELECT a.id, a.refs, a.multiplicities, a.is_constant, la.depth + 1, la.path || a.id
    FROM atom a
    JOIN leaf_atoms la ON a.id = ANY(la.refs)
    WHERE a.is_constant = FALSE
)
SELECT id, geom
FROM leaf_atoms
WHERE is_constant = TRUE
ORDER BY path;

-- ═══════════════════════════════════════════════════════════════
-- SEMANTIC NEIGHBORS (KNN on hypersphere)
-- ═══════════════════════════════════════════════════════════════
SELECT b.id, ST_Distance(a.geom, b.geom) as semantic_distance
FROM atom a, atom b
WHERE a.id = $target_id
  AND b.id != a.id
  AND b.is_constant = TRUE
ORDER BY a.geom <-> b.geom  -- GiST index KNN operator
LIMIT $k;

-- ═══════════════════════════════════════════════════════════════
-- EMBEDDING SIMILARITY (Trajectory comparison)
-- ═══════════════════════════════════════════════════════════════
SELECT b.id,
       b.refs[1] as token_atom,
       ST_FrechetDistance(a.geom, b.geom) as trajectory_distance,
       ST_HausdorffDistance(a.geom, b.geom) as hausdorff_distance
FROM atom a, atom b
WHERE a.id = $embedding_id
  AND b.is_constant = FALSE
  AND ST_GeometryType(b.geom) = 'ST_LineString'
ORDER BY trajectory_distance ASC
LIMIT $k;

-- ═══════════════════════════════════════════════════════════════
-- CONNECTION STRENGTH (Weight = multiplicity count)
-- ═══════════════════════════════════════════════════════════════
SELECT output_atom, SUM(multiplicity) as strength
FROM edge
WHERE input_atom = $input_atom_id
GROUP BY output_atom
ORDER BY strength DESC;

-- ═══════════════════════════════════════════════════════════════
-- FORWARD PROPAGATION (A* / Dijkstra on edge graph)
-- ═══════════════════════════════════════════════════════════════
-- This is where heavy computation moves to C++
-- SQL defines the graph, C++ traverses it
SELECT e.input_atom, e.output_atom, e.multiplicity
FROM edge e
WHERE e.input_atom = ANY($active_atoms)
ORDER BY e.multiplicity DESC;

-- ═══════════════════════════════════════════════════════════════
-- SEMANTIC CLUSTERS (ConvexHull grouping)
-- ═══════════════════════════════════════════════════════════════
SELECT ST_ConvexHull(ST_Collect(geom)) as semantic_boundary,
       COUNT(*) as cluster_size
FROM atom
WHERE is_constant = TRUE
  AND hilbert_low BETWEEN $range_start AND $range_end
GROUP BY (hilbert_low >> 16);  -- Group by Hilbert region

-- ═══════════════════════════════════════════════════════════════
-- TRAJECTORY INTERSECTION (Related embeddings)
-- ═══════════════════════════════════════════════════════════════
SELECT b.id, b.refs[1] as token_atom
FROM atom a, atom b
WHERE a.id = $embedding_id
  AND b.id != a.id
  AND ST_Intersects(a.geom, b.geom)
  AND ST_GeometryType(b.geom) = 'ST_LineString';
```

### C++: RBAR Operations

Heavy lifting (loops, cursors, row-by-row processing) happens in C++:

```cpp
// ═══════════════════════════════════════════════════════════════
// BATCH CHARACTER ATOMIZATION
// ═══════════════════════════════════════════════════════════════
std::vector<int64_t> atomize_text_batch(
    AtomStore& store,
    const std::vector<std::string>& texts
) {
    std::vector<int64_t> document_atoms;

    // Process all texts, building atoms in memory first
    std::vector<AtomData> pending_constants;
    std::vector<AtomData> pending_compositions;

    for (const auto& text : texts) {
        // ... build atoms in memory
    }

    // Bulk insert to PostgreSQL (COPY protocol)
    store.bulk_insert_constants(pending_constants);
    store.bulk_insert_compositions(pending_compositions);

    return document_atoms;
}

// ═══════════════════════════════════════════════════════════════
// STREAMING WEIGHT MATRIX PROCESSING
// ═══════════════════════════════════════════════════════════════
void stream_weight_ingestion(
    AtomStore& store,
    const std::string& tensor_path,
    double threshold
) {
    // Memory-map large tensor file
    MappedTensor tensor(tensor_path);

    // Process in chunks to avoid memory explosion
    constexpr size_t CHUNK_SIZE = 10000;

    for (size_t row = 0; row < tensor.rows(); row++) {
        std::vector<WeightEdge> edges;

        for (size_t col = 0; col < tensor.cols(); col++) {
            float w = tensor.at(row, col);
            if (std::abs(w) >= threshold) {
                edges.push_back({row, col, w});
            }
        }

        // Batch insert edges for this row
        if (!edges.empty()) {
            store.batch_insert_weight_edges(edges);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// RECONSTRUCTION (C++ side traversal)
// ═══════════════════════════════════════════════════════════════
std::string reconstruct_text(AtomStore& store, int64_t composition_id) {
    std::vector<int64_t> leaf_atoms;

    // Stack-based traversal (avoid recursion for deep trees)
    std::stack<std::pair<int64_t, int32_t>> work;  // (atom_id, remaining_mult)
    work.push({composition_id, 1});

    while (!work.empty()) {
        auto [atom_id, mult] = work.top();
        work.pop();

        Atom atom = store.get_atom(atom_id);

        if (atom.is_constant) {
            // Leaf - add mult times
            for (int32_t i = 0; i < mult; i++) {
                leaf_atoms.push_back(atom_id);
            }
        } else {
            // Composition - push children in reverse order
            for (int i = atom.refs.size() - 1; i >= 0; i--) {
                work.push({atom.refs[i], atom.multiplicities[i]});
            }
        }
    }

    // Convert atom IDs to codepoints
    std::string result;
    for (int64_t atom_id : leaf_atoms) {
        uint32_t cp = store.get_codepoint(atom_id);
        append_utf8(result, cp);
    }

    return result;
}
```

---

## Performance Optimization

### Batch Processing

```cpp
// Use PostgreSQL COPY for bulk inserts
void AtomStore::bulk_insert_constants(const std::vector<AtomData>& atoms) {
    PQexec(conn, "COPY atom (hilbert_high, hilbert_low, geom, is_constant, content_hash) "
                 "FROM STDIN WITH (FORMAT binary)");

    for (const auto& atom : atoms) {
        // Write binary data
        PQputCopyData(conn, atom.to_binary().data(), atom.to_binary().size());
    }

    PQputCopyEnd(conn, nullptr);
}
```

### Index Management

```sql
-- Disable indexes during bulk load
ALTER INDEX idx_atom_geom SET (fastupdate = off);
ALTER INDEX idx_atom_hilbert SET (fastupdate = off);

-- Re-enable after
REINDEX INDEX CONCURRENTLY idx_atom_geom;
REINDEX INDEX CONCURRENTLY idx_atom_hilbert;
```

### Parallel Processing

```cpp
// Use thread pool for independent atom creation
void parallel_atomize(
    AtomStore& store,
    const std::vector<std::string>& texts
) {
    ThreadPool pool(std::thread::hardware_concurrency());
    std::vector<std::future<int64_t>> futures;

    for (const auto& text : texts) {
        futures.push_back(pool.submit([&store, &text]() {
            return ingest_text(store, text, global_cpe_vocab);
        }));
    }

    for (auto& f : futures) {
        f.get();
    }
}
```

---

## Summary

### The Pipeline

1. **Characters/Numbers** → Constant atoms (PointZM) via landmark projection
2. **RLE** → Compress repeated atoms
3. **CPE** → Merge frequent pairs into composition atoms (Merkle DAG)
4. **Hierarchy** → Word/sentence/paragraph/document compositions
5. **Embeddings** → ARE LineStrings (N/4 points = trajectory)
6. **Weights** → Sparse edges [A, B] with multiplicity (no value atoms)

### The Model

- **Embedding IS a trajectory** - Not "vector stored as LineString" - it IS a LineString
- **Weight IS connection count** - Multiplicity encodes magnitude, not float atoms
- **Semantics emerge from topology** - Which atoms connect, not random float values
- **Referential integrity = weight strength** - `COUNT(*)` / `SUM(multiplicity)`

### Spatial = Semantic

| PostGIS Operation | Semantic Meaning |
|-------------------|------------------|
| `ST_Distance` | Similarity |
| `ST_Intersects` | Relatedness |
| `ST_FrechetDistance` | Trajectory similarity |
| `ST_HausdorffDistance` | Shape similarity |
| `ST_ConvexHull` | Semantic boundary |
| `<->` (KNN operator) | Nearest neighbors |
| Hilbert range scan | Locality-preserving search |

### Mathematical Foundations

- **Hilbert Curve**: Locality-preserving 4D → 1D mapping (Gray codes, rotation states)
- **Borsuk-Ulam**: Antipodal points guarantee for hypersphere semantics
- **A\*/Dijkstra**: Graph traversal on edge topology (C++ side)
- **Euler characteristic**: Topological invariants for composition structure

### The Result

- **Storage IS the model** - No separate inference engine
- **Query IS inference** - SQL + PostGIS = forward pass
- **Deduplication IS free** - Content addressing (BLAKE3)
- **The database replaces the AI** - Same substrate for text, code, models, everything

Sources:
- [BPE Algorithm (karpathy/minbpe)](https://github.com/karpathy/minbpe)
- [Merkle DAG (IPFS Docs)](https://docs.ipfs.tech/concepts/merkle-dag/)
- [Neural Network Pruning](https://datature.io/blog/a-comprehensive-guide-to-neural-network-model-pruning)
- [Magnitude-based Pruning (Intel Distiller)](https://intellabs.github.io/distiller/pruning.html)
