#ifndef HARTONOMOUS_HILBERT_CURVE_H
#define HARTONOMOUS_HILBERT_CURVE_H

#include "types.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Convert 4D coordinates to 128-bit Hilbert index.
 * 
 * PURPOSE: Space-filling curve for efficient spatial indexing.
 * Preserves locality: Points close in 4D space have numerically close Hilbert indices.
 * 
 * GUARANTEES:
 * - Deterministic: Same coordinates always produce same index
 * - Locality preserving: Spatial distance ≈ Hilbert distance
 * - Reversible: Can convert back to approximate coordinates
 * 
 * IMPORTANT: This is for INDEXING only. Reconstruction uses original geom, not Hilbert.
 * 
 * @param point 4D coordinates (must satisfy x² + y² + z² + m² ≈ 1.0)
 * @param out Output Hilbert index (128 bits split into high/low)
 */
void hart_coords_to_hilbert(const Point4D* point, HilbertIndex* out);

/**
 * Convert 128-bit Hilbert index back to approximate 4D coordinates.
 * 
 * NOTE: This is lossy due to finite Hilbert precision (128 bits for continuous 4D space).
 * Used for visualization/exploration, NOT for exact reconstruction.
 * 
 * @param index Hilbert index (128 bits)
 * @param out Output approximate 4D coordinates
 */
void hart_hilbert_to_coords(const HilbertIndex* index, Point4D* out);

/**
 * Compute distance between two Hilbert indices.
 * Approximates spatial distance without decompressing to 4D.
 * 
 * @param a First Hilbert index
 * @param b Second Hilbert index
 * @return Absolute difference (unsigned 128-bit arithmetic)
 */
uint64_t hart_hilbert_distance(const HilbertIndex* a, const HilbertIndex* b);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_HILBERT_CURVE_H
