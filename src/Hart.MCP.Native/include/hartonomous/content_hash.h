#ifndef HARTONOMOUS_CONTENT_HASH_H
#define HARTONOMOUS_CONTENT_HASH_H

#include "types.h"    // Use centralized type definitions
#include "atom_seed.h"
#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// ContentHash is now defined in types.h
// Keep HASH_SIZE for compatibility
#define HASH_SIZE 32

// Compute hash from a 32-bit seed value (used for Unicode codepoints)
// Same seed → identical hash across all platforms
HART_API ContentHash compute_seed_hash(uint32_t seed);

// Hash a seed structure (lossless, deterministic)
// Same seed → identical hash across all platforms
HART_API ContentHash hash_seed(const AtomSeed* seed);

// Hash a composition (Merkle DAG node)
// Hash of child hashes + multiplicities
HART_API ContentHash hash_composition(
    const ContentHash* child_hashes,
    const int32_t* multiplicities,
    size_t count
);

// Hash raw bytes (for embeddings, etc.)
HART_API ContentHash hash_bytes(const void* data, size_t len);

// Compare hashes
HART_API int hash_equal(const ContentHash* a, const ContentHash* b);

// Convert hash to hex string (for display)
HART_API void hash_to_hex(const ContentHash* hash, char* out);  // out must be 65 bytes

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_CONTENT_HASH_H
