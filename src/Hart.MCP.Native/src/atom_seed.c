#include "hartonomous/atom_seed.h"
#include <string.h>
#include <math.h>

#define HYPERSPHERE_RADIUS 1.0
#define M_PI 3.14159265358979323846
#define GOLDEN_RATIO 1.6180339887498948482
#define GOLDEN_ANGLE (2.0 * M_PI / (GOLDEN_RATIO * GOLDEN_RATIO))

AtomSeed seed_from_codepoint(uint32_t codepoint) {
    AtomSeed seed;
    seed.type = SEED_UNICODE;
    seed.value.codepoint = codepoint;
    return seed;
}

AtomSeed seed_from_integer(int64_t value) {
    AtomSeed seed;
    seed.type = SEED_INTEGER;
    seed.value.integer = value;
    return seed;
}

AtomSeed seed_from_double(double value) {
    AtomSeed seed;
    seed.type = SEED_FLOAT_BITS;
    memcpy(&seed.value.float_bits, &value, sizeof(double));
    return seed;
}

// Category segmentation for Unicode (1.1M codepoints)
static const uint32_t UNICODE_MAX = 0x10FFFF;
static const double PSI_SEGMENT_SIZE = M_PI / 16.0;  // 16 major categories

static int get_category(uint32_t codepoint) {
    if (codepoint <= 0x1F || codepoint == 0x7F) return 0;  // Control
    if (codepoint >= 0x30 && codepoint <= 0x39) return 1;  // Digits
    if (codepoint >= 0x41 && codepoint <= 0x5A) return 2;  // Upper
    if (codepoint >= 0x61 && codepoint <= 0x7A) return 3;  // Lower
    if (codepoint >= 0x20 && codepoint <= 0x7E) return 4;  // ASCII punctuation
    if (codepoint >= 0x80 && codepoint <= 0x024F) return 5;  // Latin extended
    if (codepoint >= 0x0370 && codepoint <= 0x03FF) return 6;  // Greek
    if (codepoint >= 0x0400 && codepoint <= 0x04FF) return 7;  // Cyrillic
    if (codepoint >= 0x4E00 && codepoint <= 0x9FFF) return 8;  // CJK
    if (codepoint >= 0x1F600) return 9;  // Emoji
    return 10;  // Other
}

Point4D compute_coords_from_seed(const AtomSeed* seed) {
    Point4D p;
    double psi, theta, phi;
    const double R = HYPERSPHERE_RADIUS;
    
    switch (seed->type) {
        case SEED_UNICODE: {
            uint32_t cp = seed->value.codepoint;
            int category = get_category(cp);
            
            // ψ determined by category (latitude bands)
            psi = (category + 0.5) * PSI_SEGMENT_SIZE;
            
            // θ and φ use golden angle spiral within category
            // Deterministic distribution based on codepoint value
            uint32_t index_in_cat = cp % 10000;  // Spread within category
            theta = fmod(index_in_cat * GOLDEN_ANGLE, M_PI);
            phi = fmod(cp * GOLDEN_ANGLE * 1.5, 2.0 * M_PI);
            
            // Small perturbation based on exact codepoint for uniqueness
            double perturb = (cp % 1000) / 100000.0;
            psi += perturb * PSI_SEGMENT_SIZE * 0.1;
            break;
        }
        
        case SEED_INTEGER: {
            int64_t val = seed->value.integer;
            int is_negative = (val < 0);
            uint64_t abs_val = (uint64_t)(is_negative ? -val : val);
            
            // Sign determines hemisphere
            psi = is_negative ? M_PI * 0.25 : M_PI * 0.75;
            
            // Magnitude determines position within hemisphere
            theta = fmod(abs_val * GOLDEN_ANGLE, M_PI);
            phi = fmod(abs_val * GOLDEN_ANGLE * GOLDEN_RATIO, 2.0 * M_PI);
            
            // Small variation for exact value
            psi += (abs_val % 1000) / 10000.0;
            break;
        }
        
        case SEED_FLOAT_BITS: {
            uint64_t bits = seed->value.float_bits;
            
            // Extract IEEE754 components (deterministic across platforms)
            uint64_t sign = (bits >> 63) & 0x1;
            uint64_t exponent = (bits >> 52) & 0x7FF;
            uint64_t mantissa = bits & 0xFFFFFFFFFFFFFULL;
            
            // Map to angles using bit patterns
            psi = (exponent / 2048.0) * M_PI;
            theta = ((mantissa >> 32) / (double)0xFFFFF) * M_PI;
            phi = ((mantissa & 0xFFFFFFFF) / (double)0xFFFFFFFF) * 2.0 * M_PI;
            
            // Sign affects phi
            if (sign) phi += M_PI;
            break;
        }
        
        default:
            // Invalid seed type - return origin
            p.x = p.y = p.z = 0.0;
            p.m = R;
            return p;
    }
    
    // Clamp angles to valid ranges
    if (psi < 0.001) psi = 0.001;
    if (psi > M_PI - 0.001) psi = M_PI - 0.001;
    if (theta < 0.001) theta = 0.001;
    if (theta > M_PI - 0.001) theta = M_PI - 0.001;
    while (phi < 0) phi += 2.0 * M_PI;
    while (phi >= 2.0 * M_PI) phi -= 2.0 * M_PI;
    
    // Convert spherical → Cartesian
    // X² + Y² + Z² + M² = R²
    double sin_psi = sin(psi);
    double cos_psi = cos(psi);
    double sin_theta = sin(theta);
    double cos_theta = cos(theta);
    double sin_phi = sin(phi);
    double cos_phi = cos(phi);
    
    p.x = R * sin_psi * sin_theta * cos_phi;
    p.y = R * sin_psi * sin_theta * sin_phi;
    p.z = R * sin_psi * cos_theta;
    p.m = R * cos_psi;
    
    return p;
}

HART_API PointZM project_seed_to_hypersphere(uint32_t seed) {
    AtomSeed s;
    s.type = SEED_UNICODE;
    s.value.codepoint = seed;
    return compute_coords_from_seed(&s);
}

int verify_on_sphere(const PointZM* p, double tolerance) {
    double sum_squares = p->x * p->x + p->y * p->y + p->z * p->z + p->m * p->m;
    double expected = HYPERSPHERE_RADIUS * HYPERSPHERE_RADIUS;
    return fabs(sum_squares - expected) < tolerance;
}
