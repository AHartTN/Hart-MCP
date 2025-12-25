#ifndef HARTONOMOUS_LANDMARK_PROJECTION_H
#define HARTONOMOUS_LANDMARK_PROJECTION_H

#include "types.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Project a Unicode codepoint onto the 4D hypersphere surface.
 * 
 * GUARANTEES:
 * - Deterministic: Same codepoint always produces identical coordinates
 * - Lossless: Can reconstruct codepoint from coordinates (via lookup table)
 * - Hypersphere constraint: x² + y² + z² + m² = 1.0 (unit sphere)
 * - Clustering: Related characters (A/a, e/è) are spatially close
 * 
 * @param codepoint Unicode codepoint (0 to 0x10FFFF = 1,114,111 max)
 * @param out Output point (must not be NULL)
 */
void hart_landmark_project_character(uint32_t codepoint, Point4D* out);

/**
 * Project a double-precision number onto the 4D hypersphere surface.
 * 
 * GUARANTEES:
 * - Deterministic: Same number always produces identical coordinates
 * - Lossless: Can reconstruct exact number from coordinates
 * - Hypersphere constraint: x² + y² + z² + m² = 1.0
 * - Monotonic: Larger numbers map to "further" positions
 * 
 * @param value Double-precision number (any finite value)
 * @param out Output point (must not be NULL)
 */
void hart_landmark_project_number(double value, Point4D* out);

/**
 * Get character category for a Unicode codepoint.
 * Used internally for segmentation in landmark projection.
 * 
 * @param codepoint Unicode codepoint
 * @return Character category
 */
CharCategory hart_get_char_category(uint32_t codepoint);

/**
 * Reverse lookup: Find codepoint from 4D coordinates.
 * Uses precomputed lookup table for all 1.1M Unicode codepoints.
 * 
 * @param point 4D coordinates
 * @param tolerance Distance tolerance for matching
 * @param out_codepoint Output codepoint (if found)
 * @return HART_OK if found, HART_ERROR_NOT_FOUND otherwise
 */
HartResult hart_reverse_lookup_character(const Point4D* point, double tolerance, uint32_t* out_codepoint);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_LANDMARK_PROJECTION_H
