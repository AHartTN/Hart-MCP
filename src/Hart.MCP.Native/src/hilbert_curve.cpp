#include "hartonomous/hilbert_curve.h"
#include <math.h>
#include <string.h>

// Gray code conversions
static inline uint32_t binary_to_gray(uint32_t n) {
    return n ^ (n >> 1);
}

static inline uint32_t gray_to_binary(uint32_t g) {
    uint32_t b = g;
    while (g >>= 1) {
        b ^= g;
    }
    return b;
}

// 4D Hilbert curve implementation
// Based on: "Programming the Hilbert Curve" by John Skilling
// Adapted for 4 dimensions with 32 bits per dimension

#define DIMS 4
#define BITS 32

// Rotation tables for 4D
static void rotate_right(uint32_t coords[DIMS], int num_bits) {
    uint32_t temp = coords[DIMS-1];
    for (int i = DIMS-1; i > 0; i--) {
        coords[i] = coords[i-1];
    }
    coords[0] = temp;
}

// Transform coordinates based on Gray code state
static void hilbert_transform(uint32_t coords[DIMS], int b, int d) {
    uint32_t t = coords[d] ^ coords[0];
    for (int i = 1; i < DIMS; i++) {
        coords[i] ^= coords[0];
    }
    
    uint32_t flip = (1U << b) - 1;
    coords[d] ^= flip;
}

void hart_coords_to_hilbert(const Point4D* point, HilbertIndex* out) {
    if (!point || !out) return;
    
    // Map [-1, 1] coordinates to [0, 2^32-1] unsigned integers
    // NO QUANTIZATION - we use full double precision then map to uint32 range
    uint32_t coords[DIMS];
    coords[0] = (uint32_t)((point->x + 1.0) * 0.5 * 4294967295.0);
    coords[1] = (uint32_t)((point->y + 1.0) * 0.5 * 4294967295.0);
    coords[2] = (uint32_t)((point->z + 1.0) * 0.5 * 4294967295.0);
    coords[3] = (uint32_t)((point->m + 1.0) * 0.5 * 4294967295.0);
    
    // Convert to Gray code
    for (int i = 0; i < DIMS; i++) {
        coords[i] = binary_to_gray(coords[i]);
    }
    
    // Initialize Hilbert index (128 bits = 4 * 32 bits)
    uint64_t hilbert[2] = {0, 0};  // high and low 64 bits
    
    // Process bits from most significant to least significant
    for (int b = BITS - 1; b >= 0; b--) {
        // Extract bit b from each coordinate
        uint32_t bits = 0;
        for (int d = 0; d < DIMS; d++) {
            if (coords[d] & (1U << b)) {
                bits |= (1U << d);
            }
        }
        
        // Append to Hilbert index
        int bit_pos = b * DIMS;
        if (bit_pos < 64) {
            hilbert[1] |= ((uint64_t)bits << bit_pos);
        } else {
            hilbert[0] |= ((uint64_t)bits << (bit_pos - 64));
        }
        
        // Apply transformation for next bit
        if (b > 0) {
            hilbert_transform(coords, b, bits);
        }
    }
    
    out->high = hilbert[0];
    out->low = hilbert[1];
}

void hart_hilbert_to_coords(const HilbertIndex* index, Point4D* out) {
    if (!index || !out) return;
    
    // Extract 128 bits
    uint64_t hilbert[2];
    hilbert[0] = index->high;
    hilbert[1] = index->low;
    
    // Initialize coordinates
    uint32_t coords[DIMS] = {0, 0, 0, 0};
    
    // Process bits from most significant to least significant
    for (int b = BITS - 1; b >= 0; b--) {
        // Extract 4 bits from Hilbert index
        int bit_pos = b * DIMS;
        uint32_t bits;
        if (bit_pos < 64) {
            bits = (uint32_t)((hilbert[1] >> bit_pos) & 0xF);
        } else {
            bits = (uint32_t)((hilbert[0] >> (bit_pos - 64)) & 0xF);
        }
        
        // Inverse transform
        if (b < BITS - 1) {
            hilbert_transform(coords, b + 1, bits);
        }
        
        // Set bit b in each coordinate
        for (int d = 0; d < DIMS; d++) {
            if (bits & (1U << d)) {
                coords[d] |= (1U << b);
            }
        }
    }
    
    // Convert from Gray code
    for (int i = 0; i < DIMS; i++) {
        coords[i] = gray_to_binary(coords[i]);
    }
    
    // Map [0, 2^32-1] back to [-1, 1]
    out->x = ((double)coords[0] / 4294967295.0) * 2.0 - 1.0;
    out->y = ((double)coords[1] / 4294967295.0) * 2.0 - 1.0;
    out->z = ((double)coords[2] / 4294967295.0) * 2.0 - 1.0;
    out->m = ((double)coords[3] / 4294967295.0) * 2.0 - 1.0;
    
    // Renormalize to hypersphere surface
    double norm = sqrt(out->x * out->x + out->y * out->y + out->z * out->z + out->m * out->m);
    if (norm > 0.0) {
        out->x /= norm;
        out->y /= norm;
        out->z /= norm;
        out->m /= norm;
    }
}

uint64_t hart_hilbert_distance(const HilbertIndex* a, const HilbertIndex* b) {
    if (!a || !b) return 0;
    
    // Compute absolute difference treating as 128-bit unsigned integers
    // Simplified: just use high 64 bits for approximate distance
    uint64_t diff_high = a->high > b->high ? a->high - b->high : b->high - a->high;
    
    if (diff_high > 0) return diff_high;
    
    // If high bits are equal, check low bits
    uint64_t diff_low = a->low > b->low ? a->low - b->low : b->low - a->low;
    return diff_low;
}
