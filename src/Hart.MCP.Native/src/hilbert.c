/**
 * hilbert.c - 4D Hilbert space-filling curve implementation
 * 
 * CRITICAL INVARIANTS:
 * 1. coords_to_hilbert(hilbert_to_coords(h)) == h (modulo quantization)
 * 2. Locality: nearby 4D points map to nearby Hilbert indices
 * 3. Deterministic: identical inputs ALWAYS produce identical outputs
 * 4. No data loss within quantization precision
 * 
 * All operations use explicit type casts to prevent truncation.
 */

#include "hartonomous/hilbert.h"
#include <string.h>
#include <math.h>

#define HYPERSPHERE_RADIUS 1.0
#define COORD_MIN (-HYPERSPHERE_RADIUS)
#define COORD_MAX (HYPERSPHERE_RADIUS)

/* ============================================================================
 * Gray Code Conversions - 4-bit versions for Hilbert curve operations
 * Gray code ensures only one bit changes between adjacent values
 * ============================================================================ */

static inline uint8_t binary_to_gray_4(uint8_t n) {
    return (uint8_t)(n ^ (n >> 1));
}

static inline uint8_t gray_to_binary_4(uint8_t g) {
    uint8_t n = g;
    n = (uint8_t)(n ^ (n >> 2));
    n = (uint8_t)(n ^ (n >> 1));
    return n;
}

/* ============================================================================
 * Coordinate Quantization
 * Maps continuous [-1, 1] to discrete [0, 2^16-1]
 * ============================================================================ */

uint32_t quantize_coord(double value, double min, double max) {
    double normalized = (value - min) / (max - min);
    
    /* Clamp to [0, 1] */
    if (normalized < 0.0) normalized = 0.0;
    if (normalized > 1.0) normalized = 1.0;
    
    uint32_t max_val = (1U << HILBERT_BITS_PER_DIM) - 1;
    return (uint32_t)(normalized * (double)max_val + 0.5); /* Round to nearest */
}

double dequantize_coord(uint32_t quantized, double min, double max) {
    uint32_t max_val = (1U << HILBERT_BITS_PER_DIM) - 1;
    double normalized = (double)quantized / (double)max_val;
    return normalized * (max - min) + min;
}

/* ============================================================================
 * 4D Hilbert Curve Rotation State
 * ============================================================================ */

typedef struct {
    uint8_t perm[4];   /* Dimension permutation [0-3] */
    uint8_t flip;      /* Flip mask (4 bits, one per dimension) */
} RotationState;

static const RotationState INITIAL_STATE = {{0, 1, 2, 3}, 0};

static void update_rotation(RotationState* state, uint8_t gray_code) {
    uint8_t axis = 0;
    for (uint8_t i = 0; i < 4; i++) {
        if (gray_code & (uint8_t)(1U << i)) {
            axis = i;
        }
    }
    
    if (gray_code != 0 && gray_code != 15) {
        uint8_t d1 = state->perm[0];
        uint8_t d2 = state->perm[axis];
        state->perm[0] = d2;
        state->perm[axis] = d1;
        state->flip = (uint8_t)(state->flip ^ (1U << d1));
    }
}

/* ============================================================================
 * Forward Transform: 4D Coordinates -> Hilbert Index
 * ============================================================================ */

HilbertIndex coords_to_hilbert(double x, double y, double z, double m) {
    uint32_t coords[4] = {
        quantize_coord(x, COORD_MIN, COORD_MAX),
        quantize_coord(y, COORD_MIN, COORD_MAX),
        quantize_coord(z, COORD_MIN, COORD_MAX),
        quantize_coord(m, COORD_MIN, COORD_MAX)
    };
    
    HilbertIndex result = {0, 0};
    RotationState state = INITIAL_STATE;
    
    for (int bit = HILBERT_BITS_PER_DIM - 1; bit >= 0; bit--) {
        uint8_t bits = 0;
        for (uint8_t d = 0; d < 4; d++) {
            if (coords[state.perm[d]] & (1U << bit)) {
                bits = (uint8_t)(bits | (1U << d));
            }
        }
        
        bits = (uint8_t)(bits ^ state.flip);
        uint8_t gray = binary_to_gray_4(bits);
        
        int shift_amount = bit * 4;
        if (shift_amount < 64) {
            result.low |= ((uint64_t)gray << shift_amount);
        } else {
            result.high |= ((uint64_t)gray << (shift_amount - 64));
        }
        
        update_rotation(&state, gray);
    }
    
    return result;
}

HART_API HilbertIndex point_to_hilbert(PointZM p) {
    return coords_to_hilbert(p.x, p.y, p.z, p.m);
}

/* ============================================================================
 * Inverse Transform: Hilbert Index -> 4D Coordinates
 * ============================================================================ */

void hilbert_to_coords(HilbertIndex h, double* x, double* y, double* z, double* m) {
    uint32_t coords[4] = {0, 0, 0, 0};
    RotationState state = INITIAL_STATE;
    
    for (int bit = HILBERT_BITS_PER_DIM - 1; bit >= 0; bit--) {
        int shift_amount = bit * 4;
        uint8_t gray;
        if (shift_amount < 64) {
            gray = (uint8_t)((h.low >> shift_amount) & 0xFU);
        } else {
            gray = (uint8_t)((h.high >> (shift_amount - 64)) & 0xFU);
        }
        
        uint8_t bits = gray_to_binary_4(gray);
        bits = (uint8_t)(bits ^ state.flip);
        
        for (uint8_t d = 0; d < 4; d++) {
            if (bits & (uint8_t)(1U << d)) {
                coords[state.perm[d]] |= (1U << bit);
            }
        }
        
        update_rotation(&state, gray);
    }
    
    *x = dequantize_coord(coords[0], COORD_MIN, COORD_MAX);
    *y = dequantize_coord(coords[1], COORD_MIN, COORD_MAX);
    *z = dequantize_coord(coords[2], COORD_MIN, COORD_MAX);
    *m = dequantize_coord(coords[3], COORD_MIN, COORD_MAX);
}

HART_API PointZM hilbert_to_point(HilbertIndex h) {
    PointZM p;
    hilbert_to_coords(h, &p.x, &p.y, &p.z, &p.m);
    return p;
}

HilbertIndex seed_to_hilbert(const AtomSeed* seed) {
    Point4D p = compute_coords_from_seed(seed);
    return coords_to_hilbert(p.x, p.y, p.z, p.m);
}

uint64_t hilbert_distance(HilbertIndex a, HilbertIndex b) {
    if (a.low > b.low) return a.low - b.low;
    return b.low - a.low;
}
