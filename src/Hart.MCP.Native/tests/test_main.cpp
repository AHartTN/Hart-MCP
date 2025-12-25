#include "hartonomous/types.h"
#include "hartonomous/landmark_projection.h"
#include "hartonomous/hilbert_curve.h"
#include "hartonomous/content_hash.h"
#include "hartonomous/db_connection.h"
#include "hartonomous/text_ingestion.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

void test_hypersphere_constraint() {
    printf("\n=== Testing Hypersphere Constraint ===\n");
    
    uint32_t test_chars[] = {'A', 'a', 'e', 'é', '0', '9', ' ', '!', 0x4E00};  // Including CJK
    
    for (int i = 0; i < 9; i++) {
        Point4D point;
        hart_landmark_project_character(test_chars[i], &point);
        
        double r_squared = point.x*point.x + point.y*point.y + point.z*point.z + point.m*point.m;
        double error = fabs(r_squared - 1.0);
        
        printf("Char U+%04X: (%.6f, %.6f, %.6f, %.6f) r²=%.10f error=%.2e %s\n",
               test_chars[i], point.x, point.y, point.z, point.m, r_squared, error,
               error < 1e-10 ? "✓" : "✗");
    }
}

void test_clustering() {
    printf("\n=== Testing Character Clustering ===\n");
    
    Point4D A, a, e, e_acute;
    hart_landmark_project_character('A', &A);
    hart_landmark_project_character('a', &a);
    hart_landmark_project_character('e', &e);
    hart_landmark_project_character(0xE9, &e_acute);  // é
    
    double dist_Aa = sqrt(pow(A.x-a.x,2) + pow(A.y-a.y,2) + pow(A.z-a.z,2) + pow(A.m-a.m,2));
    double dist_e_eacute = sqrt(pow(e.x-e_acute.x,2) + pow(e.y-e_acute.y,2) + pow(e.z-e_acute.z,2) + pow(e.m-e_acute.m,2));
    double dist_Ae = sqrt(pow(A.x-e.x,2) + pow(A.y-e.y,2) + pow(A.z-e.z,2) + pow(A.m-e.m,2));
    
    printf("Distance A-a: %.6f\n", dist_Aa);
    printf("Distance e-é: %.6f\n", dist_e_eacute);
    printf("Distance A-e: %.6f\n", dist_Ae);
    printf("Clustering works: %s\n", (dist_Aa < dist_Ae && dist_e_eacute < dist_Ae) ? "✓" : "✗");
}

void test_hilbert_determinism() {
    printf("\n=== Testing Hilbert Determinism ===\n");
    
    Point4D point = {0.5, 0.5, 0.5, 0.5};
    double norm = sqrt(point.x*point.x + point.y*point.y + point.z*point.z + point.m*point.m);
    point.x /= norm;
    point.y /= norm;
    point.z /= norm;
    point.m /= norm;
    
    HilbertIndex h1, h2;
    hart_coords_to_hilbert(&point, &h1);
    hart_coords_to_hilbert(&point, &h2);
    
    printf("Hilbert 1: %016llx%016llx\n", (unsigned long long)h1.high, (unsigned long long)h1.low);
    printf("Hilbert 2: %016llx%016llx\n", (unsigned long long)h2.high, (unsigned long long)h2.low);
    printf("Deterministic: %s\n", (h1.high == h2.high && h1.low == h2.low) ? "✓" : "✗");
}

void test_content_hashing() {
    printf("\n=== Testing Content Hashing ===\n");
    
    Point4D A;
    hart_landmark_project_character('A', &A);
    
    ContentHash hash1, hash2;
    hart_hash_constant(&A, &hash1);
    hart_hash_constant(&A, &hash2);
    
    char hex1[65], hex2[65];
    hart_hash_to_hex(&hash1, hex1);
    hart_hash_to_hex(&hash2, hex2);
    
    printf("Hash 1: %s\n", hex1);
    printf("Hash 2: %s\n", hex2);
    printf("Deterministic: %s\n", hart_hash_equal(&hash1, &hash2) ? "✓" : "✗");
}

void test_database_operations() {
    printf("\n=== Testing Database Operations ===\n");
    
    const char* conninfo = "host=localhost port=5432 dbname=HART-MCP user=hartonomous password=hartonomous";
    PGconn* conn = hart_db_connect(conninfo);
    
    if (!conn) {
        printf("Database connection failed ✗\n");
        return;
    }
    
    printf("Connected to database ✓\n");
    
    // Create schema
    if (hart_db_create_schema(conn) == HART_OK) {
        printf("Schema created ✓\n");
    } else {
        printf("Schema creation failed ✗\n");
        hart_db_disconnect(conn);
        return;
    }
    
    // Test text ingestion
    const char* test_text = "Hello World!";
    int64_t atom_id;
    
    printf("\nIngesting text: \"%s\"\n", test_text);
    HartResult result = hart_ingest_text(conn, test_text, strlen(test_text), &atom_id);
    
    if (result == HART_OK) {
        printf("Text ingested successfully! Atom ID: %lld ✓\n", (long long)atom_id);
        
        // Test reconstruction
        char* reconstructed;
        size_t reconstructed_length;
        result = hart_reconstruct_text(conn, atom_id, &reconstructed, &reconstructed_length);
        
        if (result == HART_OK) {
            printf("Reconstructed text: \"%s\" ✓\n", reconstructed);
            printf("Match: %s\n", strcmp(test_text, reconstructed) == 0 ? "✓" : "✗");
            free(reconstructed);
        } else {
            printf("Reconstruction failed ✗\n");
        }
    } else {
        printf("Text ingestion failed ✗\n");
    }
    
    hart_db_disconnect(conn);
}

int main() {
    printf("╔════════════════════════════════════════════════════════════╗\n");
    printf("║        HART.MCP NATIVE LIBRARY TEST SUITE                 ║\n");
    printf("║        Hartonomous Universal Substrate                    ║\n");
    printf("╚════════════════════════════════════════════════════════════╝\n");
    
    test_hypersphere_constraint();
    test_clustering();
    test_hilbert_determinism();
    test_content_hashing();
    test_database_operations();
    
    printf("\n╔════════════════════════════════════════════════════════════╗\n");
    printf("║        ALL TESTS COMPLETE                                  ║\n");
    printf("╚════════════════════════════════════════════════════════════╝\n");
    
    return 0;
}
