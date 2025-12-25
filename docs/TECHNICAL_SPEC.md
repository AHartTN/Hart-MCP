# Hartonomous Technical Specification

## CRITICAL CONSTRAINTS

- All algorithms implemented from first principles - NO external libraries
- PostgreSQL + PostGIS + standard C++ + .NET only
- NO SRID - geometry is abstract 4D hypersphere, not geographic
- NO assumed dimensions - N is whatever the ingested model has

---

## 1. 4D Hilbert Curve Algorithm

The 4D Hilbert curve maps coordinates (x, y, z, m) to a 1D index preserving locality. Points near each other in 4D space have numerically close Hilbert indices.

### 1.1 Data Structures

```cpp
struct HilbertIndex {
    uint64_t high;  // Upper 64 bits
    uint64_t low;   // Lower 64 bits
};

// Bit depth: 16 bits per dimension = 64 total bits (fits in low)
// Bit depth: 32 bits per dimension = 128 total bits (uses both)
constexpr int BITS_PER_DIM = 16;
constexpr int DIMENSIONS = 4;
```

### 1.2 Gray Code Operations

Gray code is essential for Hilbert curve computation. Adjacent values differ in exactly one bit.

```cpp
// Binary to Gray code
inline uint32_t binary_to_gray(uint32_t n) {
    return n ^ (n >> 1);
}

// Gray code to binary
inline uint32_t gray_to_binary(uint32_t g) {
    uint32_t n = g;
    for (uint32_t shift = 1; shift < 32; shift <<= 1) {
        n ^= (n >> shift);
    }
    return n;
}
```

### 1.3 Quantization

Convert floating-point coordinates to fixed-point integers for bit manipulation.

```cpp
// Hypersphere radius - all constants live on surface where X² + Y² + Z² + M² = R²
constexpr double HYPERSPHERE_RADIUS = 1.0;
constexpr double COORD_MIN = -HYPERSPHERE_RADIUS;
constexpr double COORD_MAX = HYPERSPHERE_RADIUS;

inline uint32_t quantize(double value) {
    double normalized = (value - COORD_MIN) / (COORD_MAX - COORD_MIN);
    normalized = std::clamp(normalized, 0.0, 1.0);
    return static_cast<uint32_t>(normalized * ((1ULL << BITS_PER_DIM) - 1));
}

inline double dequantize(uint32_t quantized) {
    double normalized = static_cast<double>(quantized) / ((1ULL << BITS_PER_DIM) - 1);
    return normalized * (COORD_MAX - COORD_MIN) + COORD_MIN;
}
```

### 1.4 The 4D Hilbert Algorithm

The algorithm processes bits from most significant to least, tracking orientation through a rotation state.

```cpp
// 4D rotation state encodes current orientation of the Hilbert curve
// There are 384 possible orientations in 4D (4! * 2^4 = 384)
struct RotationState {
    uint8_t perm[4];   // Dimension permutation
    uint8_t flip;      // Bit mask for dimension flips (4 bits used)
};

// Initial state: identity permutation, no flips
constexpr RotationState INITIAL_STATE = {{0, 1, 2, 3}, 0};

// Entry point lookup table: maps Gray code to entry point adjustment
// For 4D, there are 16 possible subcells (2^4)
// entry_point[gray] gives the Gray code of the entry point for that subcell
const uint8_t ENTRY_POINT[16] = {
    0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0
};  // Computed via: entry_point[g] = g ^ (g >> 1) for proper initialization

// Direction lookup: determines axis of traversal change
// direction[gray] gives which dimension the curve exits through
const uint8_t DIRECTION[16] = {
    0, 1, 2, 0, 3, 0, 0, 1,
    2, 0, 0, 1, 0, 3, 2, 0
};

// Transform rotation state based on which subcell we enter
void update_rotation(RotationState& state, uint8_t gray_code) {
    // The rotation update depends on the entry/exit geometry
    // This implements the 4D analog of the 2D "L" and "⌐" orientations

    uint8_t entry = gray_code ^ (gray_code >> 1);  // Entry Gray code
    uint8_t axis = 0;

    // Find trailing set bit to determine rotation axis
    for (int i = 0; i < DIMENSIONS; i++) {
        if (gray_code & (1 << i)) {
            axis = i;
        }
    }

    // Swap dimensions in permutation based on Gray code
    if (gray_code != 0 && gray_code != 15) {
        // Find the two dimensions to swap
        uint8_t d1 = state.perm[0];
        uint8_t d2 = state.perm[axis];
        state.perm[0] = d2;
        state.perm[axis] = d1;

        // Update flips
        state.flip ^= (1 << d1);
    }
}

// Forward transform: 4D coordinates to Hilbert index
HilbertIndex coords_to_hilbert(double x, double y, double z, double m) {
    // Quantize coordinates
    uint32_t coords[4] = {
        quantize(x), quantize(y), quantize(z), quantize(m)
    };

    HilbertIndex result = {0, 0};
    RotationState state = INITIAL_STATE;

    // Process from most significant bit to least
    for (int bit = BITS_PER_DIM - 1; bit >= 0; bit--) {
        // Extract current bit from each dimension
        uint8_t bits = 0;
        for (int d = 0; d < DIMENSIONS; d++) {
            if (coords[state.perm[d]] & (1U << bit)) {
                bits |= (1 << d);
            }
        }

        // Apply current flip state
        bits ^= state.flip;

        // Convert to Gray code position within this level
        uint8_t gray = binary_to_gray(bits);

        // Accumulate into result (shift left by 4 bits, add gray)
        // For 16-bit depth: result fits in 64 bits
        result.low = (result.low << DIMENSIONS) | gray;

        // Update rotation state for next level
        update_rotation(state, gray);
    }

    return result;
}

// Inverse transform: Hilbert index to 4D coordinates
void hilbert_to_coords(HilbertIndex h, double& x, double& y, double& z, double& m) {
    uint32_t coords[4] = {0, 0, 0, 0};
    RotationState state = INITIAL_STATE;

    // Extract gray codes from most significant to least
    for (int bit = BITS_PER_DIM - 1; bit >= 0; bit--) {
        // Extract 4-bit gray code at this level
        int shift = bit * DIMENSIONS;
        uint8_t gray = (h.low >> shift) & 0xF;

        // Convert Gray to binary position
        uint8_t bits = gray_to_binary(gray);

        // Apply inverse flip
        bits ^= state.flip;

        // Distribute bits to coordinate dimensions
        for (int d = 0; d < DIMENSIONS; d++) {
            if (bits & (1 << d)) {
                coords[state.perm[d]] |= (1U << bit);
            }
        }

        // Update rotation state
        update_rotation(state, gray);
    }

    // Dequantize
    x = dequantize(coords[0]);
    y = dequantize(coords[1]);
    z = dequantize(coords[2]);
    m = dequantize(coords[3]);
}
```

### 1.5 Hilbert Distance

For comparing proximity of Hilbert indices:

```cpp
uint64_t hilbert_distance(HilbertIndex a, HilbertIndex b) {
    // For 16-bit depth, everything is in .low
    if (a.low > b.low) return a.low - b.low;
    return b.low - a.low;
}
```

---

## 2. Landmark Projection

Maps constants (characters, numbers) to deterministic positions on the 4D hypersphere surface.

### 2.1 4D Hyperspherical Coordinates

A point on the 4D hypersphere surface is defined by three angles:

```
Given angles (ψ, θ, φ) where:
  ψ ∈ [0, π]      - "latitude" from pole
  θ ∈ [0, π]      - secondary angle
  φ ∈ [0, 2π)     - "longitude"

Cartesian coordinates on sphere of radius R:
  X = R × sin(ψ) × sin(θ) × cos(φ)
  Y = R × sin(ψ) × sin(θ) × sin(φ)
  Z = R × sin(ψ) × cos(θ)
  M = R × cos(ψ)

Verification: X² + Y² + Z² + M² = R² (always satisfied)
```

### 2.2 Category Segmentation

The hypersphere is segmented by the ψ angle (pole-to-pole), giving each category a distinct "latitude band":

```cpp
// Category ID determines ψ band
enum class CharacterCategory : uint8_t {
    ASCII_CONTROL = 0,      // 0x00-0x1F, 0x7F
    ASCII_DIGIT = 1,        // 0x30-0x39
    ASCII_UPPER = 2,        // 0x41-0x5A
    ASCII_LOWER = 3,        // 0x61-0x7A
    ASCII_PUNCT = 4,        // 0x20-0x2F, 0x3A-0x40, 0x5B-0x60, 0x7B-0x7E
    LATIN_EXTENDED = 5,     // 0x80-0x024F
    GREEK = 6,              // 0x0370-0x03FF
    CYRILLIC = 7,           // 0x0400-0x04FF
    CJK = 8,                // 0x4E00-0x9FFF
    EMOJI = 9,              // 0x1F600-0x1F64F and others
    MATH_SYMBOLS = 10,      // 0x2200-0x22FF
    CURRENCY = 11,          // 0x20A0-0x20CF
    NUMBERS_POSITIVE = 12,  // Positive real numbers
    NUMBERS_NEGATIVE = 13,  // Negative real numbers
    NUMBERS_SPECIAL = 14,   // 0, 1, -1, π, e, etc.
    OTHER = 15              // Everything else
};

// Each category gets a band of ψ
constexpr double CATEGORY_COUNT = 16.0;
constexpr double PSI_PER_CATEGORY = M_PI / CATEGORY_COUNT;

// Get ψ band center for category
inline double category_to_psi_center(CharacterCategory cat) {
    return (static_cast<double>(cat) + 0.5) * PSI_PER_CATEGORY;
}

// Category lookup from codepoint
CharacterCategory get_category(uint32_t codepoint) {
    if (codepoint <= 0x1F || codepoint == 0x7F) return CharacterCategory::ASCII_CONTROL;
    if (codepoint >= 0x30 && codepoint <= 0x39) return CharacterCategory::ASCII_DIGIT;
    if (codepoint >= 0x41 && codepoint <= 0x5A) return CharacterCategory::ASCII_UPPER;
    if (codepoint >= 0x61 && codepoint <= 0x7A) return CharacterCategory::ASCII_LOWER;
    if ((codepoint >= 0x20 && codepoint <= 0x2F) ||
        (codepoint >= 0x3A && codepoint <= 0x40) ||
        (codepoint >= 0x5B && codepoint <= 0x60) ||
        (codepoint >= 0x7B && codepoint <= 0x7E)) return CharacterCategory::ASCII_PUNCT;
    if (codepoint >= 0x80 && codepoint <= 0x024F) return CharacterCategory::LATIN_EXTENDED;
    if (codepoint >= 0x0370 && codepoint <= 0x03FF) return CharacterCategory::GREEK;
    if (codepoint >= 0x0400 && codepoint <= 0x04FF) return CharacterCategory::CYRILLIC;
    if (codepoint >= 0x4E00 && codepoint <= 0x9FFF) return CharacterCategory::CJK;
    if ((codepoint >= 0x1F600 && codepoint <= 0x1F64F) ||
        (codepoint >= 0x1F300 && codepoint <= 0x1F5FF)) return CharacterCategory::EMOJI;
    if (codepoint >= 0x2200 && codepoint <= 0x22FF) return CharacterCategory::MATH_SYMBOLS;
    if (codepoint >= 0x20A0 && codepoint <= 0x20CF) return CharacterCategory::CURRENCY;
    return CharacterCategory::OTHER;
}
```

### 2.3 Fibonacci Spiral Placement

Within each category, characters are placed using the golden angle to achieve uniform distribution:

```cpp
constexpr double GOLDEN_RATIO = 1.6180339887498948482;
constexpr double GOLDEN_ANGLE = 2.0 * M_PI / (GOLDEN_RATIO * GOLDEN_RATIO);  // ≈ 2.39996 rad ≈ 137.5°

// Index within category determines (θ, φ) on that latitude band
// We use Fibonacci spiral within each category's band

struct SphericalCoords {
    double psi;    // [0, π]
    double theta;  // [0, π]
    double phi;    // [0, 2π)
};

SphericalCoords fibonacci_position(CharacterCategory cat, uint32_t index_in_category, uint32_t category_size) {
    SphericalCoords coords;

    // ψ is determined by category, with slight variation based on index for depth
    double psi_center = category_to_psi_center(cat);
    double psi_range = PSI_PER_CATEGORY * 0.8;  // Use 80% of band, leave gaps

    // θ is distributed linearly based on index
    double t = static_cast<double>(index_in_category) / static_cast<double>(std::max(1u, category_size - 1));
    coords.theta = t * M_PI;  // 0 to π

    // φ uses golden angle for uniform spacing
    coords.phi = fmod(index_in_category * GOLDEN_ANGLE, 2.0 * M_PI);

    // ψ varies slightly with index to spread points in 3D within the band
    double psi_offset = (t - 0.5) * psi_range;
    coords.psi = psi_center + psi_offset;

    return coords;
}
```

### 2.4 Case and Variant Clustering

Related characters must be adjacent. We achieve this by assigning base characters and offsets:

```cpp
// Get base character and offset for clustering
struct ClusterInfo {
    uint32_t base_codepoint;  // The "canonical" form
    double offset_scale;       // How far from base (0.0 = same, 1.0 = max offset)
    int offset_variant;        // Which variant (for multiple accents)
};

ClusterInfo get_cluster_info(uint32_t codepoint) {
    ClusterInfo info = {codepoint, 0.0, 0};

    // Lowercase -> Uppercase clustering
    if (codepoint >= 0x61 && codepoint <= 0x7A) {
        info.base_codepoint = codepoint - 0x20;  // 'a' -> 'A'
        info.offset_scale = 0.01;  // Very close
        info.offset_variant = 1;
    }

    // Latin Extended clustering to ASCII base
    // À Á Â Ã Ä Å -> A
    if (codepoint >= 0x00C0 && codepoint <= 0x00C5) {
        info.base_codepoint = 'A';
        info.offset_scale = 0.02;
        info.offset_variant = codepoint - 0x00C0 + 2;
    }
    // à á â ã ä å -> a -> A
    if (codepoint >= 0x00E0 && codepoint <= 0x00E5) {
        info.base_codepoint = 'A';
        info.offset_scale = 0.02;
        info.offset_variant = codepoint - 0x00E0 + 8;
    }
    // È É Ê Ë -> E
    if (codepoint >= 0x00C8 && codepoint <= 0x00CB) {
        info.base_codepoint = 'E';
        info.offset_scale = 0.02;
        info.offset_variant = codepoint - 0x00C8 + 2;
    }
    // è é ê ë -> e -> E
    if (codepoint >= 0x00E8 && codepoint <= 0x00EB) {
        info.base_codepoint = 'E';
        info.offset_scale = 0.02;
        info.offset_variant = codepoint - 0x00E8 + 8;
    }

    // Add more mappings as needed for other accented characters
    // Pattern: variant_codepoint -> base, small offset, unique variant number

    return info;
}
```

### 2.5 Complete Character Projection

```cpp
struct PointZM {
    double x, y, z, m;
};

// Pre-computed category indices (built once at init)
std::unordered_map<CharacterCategory, std::vector<uint32_t>> category_members;
std::unordered_map<uint32_t, uint32_t> codepoint_to_index;

void build_category_indices() {
    // Iterate all supported codepoints, assign to categories
    for (uint32_t cp = 0; cp <= 0x10FFFF; cp++) {
        if (!is_valid_codepoint(cp)) continue;
        CharacterCategory cat = get_category(cp);
        uint32_t idx = category_members[cat].size();
        category_members[cat].push_back(cp);
        codepoint_to_index[cp] = idx;
    }
}

PointZM landmark_project_character(uint32_t codepoint) {
    constexpr double R = HYPERSPHERE_RADIUS;

    ClusterInfo cluster = get_cluster_info(codepoint);
    CharacterCategory cat = get_category(cluster.base_codepoint);

    // Get base position from Fibonacci placement
    uint32_t idx = codepoint_to_index[cluster.base_codepoint];
    uint32_t cat_size = category_members[cat].size();
    SphericalCoords sph = fibonacci_position(cat, idx, cat_size);

    // Apply clustering offset
    double offset_angle = cluster.offset_variant * 0.001;  // Tiny angular offset
    sph.phi += offset_angle;
    sph.theta += offset_angle * 0.5;
    sph.psi += cluster.offset_scale * 0.1;

    // Clamp angles to valid ranges
    sph.psi = std::clamp(sph.psi, 0.001, M_PI - 0.001);
    sph.theta = std::clamp(sph.theta, 0.001, M_PI - 0.001);
    sph.phi = fmod(sph.phi + 4.0 * M_PI, 2.0 * M_PI);

    // Convert to Cartesian
    PointZM p;
    p.x = R * sin(sph.psi) * sin(sph.theta) * cos(sph.phi);
    p.y = R * sin(sph.psi) * sin(sph.theta) * sin(sph.phi);
    p.z = R * sin(sph.psi) * cos(sph.theta);
    p.m = R * cos(sph.psi);

    return p;
}
```

---

## 3. Number Projection

Numbers are constants that need hypersphere positions. They occupy dedicated latitude bands.

### 3.1 Number Categories

```cpp
enum class NumberCategory {
    POSITIVE,    // > 0
    NEGATIVE,    // < 0
    ZERO,        // = 0
    SPECIAL      // π, e, infinity, NaN
};

NumberCategory categorize_number(double value) {
    if (std::isnan(value)) return NumberCategory::SPECIAL;
    if (std::isinf(value)) return NumberCategory::SPECIAL;
    if (value == 0.0) return NumberCategory::ZERO;
    if (value > 0.0) return NumberCategory::POSITIVE;
    return NumberCategory::NEGATIVE;
}
```

### 3.2 Number Encoding

We encode numbers using their IEEE 754 bit representation to ensure every distinct float maps to a distinct point:

```cpp
// Bijective mapping: double -> uint64 preserving order for positive numbers
inline uint64_t double_to_sortable(double value) {
    uint64_t bits;
    std::memcpy(&bits, &value, sizeof(double));

    // IEEE 754 doubles: sign bit, then exponent, then mantissa
    // For positive numbers, bit representation is already ordered
    // For negative numbers, need to flip all bits
    if (bits >> 63) {  // Negative
        return ~bits;
    }
    // Positive: flip sign bit to sort after negatives
    return bits ^ (1ULL << 63);
}

inline double sortable_to_double(uint64_t sortable) {
    uint64_t bits;
    if (sortable >> 63) {  // Was positive
        bits = sortable ^ (1ULL << 63);
    } else {  // Was negative
        bits = ~sortable;
    }
    double value;
    std::memcpy(&value, &bits, sizeof(double));
    return value;
}
```

### 3.3 Number Projection Algorithm

```cpp
PointZM landmark_project_number(double value) {
    constexpr double R = HYPERSPHERE_RADIUS;

    NumberCategory cat = categorize_number(value);

    // Base ψ from category
    double psi;
    switch (cat) {
        case NumberCategory::ZERO:
            psi = M_PI * 0.5;  // Equator
            break;
        case NumberCategory::POSITIVE:
            psi = M_PI * 0.75;  // Upper hemisphere
            break;
        case NumberCategory::NEGATIVE:
            psi = M_PI * 0.25;  // Lower hemisphere
            break;
        case NumberCategory::SPECIAL:
            psi = M_PI * 0.05;  // Near pole
            break;
    }

    // Handle zero specially - single point
    if (cat == NumberCategory::ZERO) {
        double theta = M_PI * 0.5;
        double phi = 0.0;
        return {
            R * sin(psi) * sin(theta) * cos(phi),
            R * sin(psi) * sin(theta) * sin(phi),
            R * sin(psi) * cos(theta),
            R * cos(psi)
        };
    }

    // For other numbers, use sortable representation to get angles
    uint64_t sortable = double_to_sortable(std::abs(value));

    // Split sortable into θ and φ components
    // Use upper 32 bits for θ, lower 32 bits for φ
    uint32_t theta_bits = sortable >> 32;
    uint32_t phi_bits = sortable & 0xFFFFFFFF;

    // Map to angle ranges
    double theta = (static_cast<double>(theta_bits) / static_cast<double>(UINT32_MAX)) * M_PI;
    double phi = (static_cast<double>(phi_bits) / static_cast<double>(UINT32_MAX)) * 2.0 * M_PI;

    // Add slight ψ variation based on magnitude
    double magnitude_factor = std::log1p(std::abs(value)) / 100.0;  // Compress large ranges
    psi += magnitude_factor * 0.1;
    psi = std::clamp(psi, 0.001, M_PI - 0.001);

    PointZM p;
    p.x = R * sin(psi) * sin(theta) * cos(phi);
    p.y = R * sin(psi) * sin(theta) * sin(phi);
    p.z = R * sin(psi) * cos(theta);
    p.m = R * cos(psi);

    return p;
}
```

### 3.4 Integer Optimization

For integers, use exact representation:

```cpp
PointZM landmark_project_integer(int64_t value) {
    // Integers get deterministic positions based on value directly
    constexpr double R = HYPERSPHERE_RADIUS;

    // Use golden angle placement for spread
    double abs_val = static_cast<double>(std::abs(value));
    double sign_offset = (value >= 0) ? 0.5 : -0.5;

    double psi = M_PI * (0.5 + sign_offset * 0.4);  // Separate hemispheres
    double theta = fmod(abs_val * GOLDEN_ANGLE, M_PI);
    double phi = fmod(abs_val * GOLDEN_ANGLE * GOLDEN_RATIO, 2.0 * M_PI);

    // Small integers get slight adjustments for uniqueness
    psi += value * 0.0001;
    psi = std::clamp(psi, 0.001, M_PI - 0.001);

    PointZM p;
    p.x = R * sin(psi) * sin(theta) * cos(phi);
    p.y = R * sin(psi) * sin(theta) * sin(phi);
    p.z = R * sin(psi) * cos(theta);
    p.m = R * cos(psi);

    return p;
}
```

---

## 4. Content Hashing (BLAKE3-256)

### 4.1 Rationale: Zero Collision Tolerance

This system cannot survive a single hash collision:
- Merkle DAG integrity lost
- Reconstruction impossible
- CPE vocabulary corrupted
- Different content treated as identical

256-bit hash = 2^128 birthday bound = computationally infeasible collision.

### 4.2 Implementation

```cpp
#include "blake3.h"

constexpr size_t HASH_SIZE = 32;  // 256 bits
using ContentHash = std::array<uint8_t, HASH_SIZE>;

// Hash raw bytes
ContentHash hash_bytes(const void* data, size_t len) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    blake3_hasher_update(&hasher, data, len);

    ContentHash out;
    blake3_hasher_finalize(&hasher, out.data(), HASH_SIZE);
    return out;
}

// Hash constant atom (coordinates)
ContentHash hash_constant(const PointZM& p) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);

    // Hash coordinates as raw bytes in fixed order
    blake3_hasher_update(&hasher, &p.x, sizeof(double));
    blake3_hasher_update(&hasher, &p.y, sizeof(double));
    blake3_hasher_update(&hasher, &p.z, sizeof(double));
    blake3_hasher_update(&hasher, &p.m, sizeof(double));

    ContentHash out;
    blake3_hasher_finalize(&hasher, out.data(), HASH_SIZE);
    return out;
}

// Hash composition atom (child hashes + multiplicities)
ContentHash hash_composition(
    const std::vector<ContentHash>& child_hashes,
    const std::vector<int32_t>& multiplicities
) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);

    // Hash each child's hash followed by its multiplicity
    for (size_t i = 0; i < child_hashes.size(); i++) {
        blake3_hasher_update(&hasher, child_hashes[i].data(), HASH_SIZE);
        blake3_hasher_update(&hasher, &multiplicities[i], sizeof(int32_t));
    }

    ContentHash out;
    blake3_hasher_finalize(&hasher, out.data(), HASH_SIZE);
    return out;
}
```

---

## 5. Atom Storage

### 5.1 Schema

```sql
CREATE TABLE atom (
    id BIGSERIAL PRIMARY KEY,
    hilbert_high BIGINT NOT NULL,
    hilbert_low BIGINT NOT NULL,
    geom GEOMETRY(GEOMETRYZM, 0) NOT NULL,  -- SRID 0 = no CRS
    is_constant BOOLEAN NOT NULL,
    refs BIGINT[],
    multiplicities INT[],
    content_hash BYTEA NOT NULL UNIQUE
);

-- Spatial index for geometry queries
CREATE INDEX idx_atom_geom ON atom USING GIST (geom);

-- B-tree on Hilbert for range queries
CREATE INDEX idx_atom_hilbert ON atom (hilbert_high, hilbert_low);

-- Hash index for O(1) deduplication lookup
CREATE INDEX idx_atom_hash ON atom USING HASH (content_hash);
```

### 5.2 Upsert Pattern

```sql
-- Atomic upsert: insert if new, return ID either way
INSERT INTO atom (hilbert_high, hilbert_low, geom, is_constant, refs, multiplicities, content_hash)
VALUES ($1, $2, $3, $4, $5, $6, $7)
ON CONFLICT (content_hash) DO UPDATE SET id = atom.id  -- No-op update to enable RETURNING
RETURNING id;
```

---

## 6. Embedding Storage

An N-dimensional embedding is stored as an N-point LineString. Each point is ON the hypersphere surface.

### 6.1 Point Coordinate Scheme

Each dimension becomes one point satisfying X² + Y² + Z² + M² = R²:

```cpp
constexpr double R = 1.0;  // Hypersphere radius
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

double sigmoid(double x) {
    return 1.0 / (1.0 + exp(-x));
}
```

Every point satisfies the hypersphere constraint. The embedding value (including sign) is encoded in the angular position.

### 6.2 Embedding to WKT

```cpp
std::string embedding_to_wkt(const std::vector<double>& embedding) {
    std::ostringstream wkt;
    wkt << std::setprecision(17) << "LINESTRING ZM(";

    size_t N = embedding.size();
    for (size_t i = 0; i < N; i++) {
        PointZM p = embedding_dim_to_point(embedding[i], i, N);
        
        if (i > 0) wkt << ", ";
        wkt << p.x << " " << p.y << " " << p.z << " " << p.m;
    }

    wkt << ")";
    return wkt.str();
}
```

### 6.3 Hilbert Index for Embeddings

Use the centroid of the trajectory for Hilbert indexing:

```cpp
HilbertIndex get_embedding_hilbert(const std::string& wkt) {
    // PostGIS computes centroid
    PointZM centroid = query_centroid(wkt);  // ST_Centroid
    return coords_to_hilbert(centroid.x, centroid.y, centroid.z, centroid.m);
}
```

Similar embeddings have similar centroids → similar Hilbert indices → efficient k-NN via B-tree range scan.

### 6.4 Embedding Reconstruction

```cpp
std::vector<double> wkt_to_embedding(const std::string& wkt) {
    // Inverse of embedding_dim_to_point
    // Extract polar angles from (x,y,z,m) and recover original values
    // Note: requires knowing total_dims to reconstruct properly
    std::vector<double> result;
    
    // Parse points from WKT and invert the angular mapping
    // phi = acos(z / R)
    // value = inverse_sigmoid(phi / PI)
    
    return result;
}
```

---

## 7. Weight Storage

### 7.1 Core Insight: Weights = Multiplicity

**We don't store weight values as atoms.** The weight magnitude is captured by edge multiplicity.

A weight matrix cell `W[i,j] = 0.87` means:
- Atom A (input i) connects to Atom B (output j)
- The strength 0.87 → normalized to multiplicity count
- Multiple edges = stronger connection

### 7.2 Weight Normalization to Multiplicity

```cpp
struct NormalizedWeight {
    int64_t input_atom;
    int64_t output_atom;
    int32_t multiplicity;  // 1-10 typically
};

// Normalize weight values to discrete multiplicity
std::vector<NormalizedWeight> normalize_weights(
    const double* weights,
    size_t rows,
    size_t cols,
    const std::vector<int64_t>& row_atoms,
    const std::vector<int64_t>& col_atoms,
    double threshold
) {
    std::vector<NormalizedWeight> result;
    
    // Find max weight for normalization
    double max_weight = 0.0;
    for (size_t i = 0; i < rows * cols; i++) {
        if (std::abs(weights[i]) >= threshold) {
            max_weight = std::max(max_weight, std::abs(weights[i]));
        }
    }
    
    if (max_weight == 0.0) return result;
    
    // Convert to multiplicity
    for (size_t i = 0; i < rows; i++) {
        for (size_t j = 0; j < cols; j++) {
            double w = weights[i * cols + j];
            if (std::abs(w) < threshold) continue;
            
            // Map [threshold, max] → [1, 10]
            int32_t mult = static_cast<int32_t>(
                1 + 9 * (std::abs(w) - threshold) / (max_weight - threshold)
            );
            
            result.push_back({row_atoms[i], col_atoms[j], mult});
        }
    }
    
    return result;
}
```

### 7.3 Edge Storage: Refs Only (No Stored Geometry)

Edge geometry is computed on demand, not stored. This eliminates redundancy.

```cpp
void store_edge(
    AtomStore& store,
    int64_t input_atom_id,
    int64_t output_atom_id,
    int32_t multiplicity
) {
    // refs = [input, output], mults encodes the weight strength
    std::vector<int64_t> refs = {input_atom_id, output_atom_id};
    std::vector<int32_t> mults = {1, multiplicity};
    
    // Compute Hilbert from midpoint of input/output coordinates
    PointZM a = store.get_coords(input_atom_id);
    PointZM b = store.get_coords(output_atom_id);
    PointZM mid = {(a.x+b.x)/2, (a.y+b.y)/2, (a.z+b.z)/2, (a.m+b.m)/2};
    HilbertIndex h = coords_to_hilbert(mid);
    
    // No geometry stored - compute on demand
    store.upsert_edge(h, refs, mults);
}

// Compute geometry on demand when spatial queries are needed
std::string get_edge_geometry(AtomStore& store, int64_t edge_id) {
    return store.query<std::string>(R"(
        SELECT ST_AsText(ST_MakeLine(a.geom, b.geom))
        FROM atom e
        JOIN atom a ON a.id = e.refs[1]
        JOIN atom b ON b.id = e.refs[2]
        WHERE e.id = $1
    )", edge_id);
}

// Query connection strength: sum of multiplicities
int64_t get_connection_strength(AtomStore& store, int64_t atom_a, int64_t atom_b) {
    return store.query<int64_t>(R"(
        SELECT COALESCE(SUM(multiplicities[2]), 0) FROM atom
        WHERE refs[1] = $1 AND refs[2] = $2
          AND is_constant = FALSE
          AND array_length(refs, 1) = 2
    )", atom_a, atom_b);
}
```

**Negative weights = spatial distance**: Atoms with positive weights (attraction) are close on the hypersphere. Atoms with negative weights (repulsion) are far apart. The distance between atom positions encodes relationship type; multiplicity encodes magnitude.
```

---

## 8. Run-Length Encoding

### 8.1 Data Structure

```cpp
struct RLESequence {
    std::vector<int64_t> refs;       // Unique atoms in order
    std::vector<int32_t> multiplicities;  // Count of each
};
```

### 8.2 Encode

```cpp
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

// "Hello" = [H, E, L, L, O]
// Encoded: refs=[H, E, L, O], multiplicities=[1, 1, 2, 1]
```

### 8.3 Decode

```cpp
std::vector<int64_t> rle_decode(const RLESequence& encoded) {
    std::vector<int64_t> result;

    for (size_t i = 0; i < encoded.refs.size(); i++) {
        for (int32_t j = 0; j < encoded.multiplicities[i]; j++) {
            result.push_back(encoded.refs[i]);
        }
    }

    return result;
}
```

---

## 9. Content Pair Encoding (CPE)

Like BPE, but operating on atom IDs. Learns common pairs and creates composition atoms for them.

### 9.1 Vocabulary

```cpp
struct CPEVocabulary {
    // Pair -> merged atom ID
    std::unordered_map<std::pair<int64_t, int64_t>, int64_t, PairHash> pair_to_atom;

    // Merged atom ID -> constituent pair
    std::unordered_map<int64_t, std::pair<int64_t, int64_t>> atom_to_pair;

    // Order in which merges were learned (for consistent application)
    std::vector<std::pair<int64_t, int64_t>> merge_order;
};
```

### 9.2 Training

```cpp
CPEVocabulary cpe_train(
    AtomStore& store,
    const std::vector<std::vector<int64_t>>& corpus,
    size_t max_merges,
    size_t min_frequency
) {
    CPEVocabulary vocab;
    std::vector<std::vector<int64_t>> working = corpus;

    while (vocab.merge_order.size() < max_merges) {
        // Count all adjacent pairs
        std::unordered_map<std::pair<int64_t, int64_t>, size_t, PairHash> counts;

        for (const auto& seq : working) {
            for (size_t i = 0; i + 1 < seq.size(); i++) {
                counts[{seq[i], seq[i + 1]}]++;
            }
        }

        // Find most frequent
        auto best = std::max_element(counts.begin(), counts.end(),
            [](const auto& a, const auto& b) { return a.second < b.second; });

        if (best == counts.end() || best->second < min_frequency) break;

        // Create composition atom for this pair
        std::vector<int64_t> refs = {best->first.first, best->first.second};
        std::vector<int32_t> mults = {1, 1};
        int64_t new_atom = store.create_composition(refs, mults);

        vocab.pair_to_atom[best->first] = new_atom;
        vocab.atom_to_pair[new_atom] = best->first;
        vocab.merge_order.push_back(best->first);

        // Replace in working corpus
        for (auto& seq : working) {
            seq = apply_single_merge(seq, best->first.first, best->first.second, new_atom);
        }
    }

    return vocab;
}

std::vector<int64_t> apply_single_merge(
    const std::vector<int64_t>& atoms,
    int64_t a, int64_t b, int64_t merged
) {
    std::vector<int64_t> result;

    for (size_t i = 0; i < atoms.size(); i++) {
        if (i + 1 < atoms.size() && atoms[i] == a && atoms[i + 1] == b) {
            result.push_back(merged);
            i++;  // Skip next
        } else {
            result.push_back(atoms[i]);
        }
    }

    return result;
}
```

### 9.3 Application

```cpp
std::vector<int64_t> cpe_apply(
    const std::vector<int64_t>& atoms,
    const CPEVocabulary& vocab
) {
    std::vector<int64_t> result = atoms;

    for (const auto& pair : vocab.merge_order) {
        int64_t merged = vocab.pair_to_atom.at(pair);
        result = apply_single_merge(result, pair.first, pair.second, merged);
    }

    return result;
}
```

### 9.4 Decoding (for reconstruction)

```cpp
std::vector<int64_t> cpe_decode(
    const std::vector<int64_t>& encoded,
    const CPEVocabulary& vocab
) {
    std::vector<int64_t> result;

    for (int64_t atom : encoded) {
        auto it = vocab.atom_to_pair.find(atom);
        if (it != vocab.atom_to_pair.end()) {
            // Recursively decode
            auto decoded = cpe_decode({it->second.first, it->second.second}, vocab);
            result.insert(result.end(), decoded.begin(), decoded.end());
        } else {
            result.push_back(atom);
        }
    }

    return result;
}
```

---

## 10. Spatial Query Operations

PostGIS functions become the semantic query language.

### 10.1 Similarity (Distance)

```sql
-- Find k nearest atoms to target
SELECT b.id, ST_Distance(a.geom, b.geom) AS distance
FROM atom a, atom b
WHERE a.id = $1 AND b.id != a.id
ORDER BY a.geom <-> b.geom  -- KNN operator
LIMIT $2;
```

### 10.2 Relatedness (Intersection)

```sql
-- Find compositions that share geometric space with target
SELECT b.id,
       ST_Length(ST_Intersection(a.geom, b.geom)) AS overlap_length,
       ST_Length(a.geom) AS a_length,
       ST_Length(b.geom) AS b_length
FROM atom a, atom b
WHERE a.id = $1
  AND b.is_constant = FALSE
  AND a.id != b.id
  AND ST_Intersects(a.geom, b.geom)
ORDER BY overlap_length DESC;
```

### 10.3 Containment

```sql
-- Find all compositions containing a specific atom
WITH RECURSIVE containing AS (
    SELECT id, refs, 1 AS depth
    FROM atom
    WHERE $1 = ANY(refs)

    UNION ALL

    SELECT a.id, a.refs, c.depth + 1
    FROM atom a
    JOIN containing c ON c.id = ANY(a.refs)
    WHERE a.is_constant = FALSE
)
SELECT * FROM containing ORDER BY depth;
```

### 10.4 Proximity Search

```sql
-- Find atoms within threshold distance
SELECT b.id, ST_Distance(a.geom, b.geom) AS distance
FROM atom a, atom b
WHERE a.id = $1
  AND ST_DWithin(a.geom, b.geom, $2)
  AND a.id != b.id
ORDER BY distance;
```

---

## 11. C++/C#/PostgreSQL Integration

### 11.1 C++ Native Interface

```cpp
extern "C" {
    // Projection
    void landmark_project_character(uint32_t codepoint, double* x, double* y, double* z, double* m);
    void landmark_project_number(double value, double* x, double* y, double* z, double* m);

    // Hilbert
    void coords_to_hilbert(double x, double y, double z, double m, uint64_t* high, uint64_t* low);
    void hilbert_to_coords(uint64_t high, uint64_t low, double* x, double* y, double* z, double* m);

    // Hashing
    void hash_constant(double x, double y, double z, double m, uint8_t* hash_out);
    void hash_bytes(const void* data, size_t len, uint8_t* hash_out);

    // Ingestion (returns atom ID)
    int64_t ingest_text(const char* text, size_t len, const char* conninfo);
    int64_t ingest_embedding(const double* data, size_t dims, int64_t token_id, const char* conninfo);
}
```

### 11.2 C# P/Invoke

```csharp
public static class HartonomousNative
{
    private const string LibName = "hartonomous_native";

    [DllImport(LibName)]
    public static extern void landmark_project_character(
        uint codepoint, out double x, out double y, out double z, out double m);

    [DllImport(LibName)]
    public static extern void landmark_project_number(
        double value, out double x, out double y, out double z, out double m);

    [DllImport(LibName)]
    public static extern void coords_to_hilbert(
        double x, double y, double z, double m, out ulong high, out ulong low);

    [DllImport(LibName)]
    public static extern long ingest_text(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        nuint len,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string conninfo);
}
```

---

## 12. Summary: Data Flow

```
INPUT (text, embedding, weights, any content)
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ ATOMIZATION                                                 │
│  • Characters → landmark_project_character() → PointZM      │
│  • Numbers → landmark_project_number() → PointZM            │
│  • Each constant: hash → Hilbert index → upsert atom        │
└─────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ RLE ENCODING                                                │
│  • Compress repeated atoms: [L, L, L] → [L×3]               │
│  • Reduce refs array size                                   │
└─────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ CPE ENCODING (optional)                                     │
│  • Merge common pairs into composition atoms                │
│  • [t, h] → [th], [t, h, e] → [the]                         │
│  • Further compression                                      │
└─────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ HIERARCHICAL COMPOSITION                                    │
│  • Characters → words → sentences → paragraphs → documents  │
│  • Embeddings → LineStrings                                 │
│  • Weights → 2-point LineStrings [A, B] with multiplicity   │
└─────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ INDEXING                                                    │
│  • coords_to_hilbert() → (high, low)                        │
│  • GiST spatial index on geom                               │
│  • B-tree on (hilbert_high, hilbert_low)                    │
└─────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ STORAGE                                                     │
│  • Single atom table                                        │
│  • PostGIS geometries (PointZM, LineStringZM)               │
│  • BLAKE3-256 content hash for deduplication                │
└─────────────────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────────────────┐
│ QUERY                                                       │
│  • ST_Distance = semantic similarity                        │
│  • ST_Intersects = semantic relatedness                     │
│  • Traversal = inference                                    │
│  • The database IS the AI                                   │
└─────────────────────────────────────────────────────────────┘
```
