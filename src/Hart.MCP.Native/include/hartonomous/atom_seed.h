#ifndef HARTONOMOUS_ATOM_SEED_H
#define HARTONOMOUS_ATOM_SEED_H

#include "types.h"  // Use centralized type definitions
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// LOSSLESS DETERMINISTIC SEED (integer-only storage)
typedef enum {
    SEED_UNICODE = 0,      // Unicode codepoint
    SEED_INTEGER = 1,      // int64_t value
    SEED_FLOAT_BITS = 2,   // IEEE754 double as uint64
    SEED_COMPOSITION = 3   // refs + multiplicities (no seed value)
} SeedType;

typedef struct {
    SeedType type;
    union {
        uint32_t codepoint;    // For SEED_UNICODE
        int64_t integer;       // For SEED_INTEGER
        uint64_t float_bits;   // For SEED_FLOAT_BITS (IEEE754 double)
    } value;
} AtomSeed;

// Point4D is now defined in types.h
// Alias for clarity
typedef Point4D PointZM;

// Create seeds from values
HART_API AtomSeed seed_from_codepoint(uint32_t codepoint);
HART_API AtomSeed seed_from_integer(int64_t value);
HART_API AtomSeed seed_from_double(double value);

// Deterministic projection: seed → coordinates
// ALWAYS produces identical output for same input
// Platform-independent (uses IEEE754 standard)
HART_API Point4D compute_coords_from_seed(const AtomSeed* seed);

// Verify hypersphere constraint
HART_API int verify_on_sphere(const Point4D* p, double tolerance);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_ATOM_SEED_H
