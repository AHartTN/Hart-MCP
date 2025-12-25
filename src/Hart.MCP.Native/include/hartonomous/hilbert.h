#ifndef HARTONOMOUS_HILBERT_H
#define HARTONOMOUS_HILBERT_H

#include "types.h"    // Use centralized type definitions
#include "atom_seed.h"
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// HilbertIndex is now defined in types.h

// Configuration
#define HILBERT_BITS_PER_DIM 16  // 16 bits per dimension = 64 total bits
#define HILBERT_DIMENSIONS 4

// Forward transform: 4D coordinates → Hilbert index
// Preserves spatial locality (nearby points → nearby indices)
HART_API HilbertIndex coords_to_hilbert(double x, double y, double z, double m);

// Inverse transform: Hilbert index → 4D coordinates  
HART_API void hilbert_to_coords(HilbertIndex h, double* x, double* y, double* z, double* m);

// Compute Hilbert index directly from seed (avoids floating-point)
// More deterministic than coords_to_hilbert(compute_coords_from_seed(seed))
HART_API HilbertIndex seed_to_hilbert(const AtomSeed* seed);

// Distance between Hilbert indices
HART_API uint64_t hilbert_distance(HilbertIndex a, HilbertIndex b);

// Utility: quantize double → uint32 for Hilbert computation
HART_API uint32_t quantize_coord(double value, double min, double max);

// Utility: dequantize uint32 → double
HART_API double dequantize_coord(uint32_t quantized, double min, double max);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_HILBERT_H
