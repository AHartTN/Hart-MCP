# Hartonomous Process Specification

---

## 1. Seeding

Before any content ingestion, the hypersphere must be seeded with constant atoms.

### 1.1 Character Seeding

1. Iterate all Unicode codepoints (or target subset)
2. For each codepoint:
   - Determine category (uppercase letter, lowercase letter, digit, punctuation, control, etc.)
   - Compute hypersphere region for category
   - Within region, compute position using Fibonacci spiral:
     ```
     index = position within category
     θ = 2π × index × φ⁻¹  (φ = golden ratio)
     ```
   - Convert to (X, Y, Z, M) on hypersphere surface (X² + Y² + Z² + M² = R²)
   - Cluster related characters:
     - 'A' and 'a' adjacent
     - 'e', 'é', 'è', 'ê' clustered
   - Compute Hilbert index
   - Compute content hash
   - INSERT constant atom

### 1.2 Number Seeding

1. Define numeric regions on hypersphere (integers, common floats, etc.)
2. For frequently-used numbers (0, 1, -1, common weights):
   - Pre-compute positions
   - INSERT constant atoms
3. Other numbers created on-demand during ingestion

### 1.3 Seeding Output

- All character constants exist in atom table
- Known number constants exist
- System ready for content ingestion

---

## 2. Run-Length Encoding (RLE)

Repeated constants are not duplicated in compositions.

### 2.1 Example: "Hello"

Traditional: [H, E, L, L, O] = 5 refs
RLE: [H, E, L×2, O] = 4 refs with multiplicity

### 2.2 Storage

Parallel arrays in atom table:
```
refs = [H_id, E_id, L_id, O_id]
multiplicities = [1, 1, 2, 1]
```

Both arrays have same length. `multiplicities[i]` is the repeat count for `refs[i]`.

### 2.3 Process

1. Iterate input sequence
2. Track current atom and count
3. When atom changes:
   - Emit (previous_atom, count)
   - Reset count
4. Build refs with multiplicities
5. Geometry: LineString still traces through each point, but L appears once with weight/thickness or the trajectory passes through L with noted multiplicity

---

## 3. Content Pair Encoding (CPE)

Like BPE, but for atoms. Finds common pairs, creates composition atoms for them.

### 3.1 Training Phase

1. Ingest corpus as character constants
2. Count all adjacent atom pairs:
   ```
   pair_counts = {}
   for i in range(len(atoms) - 1):
       pair = (atoms[i], atoms[i+1])
       pair_counts[pair] += 1
   ```
3. Find most frequent pair
4. Create composition atom for that pair:
   - refs = [atom_a, atom_b]
   - geometry = LineString through A and B
   - Upsert, get new atom ID
5. Replace all occurrences of pair in corpus with new atom ID
6. Repeat until vocabulary size reached or frequency threshold

### 3.2 Example

Corpus: "the cat sat on the mat"

Initial atoms: [t, h, e, ' ', c, a, t, ' ', s, a, t, ...]

Iteration 1:
- Most frequent pair: ('t', 'h') appears 2x
- Create composition atom TH_id = [t_id, h_id]
- Replace: [TH_id, e, ' ', c, a, t, ' ', s, a, t, ...]

Iteration 2:
- Most frequent pair: ('a', 't') appears 3x
- Create composition atom AT_id = [a_id, t_id]
- Replace: [TH_id, e, ' ', c, AT_id, ' ', s, AT_id, ...]

Iteration 3:
- Most frequent pair: ('TH', 'e') appears 2x
- Create composition atom THE_id = [TH_id, e_id]
- Replace: [THE_id, ' ', c, AT_id, ' ', s, AT_id, ...]

### 3.3 Hierarchical Compositions

CPE naturally creates hierarchy:
- Level 0: Character constants (t, h, e, a, ...)
- Level 1: Common pairs (th, at, ...)
- Level 2: Common pair-pairs (the, ...)
- Level N: Words, phrases, etc.

Each level is compositions referencing the level below.

### 3.4 Deduplication

- Same pair in different documents → same composition atom (content hash matches)
- "the" in document A and "the" in document B → same atom ID
- Automatic via content addressing

---

## 4. Text Ingestion (Full Flow)

Input: UTF-8 string

### 4.1 Atomization
1. Iterate codepoints
2. For each: lookup character constant atom ID (already seeded)
3. Result: ordered list of atom IDs

### 4.2 CPE Pass (First)
1. Apply learned CPE vocabulary to character sequence
2. Replace known pairs with composition atom IDs
3. Repeat until no more replacements
4. Result: compressed atom sequence (common patterns merged)

### 4.3 RLE Pass (Second)
1. Apply run-length encoding to CPE output
2. Compress consecutive identical atoms
3. Result: refs with multiplicities

**Order matters**: CPE first (corpus-wide vocabulary patterns), then RLE (local consecutive runs).

### 4.4 Hierarchical Composition
1. Group into words (by whitespace/punctuation)
2. Create word compositions (if not already exist via CPE)
3. Group into sentences
4. Create sentence compositions
5. Group into paragraphs
6. Create paragraph compositions
7. Create document composition
8. Return document atom ID

---

## 5. Token Ingestion

Input: Token string from model vocabulary

### 5.1 Single Character Token
1. Lookup character constant atom ID
2. Return that ID (no composition)

### 5.2 Multi-Character Token
1. Lookup character constants for each char
2. Apply RLE if repeats
3. Check if CPE composition already exists
   - If yes: return existing composition atom ID
   - If no: create new composition
4. Return composition atom ID

---

## 6. Embedding Ingestion

Input: Token atom ID, embedding vector (N numbers)

**An embedding IS a trajectory**, not "stored as" a LineString.

### 6.1 Point Coordinate Scheme

Each dimension becomes one point ON the hypersphere surface (satisfies X² + Y² + Z² + M² = R²):

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

Every point satisfies the hypersphere constraint. The embedding value (including sign) is encoded in the angular position.

### 6.2 LineString Creation
1. Build WKT: LINESTRING ZM(x1 y1 z1 m1, x2 y2 z2 m2, ...)
2. This IS the embedding (not a "stored representation")
3. 768 floats = 768 points = trajectory through 4D space

### 6.3 Storage
1. Compute centroid: `ST_Centroid(geom)` 
2. Compute Hilbert index from centroid (similar embeddings → similar centroids → similar indices)
3. Compute content hash (from raw embedding bytes)
4. refs = [token_atom_id]
5. INSERT composition atom
6. Return embedding atom ID

---

## 7. Weight Ingestion

Input: Weight matrix, row atom IDs, column atom IDs, threshold

**Weights ARE connection counts, not value atoms.**

### 7.1 Filtering
1. Find max absolute weight in matrix (for normalization)
2. For each (i, j) in matrix:
   - Normalize: `normalized = abs(weight[i][j]) / max_abs`
   - If normalized < 0.5: skip (no relationship)
   - Else: process

### 7.2 Quantize to Multiplicity
1. `multiplicity = round(normalized * MAX_MULTIPLICITY)` (e.g., MAX = 100)
2. Clamp to at least 1 if above threshold

### 7.3 Edge Storage (In Atom Table)
1. Get input_atom_id = row_atoms[i]
2. Get output_atom_id = col_atoms[j]
3. refs = [input_atom_id, output_atom_id]
4. multiplicities = [1, multiplicity]  // 1 for input, weight for output
5. geom = NULL (compute on demand via ST_MakeLine when needed)
6. Compute hash from refs + multiplicities
7. Compute Hilbert from midpoint of input/output atom coordinates
8. INSERT into atom table (is_constant = FALSE)

**No weight value atoms. No stored edge geometry.** Edges are compositions. Geometry derived when needed:
```sql
SELECT ST_MakeLine(a.geom, b.geom)
FROM atom a, atom b
WHERE a.id = edge.refs[1] AND b.id = edge.refs[2];
```

**Negative weights = spatial distance**: Atoms that repel (negative weight) are far apart on hypersphere. Atoms that attract (positive weight) are close. The distance between atom positions encodes relationship type. Multiplicity encodes magnitude regardless of sign.

### 7.4 Query Weight Strength
```sql
SELECT SUM(multiplicities[2]) as strength
FROM atom
WHERE is_constant = FALSE
  AND array_length(refs, 1) = 2
  AND refs[1] = $A AND refs[2] = $B;
```

---

## 8. Model Ingestion (Complete)

Input: Model file

### 8.1 Vocabulary Extraction
1. Load tokenizer/vocabulary
2. For each token: run Token Ingestion (§5)
3. Map: token_index → atom_id

### 8.2 Embedding Extraction
1. Load embedding matrix
2. For each token_index:
   - Get embedding vector (row)
   - Run Embedding Ingestion (§6)
3. Map: token_index → embedding_atom_id

### 8.3 Weight Extraction
1. For each weight tensor in model:
   - Determine input/output dimensions
   - Map to atom IDs
   - Run Weight Ingestion (§7) with threshold

### 8.4 Architecture Composition
1. Create composition representing layer structure
2. refs = relevant embedding/weight atom IDs
3. Return model atom ID

---

## 9. Hilbert Index Computation

Input: (X, Y, Z, M)

1. Define bounds (from hypersphere radius)
2. Quantize to N-bit integers:
   ```
   x_int = quantize(X, -R, R, bits)
   y_int = quantize(Y, -R, R, bits)
   z_int = quantize(Z, -R, R, bits)
   m_int = quantize(M, -R, R, bits)
   ```
3. Apply 4D Hilbert curve transform (Gray code, dimension rotation)
4. Output: 128-bit index split to (hilbert_high, hilbert_low)

---

## 10. Content Hash Computation

Algorithm: BLAKE3, 256-bit output (32 bytes). Zero collision tolerance.

### Constants
```
hash = BLAKE3(X_bytes || Y_bytes || Z_bytes || M_bytes)
```

### Compositions
```
hash = BLAKE3(child_hash_1 || mult_1 || child_hash_2 || mult_2 || ...)
```

---

## 11. Reconstruction: Text

Input: composition atom ID

```
function reconstruct(atom_id):
    atom = load(atom_id)

    if atom.is_constant:
        return reverse_landmark_projection(atom.geom) → character

    result = ""
    for (ref_id, multiplicity) in atom.refs:
        char_or_string = reconstruct(ref_id)
        result += char_or_string × multiplicity

    return result
```

---

## 12. Reconstruction: Embedding

Input: embedding atom ID

1. Load atom
2. Get LineString geometry
3. Extract points
4. Return X coordinate from each point: [p1.x, p2.x, p3.x, ...]
   - X = the embedding value
   - Y was normalized position, Z and M were reserved

---

## 13. Reconstruction: Weights

Input: list of input atom IDs, list of output atom IDs

```
function reconstruct_weights(input_atoms, output_atoms):
    rows = len(input_atoms)
    cols = len(output_atoms)
    matrix = zeros(rows, cols)
    
    # Find max multiplicity for denormalization
    max_mult = query("SELECT MAX(multiplicities[2]) FROM atom WHERE NOT is_constant AND array_length(refs, 1) = 2")
    
    for i in range(rows):
        for j in range(cols):
            mult = get_edge_multiplicity(input_atoms[i], output_atoms[j])
            matrix[i][j] = mult / max_mult  # Denormalize
    
    return matrix

function get_edge_multiplicity(input_id, output_id):
    result = query("SELECT SUM(multiplicities[2]) FROM atom WHERE refs[1] = :input_id AND refs[2] = :output_id")
    return result or 0
```

**Note**: This is LOSSY by design. Sub-threshold weights were not stored and export as 0. The exported model is smaller and tighter.

---

## 13. Query: Semantic Similarity

Input: atom_id, k

```sql
SELECT b.id, ST_Distance(a.geom, b.geom) as dist
FROM atom a, atom b
WHERE a.id = :atom_id
  AND b.id != a.id
ORDER BY dist
LIMIT :k
```

---

## 14. Query: Trajectory Intersection

Input: composition atom_id

```sql
SELECT b.id,
       ST_Length(ST_Intersection(a.geom, b.geom)) as overlap_length
FROM atom a, atom b
WHERE a.id = :atom_id
  AND b.is_constant = false
  AND ST_Intersects(a.geom, b.geom)
ORDER BY overlap_length DESC
```

---

## 15. Deduplication Flow

Every insert:

1. Compute content_hash
2. Attempt INSERT with ON CONFLICT (content_hash) DO NOTHING
3. SELECT id WHERE content_hash = :hash
4. Return id (existing or new)

Same content anywhere → same atom ID. Always.
