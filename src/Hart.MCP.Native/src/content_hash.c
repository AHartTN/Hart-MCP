/**
 * content_hash.c - BLAKE3-256 content addressing
 * 
 * CRITICAL INVARIANTS:
 * 1. Same content ALWAYS produces same hash (deterministic)
 * 2. Different content produces different hash (collision resistant)
 * 3. Order-dependent: hash([A,B]) != hash([B,A])
 * 4. Multiplicities affect hash: hash([A,1]) != hash([A,2])
 */

#include "hartonomous/content_hash.h"
#include "blake3.h"
#include <string.h>
#include <stdio.h>

#ifdef _WIN32
#define SPRINTF_S(buf, size, fmt, ...) sprintf_s(buf, size, fmt, __VA_ARGS__)
#else
#define SPRINTF_S(buf, size, fmt, ...) snprintf(buf, size, fmt, __VA_ARGS__)
#endif

HART_API ContentHash compute_seed_hash(uint32_t seed) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    blake3_hasher_update(&hasher, &seed, sizeof(uint32_t));
    
    ContentHash out;
    blake3_hasher_finalize(&hasher, out.bytes, HASH_SIZE);
    return out;
}

HART_API ContentHash compute_composition_hash(
    const int64_t* refs,
    const int32_t* multiplicities,
    int count
) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    
    for (int i = 0; i < count; i++) {
        blake3_hasher_update(&hasher, &refs[i], sizeof(int64_t));
        blake3_hasher_update(&hasher, &multiplicities[i], sizeof(int32_t));
    }
    
    ContentHash out;
    blake3_hasher_finalize(&hasher, out.bytes, HASH_SIZE);
    return out;
}

ContentHash hash_seed(const AtomSeed* seed) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    
    blake3_hasher_update(&hasher, &seed->type, sizeof(SeedType));
    
    uint64_t value_bits = 0;
    switch (seed->type) {
        case SEED_UNICODE:
            value_bits = (uint64_t)seed->value.codepoint;
            break;
        case SEED_INTEGER:
            memcpy(&value_bits, &seed->value.integer, sizeof(int64_t));
            break;
        case SEED_FLOAT_BITS:
            value_bits = seed->value.float_bits;
            break;
        default:
            break;
    }
    blake3_hasher_update(&hasher, &value_bits, sizeof(uint64_t));
    
    ContentHash out;
    blake3_hasher_finalize(&hasher, out.bytes, HASH_SIZE);
    return out;
}

ContentHash hash_composition(
    const ContentHash* child_hashes,
    const int32_t* multiplicities,
    size_t count
) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    
    for (size_t i = 0; i < count; i++) {
        blake3_hasher_update(&hasher, child_hashes[i].bytes, HASH_SIZE);
        blake3_hasher_update(&hasher, &multiplicities[i], sizeof(int32_t));
    }
    
    ContentHash out;
    blake3_hasher_finalize(&hasher, out.bytes, HASH_SIZE);
    return out;
}

ContentHash hash_bytes(const void* data, size_t len) {
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    blake3_hasher_update(&hasher, data, len);
    
    ContentHash out;
    blake3_hasher_finalize(&hasher, out.bytes, HASH_SIZE);
    return out;
}

int hash_equal(const ContentHash* a, const ContentHash* b) {
    return memcmp(a->bytes, b->bytes, HASH_SIZE) == 0;
}

void hash_to_hex(const ContentHash* hash, char* out) {
    for (int i = 0; i < HASH_SIZE; i++) {
        SPRINTF_S(out + i * 2, 3, "%02x", hash->bytes[i]);
    }
    out[HASH_SIZE * 2] = '\0';
}
