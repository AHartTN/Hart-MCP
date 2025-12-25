/**
 * test_atom_seed.c - Rigorous tests for atom seed and hash implementations
 * 
 * TESTS MATHEMATICAL INVARIANTS:
 * 1. Hypersphere constraint: X^2 + Y^2 + Z^2 + M^2 = R^2 for ALL seeds
 * 2. Determinism: identical seeds ALWAYS produce identical results
 * 3. Uniqueness: different seeds produce different positions/hashes
 * 4. Hash collision resistance: different inputs produce different hashes
 * 5. Order sensitivity: hash([A,B]) != hash([B,A])
 */

#include "hartonomous/atom_seed.h"
#include "hartonomous/hilbert.h"
#include "hartonomous/content_hash.h"
#include <stdio.h>
#include <math.h>
#include <string.h>
#include <float.h>

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

/* Test 1: Unicode seed - hypersphere constraint */
int test_unicode_hypersphere(void) {
    TEST("Unicode seed hypersphere constraint");
    
    uint32_t test_codepoints[] = {
        0, 1, 'A', 'Z', 'a', 'z', '0', '9',
        0x00E9, 0x0100, 0x0370, 0x0400, 0x4E00,
        0x1F600, 0x10FFFF
    };
    int num = sizeof(test_codepoints) / sizeof(test_codepoints[0]);
    
    for (int i = 0; i < num; i++) {
        AtomSeed seed = seed_from_codepoint(test_codepoints[i]);
        Point4D p = compute_coords_from_seed(&seed);
        
        double r2 = p.x*p.x + p.y*p.y + p.z*p.z + p.m*p.m;
        double error = fabs(r2 - 1.0);
        
        if (error > 1e-10) {
            printf(RED "  FAIL: U+%04X: r^2 = %.15f, error = %.2e" RESET "\n", 
                   test_codepoints[i], r2, error);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "All Unicode codepoints lie on unit hypersphere");
    return 1;
}

/* Test 2: Integer seed - hypersphere and determinism */
int test_integer_seeds(void) {
    TEST("Integer seed projection");
    
    int64_t test_ints[] = {0, 1, -1, 42, -42, INT64_MAX, INT64_MIN};
    int num = sizeof(test_ints) / sizeof(test_ints[0]);
    
    for (int i = 0; i < num; i++) {
        AtomSeed seed = seed_from_integer(test_ints[i]);
        Point4D p1 = compute_coords_from_seed(&seed);
        Point4D p2 = compute_coords_from_seed(&seed);
        
        /* Check hypersphere */
        double r2 = p1.x*p1.x + p1.y*p1.y + p1.z*p1.z + p1.m*p1.m;
        if (fabs(r2 - 1.0) > 1e-10) {
            printf(RED "  FAIL: %lld not on hypersphere" RESET "\n", (long long)test_ints[i]);
            tests_failed++;
            return 0;
        }
        
        /* Check determinism */
        if (p1.x != p2.x || p1.y != p2.y || p1.z != p2.z || p1.m != p2.m) {
            printf(RED "  FAIL: %lld not deterministic" RESET "\n", (long long)test_ints[i]);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "All integer seeds on hypersphere and deterministic");
    return 1;
}

/* Test 3: Float seed - IEEE754 bit preservation */
int test_float_seeds(void) {
    TEST("Float seed projection (IEEE754)");
    
    double test_floats[] = {0.0, 1.0, -1.0, 3.14159265358979, -2.71828, 
                            DBL_MIN, DBL_MAX, DBL_EPSILON};
    int num = sizeof(test_floats) / sizeof(test_floats[0]);
    
    for (int i = 0; i < num; i++) {
        AtomSeed seed = seed_from_double(test_floats[i]);
        Point4D p = compute_coords_from_seed(&seed);
        
        double r2 = p.x*p.x + p.y*p.y + p.z*p.z + p.m*p.m;
        if (fabs(r2 - 1.0) > 1e-10) {
            printf(RED "  FAIL: %.10e not on hypersphere" RESET "\n", test_floats[i]);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "All float seeds on hypersphere");
    
    /* Verify bit preservation */
    double original = 3.14159265358979;
    AtomSeed seed = seed_from_double(original);
    
    double recovered;
    memcpy(&recovered, &seed.value.float_bits, sizeof(double));
    
    ASSERT(original == recovered, "IEEE754 bits preserved exactly");
    
    return 1;
}

/* Test 4: Hash determinism */
int test_hash_determinism(void) {
    TEST("Hash determinism");
    
    AtomSeed seed = seed_from_codepoint('A');
    ContentHash h1 = hash_seed(&seed);
    ContentHash h2 = hash_seed(&seed);
    ContentHash h3 = hash_seed(&seed);
    
    ASSERT(hash_equal(&h1, &h2), "First two hashes identical");
    ASSERT(hash_equal(&h2, &h3), "Second and third hashes identical");
    
    return 1;
}

/* Test 5: Hash uniqueness */
int test_hash_uniqueness(void) {
    TEST("Hash uniqueness");
    
    AtomSeed seed_a = seed_from_codepoint('A');
    AtomSeed seed_b = seed_from_codepoint('B');
    
    ContentHash h_a = hash_seed(&seed_a);
    ContentHash h_b = hash_seed(&seed_b);
    
    ASSERT(!hash_equal(&h_a, &h_b), "Different seeds produce different hashes");
    
    /* Test adjacent codepoints */
    for (uint32_t cp = 'A'; cp < 'Z'; cp++) {
        AtomSeed s1 = seed_from_codepoint(cp);
        AtomSeed s2 = seed_from_codepoint(cp + 1);
        ContentHash h1 = hash_seed(&s1);
        ContentHash h2 = hash_seed(&s2);
        
        if (hash_equal(&h1, &h2)) {
            printf(RED "  FAIL: U+%04X and U+%04X have same hash" RESET "\n", cp, cp+1);
            tests_failed++;
            return 0;
        }
    }
    
    ASSERT(1, "All adjacent codepoints have unique hashes");
    return 1;
}

/* Test 6: Composition hash order sensitivity */
int test_composition_order(void) {
    TEST("Composition hash order sensitivity");
    
    AtomSeed seed_a = seed_from_codepoint('A');
    AtomSeed seed_b = seed_from_codepoint('B');
    ContentHash h_a = hash_seed(&seed_a);
    ContentHash h_b = hash_seed(&seed_b);
    
    ContentHash children_ab[] = {h_a, h_b};
    ContentHash children_ba[] = {h_b, h_a};
    int32_t mults[] = {1, 1};
    
    ContentHash comp_ab = hash_composition(children_ab, mults, 2);
    ContentHash comp_ba = hash_composition(children_ba, mults, 2);
    
    ASSERT(!hash_equal(&comp_ab, &comp_ba), "hash([A,B]) != hash([B,A])");
    
    return 1;
}

/* Test 7: Composition hash multiplicity sensitivity */
int test_composition_multiplicity(void) {
    TEST("Composition hash multiplicity sensitivity");
    
    AtomSeed seed_a = seed_from_codepoint('A');
    ContentHash h_a = hash_seed(&seed_a);
    ContentHash children[] = {h_a};
    
    int32_t mult1[] = {1};
    int32_t mult2[] = {2};
    
    ContentHash comp1 = hash_composition(children, mult1, 1);
    ContentHash comp2 = hash_composition(children, mult2, 1);
    
    ASSERT(!hash_equal(&comp1, &comp2), "hash([A,1]) != hash([A,2])");
    
    return 1;
}

/* Test 8: Position uniqueness for critical ASCII */
int test_ascii_uniqueness(void) {
    TEST("ASCII position uniqueness");
    
    Point4D positions[128];
    
    for (int i = 32; i < 127; i++) {
        AtomSeed seed = seed_from_codepoint((uint32_t)i);
        positions[i] = compute_coords_from_seed(&seed);
    }
    
    /* Check all pairs are distinct */
    for (int i = 32; i < 127; i++) {
        for (int j = i + 1; j < 127; j++) {
            double dist = sqrt(
                pow(positions[i].x - positions[j].x, 2) +
                pow(positions[i].y - positions[j].y, 2) +
                pow(positions[i].z - positions[j].z, 2) +
                pow(positions[i].m - positions[j].m, 2)
            );
            
            if (dist < 1e-10) {
                printf(RED "  FAIL: '%c' and '%c' have same position" RESET "\n", i, j);
                tests_failed++;
                return 0;
            }
        }
    }
    
    ASSERT(1, "All printable ASCII have unique positions");
    return 1;
}

int main(void) {
    printf("========================================\n");
    printf("  ATOM SEED MATHEMATICAL TESTS\n");
    printf("========================================\n");
    
    test_unicode_hypersphere();
    test_integer_seeds();
    test_float_seeds();
    test_hash_determinism();
    test_hash_uniqueness();
    test_composition_order();
    test_composition_multiplicity();
    test_ascii_uniqueness();
    
    printf("\n========================================\n");
    printf("  RESULTS: %d passed, %d failed\n", tests_passed, tests_failed);
    printf("========================================\n");
    
    return tests_failed > 0 ? 1 : 0;
}