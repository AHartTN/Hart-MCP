#include "hartonomous/content_hash.h"
#include "blake3.h"
#include <string.h>
#include <stdio.h>

void hart_hash_constant(const Point4D* point, ContentHash* out) {
    if (!point || !out) return;
    
    // Hash the exact double values (8 bytes each)
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    
    blake3_hasher_update(&hasher, &point->x, sizeof(double));
    blake3_hasher_update(&hasher, &point->y, sizeof(double));
    blake3_hasher_update(&hasher, &point->z, sizeof(double));
    blake3_hasher_update(&hasher, &point->m, sizeof(double));
    
    blake3_hasher_finalize(&hasher, out->bytes, 32);
}

void hart_hash_composition(
    const ContentHash* child_hashes,
    size_t num_children,
    const int32_t* multiplicities,
    ContentHash* out)
{
    if (!child_hashes || !out || num_children == 0) return;
    
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    
    // Hash children in order with their multiplicities
    for (size_t i = 0; i < num_children; i++) {
        blake3_hasher_update(&hasher, child_hashes[i].bytes, 32);
        
        if (multiplicities) {
            blake3_hasher_update(&hasher, &multiplicities[i], sizeof(int32_t));
        } else {
            int32_t one = 1;
            blake3_hasher_update(&hasher, &one, sizeof(int32_t));
        }
    }
    
    blake3_hasher_finalize(&hasher, out->bytes, 32);
}

void hart_hash_bytes(const void* data, size_t len, ContentHash* out) {
    if (!data || !out || len == 0) return;
    
    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    blake3_hasher_update(&hasher, data, len);
    blake3_hasher_finalize(&hasher, out->bytes, 32);
}

int hart_hash_equal(const ContentHash* a, const ContentHash* b) {
    if (!a || !b) return 0;
    return memcmp(a->bytes, b->bytes, 32) == 0;
}

void hart_hash_to_hex(const ContentHash* hash, char* out) {
    if (!hash || !out) return;
    
    for (int i = 0; i < 32; i++) {
        sprintf(out + (i * 2), "%02x", hash->bytes[i]);
    }
    out[64] = '\0';
}
