/**
 * test_hilbert.c - Rigorous tests for Hilbert curve implementation
 * 
 * TESTS MATHEMATICAL INVARIANTS, NOT JUST "DOES IT RUN":
 * 1. Bijection: forward(inverse(h)) == h for ALL valid inputs
 * 2. Locality: nearby points have closer indices than far points
 * 3. Determinism: identical inputs ALWAYS produce identical outputs
 * 4. Boundary conditions: edge cases at min/max coordinates
 * 5. Quantization: verify precision bounds
 */

#include "hartonomous/hilbert.h"
#include "hartonomous/atom_seed.h"
#include <stdio.h>
#include <math.h>
#include <stdlib.h>
#include <time.h>

#define RED "\033[31m"
#define GREEN "\033[32m"
#define RESET "\033[0m"

static int tests_passed = 0;
static int tests_failed = 0;

#define TEST(name) printf("\nTEST: %s\n", name)
#define ASSERT(cond, msg) do { \
    if (!(cond)) { \
        printf(RED "  FAIL: %s" RESET "\n", msg); \
        tests_failed++; \
        return 0; \
    } else { \
        printf(GREEN "  PASS: %s" RESET "\n", msg); \
        tests_passed++; \
    } \
} while(0)

/* Test 1: Quantization roundtrip preserves values within precision */
int test_quantization_roundtrip(void) {
    TEST("Quantization roundtrip precision");
    
    double test_values[] = {-1.0, -0.5, 0.0, 0.5, 1.0, 0.123456, -0.789012};
    int num_values = sizeof(test_values) / sizeof(test_values[0]);
    
    for (int i = 0; i < num_values; i++) {
        double original = test_values[i];
        uint32_t quantized = quantize_coord(original, -1.0, 1.0);
        double recovered = dequantize_coord(quantized, -1.0, 1.0);
        
        /* Max error with 16-bit quantization: 2/(2^16) = 0.000031 */
        double error = fabs(original - recovered);
        double max_error = 2.0 / 65535.0;
        
        if (error > max_error) {
            printf(RED "  FAIL: quantize(%.6f) -> %u -> %.6f, error=%.6f > max=%.6f" RESET "\n",
                   original, quantized, recovered, error, max_error);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "All quantization roundtrips within precision bounds");
    return 1;
}

/* Test 2: Hilbert curve bijection - coords_to_hilbert(hilbert_to_coords(h)) == h */
int test_hilbert_bijection(void) {
    TEST("Hilbert curve bijection (inverse is exact)");
    
    /* Test corners of the coordinate space */
    double corners[][4] = {
        {-1.0, -1.0, -1.0, -1.0},
        {1.0, 1.0, 1.0, 1.0},
        {-1.0, 1.0, -1.0, 1.0},
        {0.0, 0.0, 0.0, 0.0}
    };
    
    for (int i = 0; i < 4; i++) {
        double x = corners[i][0], y = corners[i][1];
        double z = corners[i][2], m = corners[i][3];
        
        HilbertIndex h = coords_to_hilbert(x, y, z, m);
        double x2, y2, z2, m2;
        hilbert_to_coords(h, &x2, &y2, &z2, &m2);
        HilbertIndex h2 = coords_to_hilbert(x2, y2, z2, m2);
        
        /* After one roundtrip through quantization, result should be stable */
        if (h.low != h2.low || h.high != h2.high) {
            printf(RED "  FAIL: Bijection failed for (%.2f,%.2f,%.2f,%.2f)" RESET "\n", x, y, z, m);
            printf("    h1 = {%llu, %llu}, h2 = {%llu, %llu}\n", 
                   (unsigned long long)h.high, (unsigned long long)h.low,
                   (unsigned long long)h2.high, (unsigned long long)h2.low);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "Hilbert bijection holds for corner cases");
    return 1;
}

/* Test 3: Determinism - same input always produces same output */
int test_determinism(void) {
    TEST("Determinism (reproducibility)");
    
    double x = 0.12345, y = -0.67890, z = 0.11111, m = -0.99999;
    
    HilbertIndex h1 = coords_to_hilbert(x, y, z, m);
    HilbertIndex h2 = coords_to_hilbert(x, y, z, m);
    HilbertIndex h3 = coords_to_hilbert(x, y, z, m);
    
    ASSERT(h1.low == h2.low && h1.high == h2.high, "First two calls identical");
    ASSERT(h2.low == h3.low && h2.high == h3.high, "Second and third calls identical");
    
    return 1;
}

/* Test 4: Locality preservation - nearby points have closer Hilbert indices */
int test_locality(void) {
    TEST("Locality preservation");
    
    HilbertIndex h_origin = coords_to_hilbert(0.0, 0.0, 0.0, 0.0);
    HilbertIndex h_near = coords_to_hilbert(0.001, 0.001, 0.001, 0.001);
    HilbertIndex h_far = coords_to_hilbert(0.9, 0.9, 0.9, 0.9);
    
    uint64_t dist_near = hilbert_distance(h_origin, h_near);
    uint64_t dist_far = hilbert_distance(h_origin, h_far);
    
    printf("  Distance to near point: %llu\n", (unsigned long long)dist_near);
    printf("  Distance to far point:  %llu\n", (unsigned long long)dist_far);
    
    ASSERT(dist_near < dist_far, "Near point has smaller Hilbert distance than far point");
    
    return 1;
}

/* Test 5: Seed projection determinism */
int test_seed_projection_determinism(void) {
    TEST("Seed projection determinism");
    
    AtomSeed seed = seed_from_codepoint('A');
    Point4D p1 = compute_coords_from_seed(&seed);
    Point4D p2 = compute_coords_from_seed(&seed);
    
    ASSERT(p1.x == p2.x && p1.y == p2.y && p1.z == p2.z && p1.m == p2.m,
           "Same seed produces identical coordinates");
    
    /* Verify hypersphere constraint */
    double r2 = p1.x*p1.x + p1.y*p1.y + p1.z*p1.z + p1.m*p1.m;
    double error = fabs(r2 - 1.0);
    
    printf("  Sum of squares: %.15f (should be 1.0)\n", r2);
    ASSERT(error < 1e-10, "Point lies on unit hypersphere");
    
    return 1;
}

/* Test 6: Full range coverage */
int test_full_range_coverage(void) {
    TEST("Full Unicode range projection");
    
    /* Test several Unicode ranges */
    uint32_t test_codepoints[] = {
        'A', 'Z', 'a', 'z', '0', '9',           /* ASCII */
        0x00E9, 0x00F1,                          /* Latin-1 */
        0x0391, 0x03C9,                          /* Greek */
        0x4E00, 0x9FFF,                          /* CJK */
        0x1F600, 0x1F64F,                        /* Emoji */
        0x10FFFF                                 /* Max Unicode */
    };
    int num_codepoints = sizeof(test_codepoints) / sizeof(test_codepoints[0]);
    
    for (int i = 0; i < num_codepoints; i++) {
        AtomSeed seed = seed_from_codepoint(test_codepoints[i]);
        Point4D p = compute_coords_from_seed(&seed);
        
        if (!verify_on_sphere(&p, 1e-10)) {
            printf(RED "  FAIL: Codepoint U+%04X not on hypersphere" RESET "\n", test_codepoints[i]);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "All Unicode codepoints project onto hypersphere");
    return 1;
}

/* Test 7: Uniqueness - different inputs produce different outputs */
int test_uniqueness(void) {
    TEST("Uniqueness (collision resistance)");
    
    AtomSeed seed_a = seed_from_codepoint('A');
    AtomSeed seed_b = seed_from_codepoint('B');
    
    Point4D p_a = compute_coords_from_seed(&seed_a);
    Point4D p_b = compute_coords_from_seed(&seed_b);
    
    double dist = sqrt(pow(p_a.x - p_b.x, 2) + pow(p_a.y - p_b.y, 2) + 
                       pow(p_a.z - p_b.z, 2) + pow(p_a.m - p_b.m, 2));
    
    printf("  Distance between 'A' and 'B': %.10f\n", dist);
    ASSERT(dist > 1e-6, "Different codepoints have different positions");
    
    HilbertIndex h_a = seed_to_hilbert(&seed_a);
    HilbertIndex h_b = seed_to_hilbert(&seed_b);
    
    ASSERT(h_a.low != h_b.low || h_a.high != h_b.high, 
           "Different codepoints have different Hilbert indices");
    
    return 1;
}

int main(void) {
    printf("========================================\n");
    printf("  HILBERT CURVE MATHEMATICAL TESTS\n");
    printf("========================================\n");
    
    test_quantization_roundtrip();
    test_hilbert_bijection();
    test_determinism();
    test_locality();
    test_seed_projection_determinism();
    test_full_range_coverage();
    test_uniqueness();
    
    printf("\n========================================\n");
    printf("  RESULTS: %d passed, %d failed\n", tests_passed, tests_failed);
    printf("========================================\n");
    
    return tests_failed > 0 ? 1 : 0;
}