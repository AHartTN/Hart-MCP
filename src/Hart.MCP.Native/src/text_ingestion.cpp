#include "hartonomous/text_ingestion.h"
#include "hartonomous/landmark_projection.h"
#include "hartonomous/hilbert_curve.h"
#include "hartonomous/content_hash.h"
#include "hartonomous/db_connection.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// UTF-8 decoder
static int utf8_decode(const char* text, size_t* pos, size_t length, uint32_t* out_codepoint) {
    if (*pos >= length) return 0;
    
    unsigned char c = text[*pos];
    *pos += 1;
    
    if (c < 0x80) {
        *out_codepoint = c;
        return 1;
    }
    
    if ((c & 0xE0) == 0xC0) {
        if (*pos >= length) return 0;
        *out_codepoint = ((c & 0x1F) << 6) | (text[*pos] & 0x3F);
        *pos += 1;
        return 1;
    }
    
    if ((c & 0xF0) == 0xE0) {
        if (*pos + 1 >= length) return 0;
        *out_codepoint = ((c & 0x0F) << 12) | ((text[*pos] & 0x3F) << 6) | (text[*pos + 1] & 0x3F);
        *pos += 2;
        return 1;
    }
    
    if ((c & 0xF8) == 0xF0) {
        if (*pos + 2 >= length) return 0;
        *out_codepoint = ((c & 0x07) << 18) | ((text[*pos] & 0x3F) << 12) | 
                         ((text[*pos + 1] & 0x3F) << 6) | (text[*pos + 2] & 0x3F);
        *pos += 3;
        return 1;
    }
    
    return 0;
}

HartResult hart_ingest_text(
    PGconn* conn,
    const char* text,
    size_t length,
    int64_t* out_atom_id)
{
    if (!conn || !text || length == 0 || !out_atom_id) {
        return HART_ERROR_INVALID_INPUT;
    }
    
    // Decode UTF-8 and create character atoms
    int64_t* char_atom_ids = (int64_t*)malloc(sizeof(int64_t) * length);
    size_t num_chars = 0;
    
    size_t pos = 0;
    while (pos < length) {
        uint32_t codepoint;
        if (!utf8_decode(text, &pos, length, &codepoint)) {
            break;
        }
        
        // Project character to hypersphere
        Point4D point;
        hart_landmark_project_character(codepoint, &point);
        
        // Compute Hilbert index
        HilbertIndex hilbert;
        hart_coords_to_hilbert(&point, &hilbert);
        
        // Compute content hash
        ContentHash hash;
        hart_hash_constant(&point, &hash);
        
        // Create WKT for PointZM
        char wkt[256];
        snprintf(wkt, sizeof(wkt), "POINT ZM(%f %f %f %f)", 
                 point.x, point.y, point.z, point.m);
        
        // Upsert atom
        int64_t char_id;
        HartResult result = hart_db_upsert_atom(conn, &hilbert, wkt, &hash, &char_id);
        if (result != HART_OK) {
            free(char_atom_ids);
            return result;
        }
        
        char_atom_ids[num_chars++] = char_id;
    }
    
    if (num_chars == 0) {
        free(char_atom_ids);
        return HART_ERROR_INVALID_INPUT;
    }
    
    // Build LineString connecting all characters
    // WKT format: LINESTRING ZM(x1 y1 z1 m1, x2 y2 z2 m2, ...)
    size_t wkt_size = 128 + num_chars * 100;
    char* linestring_wkt = (char*)malloc(wkt_size);
    strcpy(linestring_wkt, "LINESTRING ZM(");
    
    // Get geometry for each character atom and build LineString
    for (size_t i = 0; i < num_chars; i++) {
        char* char_wkt = NULL;
        HartResult result = hart_db_get_atom_geom(conn, char_atom_ids[i], &char_wkt);
        if (result != HART_OK) {
            free(char_atom_ids);
            free(linestring_wkt);
            return result;
        }
        
        // Parse PointZM coordinates from WKT
        double x, y, z, m;
        sscanf(char_wkt, "POINT ZM(%lf %lf %lf %lf)", &x, &y, &z, &m);
        free(char_wkt);
        
        char coord_buf[100];
        snprintf(coord_buf, sizeof(coord_buf), "%s%f %f %f %f",
                 i > 0 ? ", " : "", x, y, z, m);
        strcat(linestring_wkt, coord_buf);
    }
    strcat(linestring_wkt, ")");
    
    // Hash the character atom IDs to create composition hash
    ContentHash* char_hashes = (ContentHash*)malloc(sizeof(ContentHash) * num_chars);
    for (size_t i = 0; i < num_chars; i++) {
        // For now, hash the atom ID itself (in production, would fetch actual hash from DB)
        hart_hash_bytes(&char_atom_ids[i], sizeof(int64_t), &char_hashes[i]);
    }
    
    ContentHash composition_hash;
    hart_hash_composition(char_hashes, num_chars, NULL, &composition_hash);
    
    free(char_hashes);
    free(char_atom_ids);
    
    // Compute Hilbert index from centroid
    // For LineString, compute geometric center
    HilbertIndex composition_hilbert;
    composition_hilbert.high = 0;  // Placeholder
    composition_hilbert.low = 0;
    
    // Upsert composition atom
    HartResult result = hart_db_upsert_atom(conn, &composition_hilbert, linestring_wkt, 
                                            &composition_hash, out_atom_id);
    
    free(linestring_wkt);
    
    return result;
}

HartResult hart_reconstruct_text(
    PGconn* conn,
    int64_t atom_id,
    char** out_text,
    size_t* out_length)
{
    if (!conn || !out_text || !out_length) {
        return HART_ERROR_INVALID_INPUT;
    }
    
    // Get atom geometry
    char* wkt = NULL;
    HartResult result = hart_db_get_atom_geom(conn, atom_id, &wkt);
    if (result != HART_OK) {
        return result;
    }
    
    // Parse LineString coordinates
    if (strncmp(wkt, "LINESTRING ZM(", 14) != 0) {
        free(wkt);
        return HART_ERROR_INVALID_INPUT;
    }
    
    // Extract points and reverse-lookup characters
    char* coords = wkt + 14;
    size_t buffer_size = 1024;
    char* text_buffer = (char*)malloc(buffer_size);
    size_t text_pos = 0;
    
    while (*coords && *coords != ')') {
        double x, y, z, m;
        int n = sscanf(coords, "%lf %lf %lf %lf", &x, &y, &z, &m);
        if (n != 4) break;
        
        Point4D point = {x, y, z, m};
        uint32_t codepoint;
        result = hart_reverse_lookup_character(&point, 0.001, &codepoint);
        
        if (result == HART_OK) {
            // Encode as UTF-8
            if (codepoint < 0x80) {
                if (text_pos + 1 >= buffer_size) {
                    buffer_size *= 2;
                    text_buffer = (char*)realloc(text_buffer, buffer_size);
                }
                text_buffer[text_pos++] = (char)codepoint;
            } else if (codepoint < 0x800) {
                if (text_pos + 2 >= buffer_size) {
                    buffer_size *= 2;
                    text_buffer = (char*)realloc(text_buffer, buffer_size);
                }
                text_buffer[text_pos++] = 0xC0 | (codepoint >> 6);
                text_buffer[text_pos++] = 0x80 | (codepoint & 0x3F);
            } else if (codepoint < 0x10000) {
                if (text_pos + 3 >= buffer_size) {
                    buffer_size *= 2;
                    text_buffer = (char*)realloc(text_buffer, buffer_size);
                }
                text_buffer[text_pos++] = 0xE0 | (codepoint >> 12);
                text_buffer[text_pos++] = 0x80 | ((codepoint >> 6) & 0x3F);
                text_buffer[text_pos++] = 0x80 | (codepoint & 0x3F);
            } else {
                if (text_pos + 4 >= buffer_size) {
                    buffer_size *= 2;
                    text_buffer = (char*)realloc(text_buffer, buffer_size);
                }
                text_buffer[text_pos++] = 0xF0 | (codepoint >> 18);
                text_buffer[text_pos++] = 0x80 | ((codepoint >> 12) & 0x3F);
                text_buffer[text_pos++] = 0x80 | ((codepoint >> 6) & 0x3F);
                text_buffer[text_pos++] = 0x80 | (codepoint & 0x3F);
            }
        }
        
        // Skip to next coordinate
        while (*coords && *coords != ',' && *coords != ')') coords++;
        if (*coords == ',') coords++;
        while (*coords == ' ') coords++;
    }
    
    free(wkt);
    
    text_buffer[text_pos] = '\0';
    *out_text = text_buffer;
    *out_length = text_pos;
    
    return HART_OK;
}
