#ifndef HARTONOMOUS_SIMD_H
#define HARTONOMOUS_SIMD_H

#include "types.h"
#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * SIMD Capabilities Detection
 */
typedef struct {
    int has_sse2;
    int has_sse41;
    int has_avx;
    int has_avx2;
    int has_avx512f;
} SIMDCapabilities;

/** Detect SIMD capabilities at runtime */
HART_API SIMDCapabilities detect_simd_capabilities(void);

/** Get string description of SIMD capabilities */
HART_API const char* simd_capabilities_string(void);

/**
 * SIMD-Accelerated Distance Computations
 */

/** Compute 4D Euclidean distance between two points */
HART_API double distance_4d(double x1, double y1, double z1, double m1,
                            double x2, double y2, double z2, double m2);

/** Compute squared 4D Euclidean distance (faster, no sqrt) */
HART_API double distance_4d_squared(double x1, double y1, double z1, double m1,
                                    double x2, double y2, double z2, double m2);

/**
 * Batch compute distances from one point to many points
 * Uses AVX2/AVX-512 when available for 4x-8x speedup
 * 
 * @param qx, qy, qz, qm  Query point coordinates
 * @param xs, ys, zs, ms  Arrays of target point coordinates
 * @param distances       Output array for computed distances
 * @param count           Number of points to process
 */
HART_API void batch_distance_4d(
    double qx, double qy, double qz, double qm,
    const double* xs, const double* ys, 
    const double* zs, const double* ms,
    double* distances,
    size_t count
);

/**
 * SIMD-Accelerated Attention/Softmax
 */

/**
 * Compute attention weights from distances (softmax over inverse distances)
 * w_i = (1 / (1 + d_i)) / sum(1 / (1 + d_j))
 * 
 * @param distances     Input distance array
 * @param weights       Output normalized weight array
 * @param count         Number of elements
 */
HART_API void compute_attention_weights(
    const double* distances,
    double* weights,
    size_t count
);

/**
 * SIMD-Accelerated Vector Operations
 */

/** Add two 4D vectors */
HART_API void vector_add_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2,
    double* rx, double* ry, double* rz, double* rm
);

/** Subtract two 4D vectors */
HART_API void vector_sub_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2,
    double* rx, double* ry, double* rz, double* rm
);

/** Scale 4D vector by scalar */
HART_API void vector_scale_4d(
    double x, double y, double z, double m,
    double scalar,
    double* rx, double* ry, double* rz, double* rm
);

/** Dot product of two 4D vectors */
HART_API double vector_dot_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2
);

/** Compute magnitude of 4D vector */
HART_API double vector_magnitude_4d(double x, double y, double z, double m);

/** Normalize 4D vector to unit length */
HART_API void vector_normalize_4d(
    double x, double y, double z, double m,
    double* rx, double* ry, double* rz, double* rm
);

/**
 * SIMD-Accelerated Batch Operations
 */

/**
 * Batch normalize many 4D vectors
 * Uses AVX2 for parallel processing
 */
HART_API void batch_normalize_4d(
    double* xs, double* ys, double* zs, double* ms,
    size_t count
);

/**
 * Compute centroid of multiple 4D points
 */
HART_API void compute_centroid_4d(
    const double* xs, const double* ys,
    const double* zs, const double* ms,
    size_t count,
    double* cx, double* cy, double* cz, double* cm
);

/**
 * SIMD-Accelerated Hash Operations
 */

/**
 * Batch compute BLAKE3 hashes for multiple seeds
 * More efficient than calling compute_seed_hash repeatedly
 */
HART_API void batch_compute_seed_hashes(
    const uint32_t* seeds,
    ContentHash* hashes,
    size_t count
);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_SIMD_H
