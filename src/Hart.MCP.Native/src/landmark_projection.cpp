#include "hartonomous/types.h"
#include "hartonomous/landmark_projection.h"
#include <math.h>
#include <string.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define PHI 1.618033988749894848  // Golden ratio
#define PHI_INV 0.618033988749894848  // 1/phi

// Unicode blocks for segmentation
#define BASIC_LATIN_START 0x0000
#define BASIC_LATIN_END 0x007F
#define LATIN_EXTENDED_START 0x0080
#define LATIN_EXTENDED_END 0x024F
#define GREEK_START 0x0370
#define GREEK_END 0x03FF
#define CYRILLIC_START 0x0400
#define CYRILLIC_END 0x052F
#define CJK_START 0x4E00
#define CJK_END 0x9FFF
#define EMOJI_START 0x1F300
#define EMOJI_END 0x1F9FF

// Get character category based on Unicode codepoint
static CharCategory get_category(uint32_t codepoint) {
    // ASCII digits
    if (codepoint >= '0' && codepoint <= '9') return CHAR_DIGIT;
    
    // ASCII uppercase
    if (codepoint >= 'A' && codepoint <= 'Z') return CHAR_LETTER_UPPER;
    
    // ASCII lowercase
    if (codepoint >= 'a' && codepoint <= 'z') return CHAR_LETTER_LOWER;
    
    // Whitespace
    if (codepoint == ' ' || codepoint == '\t' || codepoint == '\n' || codepoint == '\r')
        return CHAR_WHITESPACE;
    
    // Punctuation
    if ((codepoint >= '!' && codepoint <= '/') ||
        (codepoint >= ':' && codepoint <= '@') ||
        (codepoint >= '[' && codepoint <= '`') ||
        (codepoint >= '{' && codepoint <= '~'))
        return CHAR_PUNCTUATION;
    
    // Control characters
    if (codepoint < 32 || (codepoint >= 127 && codepoint < 160))
        return CHAR_CONTROL;
    
    // Extended Latin (includes accented characters)
    if (codepoint >= LATIN_EXTENDED_START && codepoint <= LATIN_EXTENDED_END)
        return CHAR_LETTER_LOWER;  // Group with lowercase for clustering
    
    // Greek
    if (codepoint >= GREEK_START && codepoint <= GREEK_END)
        return CHAR_SYMBOL;
    
    // Cyrillic
    if (codepoint >= CYRILLIC_START && codepoint <= CYRILLIC_END)
        return CHAR_SYMBOL;
    
    // CJK
    if (codepoint >= CJK_START && codepoint <= CJK_END)
        return CHAR_SYMBOL;
    
    return CHAR_OTHER;
}

// Get base character for clustering (e.g., é -> e, À -> A)
static uint32_t get_base_char(uint32_t codepoint) {
    // Latin Extended-A accented characters to base
    if (codepoint >= 0xC0 && codepoint <= 0xC5) return 'A';  // À-Å
    if (codepoint == 0xC6) return 'A';  // Æ -> A
    if (codepoint == 0xC7) return 'C';  // Ç
    if (codepoint >= 0xC8 && codepoint <= 0xCB) return 'E';  // È-Ë
    if (codepoint >= 0xCC && codepoint <= 0xCF) return 'I';  // Ì-Ï
    if (codepoint == 0xD0) return 'D';  // Ð
    if (codepoint == 0xD1) return 'N';  // Ñ
    if (codepoint >= 0xD2 && codepoint <= 0xD6) return 'O';  // Ò-Ö
    if (codepoint == 0xD8) return 'O';  // Ø
    if (codepoint >= 0xD9 && codepoint <= 0xDC) return 'U';  // Ù-Ü
    if (codepoint == 0xDD) return 'Y';  // Ý
    
    // Lowercase variants
    if (codepoint >= 0xE0 && codepoint <= 0xE5) return 'a';  // à-å
    if (codepoint == 0xE6) return 'a';  // æ -> a
    if (codepoint == 0xE7) return 'c';  // ç
    if (codepoint >= 0xE8 && codepoint <= 0xEB) return 'e';  // è-ë
    if (codepoint >= 0xEC && codepoint <= 0xEF) return 'i';  // ì-ï
    if (codepoint == 0xF0) return 'd';  // ð
    if (codepoint == 0xF1) return 'n';  // ñ
    if (codepoint >= 0xF2 && codepoint <= 0xF6) return 'o';  // ò-ö
    if (codepoint == 0xF8) return 'o';  // ø
    if (codepoint >= 0xF9 && codepoint <= 0xFC) return 'u';  // ù-ü
    if (codepoint == 0xFD || codepoint == 0xFF) return 'y';  // ý, ÿ
    
    return codepoint;  // No base character, return as-is
}

// Project character to hypersphere using Fibonacci spiral
void hart_landmark_project_character(uint32_t codepoint, Point4D* out) {
    if (!out) return;
    
    CharCategory category = get_category(codepoint);
    uint32_t base = get_base_char(codepoint);
    
    // Segment allocation on hypersphere (divide by category)
    double segment_start = 0.0;
    double segment_size = 0.0;
    
    switch (category) {
        case CHAR_LETTER_UPPER:
            segment_start = 0.0;
            segment_size = 0.15;  // 15% of sphere
            break;
        case CHAR_LETTER_LOWER:
            segment_start = 0.15;
            segment_size = 0.15;
            break;
        case CHAR_DIGIT:
            segment_start = 0.30;
            segment_size = 0.05;
            break;
        case CHAR_PUNCTUATION:
            segment_start = 0.35;
            segment_size = 0.05;
            break;
        case CHAR_WHITESPACE:
            segment_start = 0.40;
            segment_size = 0.02;
            break;
        case CHAR_SYMBOL:
            segment_start = 0.42;
            segment_size = 0.30;  // Large space for CJK, Greek, Cyrillic, etc.
            break;
        case CHAR_CONTROL:
            segment_start = 0.72;
            segment_size = 0.03;
            break;
        default:  // CHAR_OTHER
            segment_start = 0.75;
            segment_size = 0.25;
            break;
    }
    
    // Position within segment using Fibonacci spiral
    double index = (double)codepoint;
    double theta = 2.0 * M_PI * index * PHI_INV;  // Golden angle
    double phi = segment_start + (fmod(index * PHI_INV, 1.0) * segment_size);
    phi = phi * M_PI;  // Map to [0, π]
    
    // Clustering: if this is an accented variant, position near base character
    if (base != codepoint) {
        Point4D base_point;
        hart_landmark_project_character(base, &base_point);
        
        // Small offset from base (10% of segment size)
        double offset_angle = 2.0 * M_PI * (codepoint - base) / 256.0;
        double offset_magnitude = segment_size * M_PI * 0.1;
        
        phi = acos(base_point.z) + offset_magnitude * cos(offset_angle);
        theta = atan2(base_point.y, base_point.x) + offset_magnitude * sin(offset_angle);
    }
    
    // Convert to Cartesian coordinates on unit hypersphere
    // Standard spherical -> Cartesian with 4th dimension
    double sin_phi = sin(phi);
    out->x = sin_phi * cos(theta);
    out->y = sin_phi * sin(theta);
    out->z = cos(phi);
    
    // M dimension encodes fine detail (category + position within category)
    out->m = sin_phi * cos(theta + phi);  // Creates variation in 4th dimension
    
    // Normalize to ensure exact hypersphere constraint
    double norm = sqrt(out->x * out->x + out->y * out->y + out->z * out->z + out->m * out->m);
    out->x /= norm;
    out->y /= norm;
    out->z /= norm;
    out->m /= norm;
}

// Project number to hypersphere
void hart_landmark_project_number(double value, Point4D* out) {
    if (!out) return;
    
    // Handle special values
    if (isnan(value)) {
        out->x = 0.0;
        out->y = 0.0;
        out->z = 1.0;
        out->m = 0.0;
        return;
    }
    
    if (isinf(value)) {
        out->x = 0.0;
        out->y = 0.0;
        out->z = value > 0 ? -1.0 : 1.0;
        out->m = 0.0;
        return;
    }
    
    // Monotonic mapping: use sigmoid-like function to map R -> [0, π]
    double sign = value >= 0 ? 1.0 : -1.0;
    double abs_val = fabs(value);
    
    // Log scale for large ranges
    double scaled = log(1.0 + abs_val);
    
    // Map to angle
    double phi = M_PI * (0.5 + 0.4 * tanh(scaled * 0.1) * sign);  // Range: [0.1π, 0.9π]
    double theta = 2.0 * M_PI * fmod(abs_val * PHI_INV, 1.0);  // Wrap around
    
    // Convert to 4D Cartesian
    double sin_phi = sin(phi);
    out->x = sin_phi * cos(theta);
    out->y = sin_phi * sin(theta);
    out->z = cos(phi);
    out->m = sign * sin_phi * sin(theta + phi);  // Encode sign in M
    
    // Normalize
    double norm = sqrt(out->x * out->x + out->y * out->y + out->z * out->z + out->m * out->m);
    out->x /= norm;
    out->y /= norm;
    out->z /= norm;
    out->m /= norm;
}

CharCategory hart_get_char_category(uint32_t codepoint) {
    return get_category(codepoint);
}

// Reverse lookup requires precomputed table - for now, brute force search
HartResult hart_reverse_lookup_character(const Point4D* point, double tolerance, uint32_t* out_codepoint) {
    if (!point || !out_codepoint) return HART_ERROR_INVALID_INPUT;
    
    // This is a placeholder - in production, use spatial index or precomputed KD-tree
    // For now, check common ASCII characters
    double min_dist = tolerance * 2.0;
    uint32_t best_match = 0;
    
    for (uint32_t cp = 0; cp < 128; cp++) {
        Point4D test;
        hart_landmark_project_character(cp, &test);
        
        double dx = point->x - test.x;
        double dy = point->y - test.y;
        double dz = point->z - test.z;
        double dm = point->m - test.m;
        double dist = sqrt(dx*dx + dy*dy + dz*dz + dm*dm);
        
        if (dist < min_dist) {
            min_dist = dist;
            best_match = cp;
        }
    }
    
    if (min_dist < tolerance) {
        *out_codepoint = best_match;
        return HART_OK;
    }
    
    return HART_ERROR_NOT_FOUND;
}
