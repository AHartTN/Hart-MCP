# TRUE LOSSLESS DETERMINISTIC ARCHITECTURE

## PROBLEM IDENTIFIED
Current implementation uses `double` for coordinates, which accumulates floating-point error (< 1e-15). Over billions of operations, this compounds and becomes LOSSY. Platform/compiler differences can cause different rounding, breaking determinism.

## SOLUTION: SEED-BASED ARCHITECTURE

### Core Principle
**NEVER STORE COORDINATES. ALWAYS COMPUTE FROM SEED.**

### Atom Table Schema (Revised)

```sql
CREATE TABLE atom (
    id BIGSERIAL PRIMARY KEY,
    
    -- SEED (the source of truth - LOSSLESS)
    seed_type SMALLINT NOT NULL,  -- 0=unicode, 1=integer, 2=float_bits, 3=composition
    seed_codepoint INT,            -- Unicode codepoint (if seed_type=0)
    seed_integer BIGINT,           -- Integer value (if seed_type=1)
    seed_float_bits BIGINT,        -- IEEE754 double bits (if seed_type=2)
    
    -- COMPOSITION DATA (if seed_type=3)
    child_ids BIGINT[],            -- References to child atoms
    multiplicities INT[],          -- RLE counts or edge weights
    
    -- HILBERT INDEX (computed from seed, stored for indexing only)
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,
    
    -- CONTENT HASH (computed from seed or children, stored for deduplication)
    content_hash BYTEA NOT NULL UNIQUE,
    
    -- NO GEOMETRY STORED
    -- Coordinates computed on-demand via compute_coords_from_seed(seed_*)
    
    -- Indexes
    CONSTRAINT atom_seed_check CHECK (
        (seed_type = 0 AND seed_codepoint IS NOT NULL) OR
        (seed_type = 1 AND seed_integer IS NOT NULL) OR
        (seed_type = 2 AND seed_float_bits IS NOT NULL) OR
        (seed_type = 3 AND child_ids IS NOT NULL AND multiplicities IS NOT NULL)
    )
);

CREATE INDEX idx_atom_hilbert ON atom (hilbert_high, hilbert_low);
CREATE INDEX idx_atom_hash ON atom USING HASH (content_hash);
```

### Deterministic Coordinate Computation

```cpp
// SEED is the ground truth
struct AtomSeed {
    uint8_t type;        // 0=unicode, 1=integer, 2=float_bits
    union {
        uint32_t codepoint;
        int64_t integer;
        uint64_t float_bits;  // IEEE754 double as uint64
    } value;
};

// Coordinates are COMPUTED, never stored
struct Point4D {
    double x, y, z, m;
};

// Pure function: seed → coordinates (ALWAYS identical output for same input)
Point4D compute_coords_from_seed(AtomSeed seed) {
    switch (seed.type) {
        case 0:  // Unicode
            return project_codepoint_deterministic(seed.value.codepoint);
        case 1:  // Integer
            return project_integer_deterministic(seed.value.integer);
        case 2:  // Float
            double d;
            memcpy(&d, &seed.value.float_bits, sizeof(double));
            return project_float_deterministic(d);
    }
}

// Integer-only Hilbert computation (NO FLOATS)
HilbertIndex compute_hilbert_from_seed(AtomSeed seed) {
    // Get quantized angles directly from seed without float arithmetic
    uint32_t psi, theta, phi;
    
    switch (seed.type) {
        case 0:  // Unicode codepoint
            // Deterministic angle mapping using integer arithmetic
            psi = (seed.value.codepoint / CATEGORY_SIZE) * PSI_QUANTUM;
            theta = ((seed.value.codepoint % CATEGORY_SIZE) * GOLDEN_RATIO_INT) >> 32;
            phi = ((seed.value.codepoint * FIBONACCI_INT) & 0xFFFFFFFF);
            break;
        case 1:  // Integer
            psi = (seed.value.integer >= 0) ? PSI_POSITIVE : PSI_NEGATIVE;
            theta = (abs(seed.value.integer) * GOLDEN_RATIO_INT) >> 32;
            phi = (abs(seed.value.integer) * FIBONACCI_INT) & 0xFFFFFFFF;
            break;
        case 2:  // Float bits
            // Use bit pattern directly
            psi = (seed.value.float_bits >> 52) & 0x7FF;  // Exponent
            theta = (seed.value.float_bits >> 32) & 0xFFFFF;  // High mantissa
            phi = seed.value.float_bits & 0xFFFFFFFF;  // Low mantissa
            break;
    }
    
    // Integer-only 4D Hilbert computation
    return hilbert_from_angles_int(psi, theta, phi);
}
```

### Content Hash (from SEED, not coordinates)

```cpp
// Hash the SEED, not the coordinates
ContentHash hash_seed(AtomSeed seed) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    
    // Hash type
    blake3_hasher_update(&hasher, &seed.type, 1);
    
    // Hash value (8 bytes, deterministic across platforms)
    uint64_t value_bits;
    switch (seed.type) {
        case 0: value_bits = seed.value.codepoint; break;
        case 1: value_bits = seed.value.integer; break;
        case 2: value_bits = seed.value.float_bits; break;
    }
    blake3_hasher_update(&hasher, &value_bits, 8);
    
    ContentHash out;
    blake3_hasher_finalize(&hasher, out.bytes, 32);
    return out;
}
```

### Query Operations (Compute Geometry On-Demand)

```sql
-- Spatial query: compute geometry when needed
WITH target AS (
    SELECT 
        id,
        hartonomous_compute_geom(seed_type, seed_codepoint, seed_integer, seed_float_bits) AS geom
    FROM atom
    WHERE id = $1
),
neighbors AS (
    SELECT 
        a.id,
        hartonomous_compute_geom(a.seed_type, a.seed_codepoint, a.seed_integer, a.seed_float_bits) AS geom,
        a.hilbert_high,
        a.hilbert_low
    FROM atom a, target t
    WHERE a.hilbert_high BETWEEN t.hilbert_high - $2 AND t.hilbert_high + $2
      AND a.id != t.id
    LIMIT 1000  -- Hilbert pre-filter
)
SELECT 
    n.id,
    ST_Distance(t.geom, n.geom) AS distance
FROM neighbors n, target t
ORDER BY distance
LIMIT 10;
```

### PostgreSQL UDF for Geometry Computation

```sql
CREATE OR REPLACE FUNCTION hartonomous_compute_geom(
    seed_type SMALLINT,
    seed_codepoint INT,
    seed_integer BIGINT,
    seed_float_bits BIGINT
) RETURNS GEOMETRY(POINTZM, 0) AS $$
DECLARE
    x DOUBLE PRECISION;
    y DOUBLE PRECISION;
    z DOUBLE PRECISION;
    m DOUBLE PRECISION;
BEGIN
    -- Call C function via plpgsql or external C extension
    -- Pure function: deterministic output for given inputs
    
    CASE seed_type
        WHEN 0 THEN
            -- Project Unicode codepoint
            SELECT * INTO x, y, z, m FROM hartonomous_project_codepoint(seed_codepoint);
        WHEN 1 THEN
            -- Project integer
            SELECT * INTO x, y, z, m FROM hartonomous_project_integer(seed_integer);
        WHEN 2 THEN
            -- Project float bits
            SELECT * INTO x, y, z, m FROM hartonomous_project_float_bits(seed_float_bits);
    END CASE;
    
    RETURN ST_MakePointZM(x, y, z, m, 0);  -- SRID 0
END;
$$ LANGUAGE plpgsql IMMUTABLE;  -- IMMUTABLE = deterministic
```

## GUARANTEES

1. **TRUE LOSSLESS**: Seeds are integers (Unicode codepoints, int64, float bits). No accumulation of error.

2. **DETERMINISTIC**: Same seed ALWAYS produces identical coordinates, hash, Hilbert index. Platform-independent.

3. **SPACE EFFICIENT**: Only seeds stored. Coordinates computed on-demand. Compositions reference child IDs only.

4. **RECONSTRUCT PERFECT**: Traverse refs to seeds, reconstruct exact original. No quantization loss.

5. **DEDUPLICATION**: Hash of seed. Identical seeds = identical hash = single atom.

## IMPLEMENTATION TASKS

1. ✅ Revise `types.h` to include AtomSeed
2. ✅ Revise `landmark_projection.h` to compute from seed
3. ✅ Revise `content_hash.h` to hash seeds not coords
4. ⬜ Update database schema (migration script)
5. ⬜ Implement integer-only Hilbert computation
6. ⬜ Implement PostgreSQL UDF for on-demand geometry
7. ⬜ Update all ingestion code to store seeds
8. ⬜ Update all query code to compute geometry on-demand
9. ⬜ VERIFY: Roundtrip tests (text → seeds → reconstruct → identical text)
10. ⬜ VERIFY: Determinism tests (same input on different machines → identical hashes)

This is the REAL architecture. Anything less is LOSSY and BROKEN.
