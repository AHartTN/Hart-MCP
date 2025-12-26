/**
 * bulk_ingestion.cpp - High-performance bulk data ingestion
 * 
 * Uses PostgreSQL COPY binary protocol for bulk inserts.
 * Target: 1M+ atoms/second on decent hardware.
 * 
 * Key optimizations:
 * 1. COPY binary protocol - bypasses SQL parsing
 * 2. Parallel hash/geometry computation (OpenMP)
 * 3. Streaming file I/O - never loads full file in memory
 * 4. Batch processing - 50K atoms per COPY batch
 * 5. Pre-lookup existing atoms to avoid duplicates
 */

#include "hartonomous/bulk_ingestion.h"
#include "hartonomous/atom_seed.h"
#include "hartonomous/content_hash.h"
#include "hartonomous/hilbert.h"

#include <libpq-fe.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <chrono>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include <algorithm>
#include <fstream>
#include <thread>

#ifdef _WIN32
#define NOMINMAX  // Prevent Windows min/max macros
#include <winsock2.h>  // For htonl, etc.
#pragma comment(lib, "ws2_32.lib")
#else
#include <arpa/inet.h>
#endif

// OpenMP for parallel processing
#ifdef _OPENMP
#include <omp.h>
#endif

// Batch size for COPY operations - larger = fewer round trips
static constexpr size_t COPY_BATCH_SIZE = 500000;

// SafeTensor header structure
struct SafeTensorHeader {
    std::unordered_map<std::string, struct TensorInfo> tensors;
};

struct TensorInfo {
    std::string dtype;
    std::vector<int64_t> shape;
    std::vector<int64_t> data_offsets;  // [start, end)
    int64_t total_elements;
};

// Constant data for bulk insert (new three-table schema)
struct ConstantData {
    int64_t hilbert_high;
    int64_t hilbert_low;
    double x, y, z, m;
    ContentHash hash;
    int64_t seed_value;
    int32_t seed_type;
};

// Helper: Convert 64-bit int to network byte order
static void write_int64_be(uint8_t* buf, int64_t val) {
    uint64_t uval = (uint64_t)val;
    buf[0] = (uval >> 56) & 0xFF;
    buf[1] = (uval >> 48) & 0xFF;
    buf[2] = (uval >> 40) & 0xFF;
    buf[3] = (uval >> 32) & 0xFF;
    buf[4] = (uval >> 24) & 0xFF;
    buf[5] = (uval >> 16) & 0xFF;
    buf[6] = (uval >> 8) & 0xFF;
    buf[7] = uval & 0xFF;
}

static void write_int32_be(uint8_t* buf, int32_t val) {
    uint32_t uval = (uint32_t)val;
    buf[0] = (uval >> 24) & 0xFF;
    buf[1] = (uval >> 16) & 0xFF;
    buf[2] = (uval >> 8) & 0xFF;
    buf[3] = uval & 0xFF;
}

static void write_double_be(uint8_t* buf, double val) {
    uint64_t bits;
    memcpy(&bits, &val, sizeof(double));
    write_int64_be(buf, (int64_t)bits);
}

// Helper: Create WKT for PointZM
static std::string point_to_wkt(double x, double y, double z, double m) {
    char buf[256];
    snprintf(buf, sizeof(buf), "POINT ZM (%.17g %.17g %.17g %.17g)", x, y, z, m);
    return std::string(buf);
}

// Helper: Parse SafeTensor header
static bool parse_safetensor_header(FILE* fp, SafeTensorHeader& header, int64_t& data_offset) {
    // First 8 bytes: header length (little-endian)
    uint64_t header_len;
    if (fread(&header_len, sizeof(uint64_t), 1, fp) != 1) {
        return false;
    }
    
    // Read header JSON
    std::vector<char> json_buf(header_len + 1);
    if (fread(json_buf.data(), 1, header_len, fp) != header_len) {
        return false;
    }
    json_buf[header_len] = '\0';
    
    data_offset = 8 + header_len;
    
    // Simple JSON parsing for SafeTensor format
    // Format: {"tensor_name": {"dtype": "F32", "shape": [x,y], "data_offsets": [start, end]}, ...}
    // Using basic string parsing - in production use a proper JSON library
    
    std::string json(json_buf.data());
    size_t pos = 0;
    
    while ((pos = json.find("\"dtype\"", pos)) != std::string::npos) {
        // Find tensor name (search backwards for opening quote)
        size_t name_end = json.rfind("\":", pos);
        if (name_end == std::string::npos) break;
        size_t name_start = json.rfind("\"", name_end - 1);
        if (name_start == std::string::npos) break;
        
        std::string tensor_name = json.substr(name_start + 1, name_end - name_start - 1);
        
        if (tensor_name == "__metadata__") {
            pos++;
            continue;
        }
        
        TensorInfo info;
        
        // Parse dtype
        size_t dtype_start = json.find("\"", pos + 7) + 1;
        size_t dtype_end = json.find("\"", dtype_start);
        info.dtype = json.substr(dtype_start, dtype_end - dtype_start);
        
        // Parse shape
        size_t shape_start = json.find("[", dtype_end);
        size_t shape_end = json.find("]", shape_start);
        std::string shape_str = json.substr(shape_start + 1, shape_end - shape_start - 1);
        
        // Parse shape values
        info.total_elements = 1;
        size_t sp = 0;
        while (sp < shape_str.length()) {
            size_t next = shape_str.find(",", sp);
            if (next == std::string::npos) next = shape_str.length();
            int64_t dim = std::stoll(shape_str.substr(sp, next - sp));
            info.shape.push_back(dim);
            info.total_elements *= dim;
            sp = next + 1;
        }
        
        // Parse data_offsets
        size_t off_start = json.find("data_offsets", shape_end);
        size_t off_arr_start = json.find("[", off_start);
        size_t off_arr_end = json.find("]", off_arr_start);
        std::string off_str = json.substr(off_arr_start + 1, off_arr_end - off_arr_start - 1);
        
        size_t comma = off_str.find(",");
        info.data_offsets.push_back(std::stoll(off_str.substr(0, comma)));
        info.data_offsets.push_back(std::stoll(off_str.substr(comma + 1)));
        
        header.tensors[tensor_name] = info;
        pos = off_arr_end;
    }
    
    return true;
}

// Write EWKB PointZM to buffer (PostGIS binary format) - returns bytes written
static size_t write_ewkb_pointzm(uint8_t* buf, double x, double y, double z, double m) {
    size_t pos = 0;

    // Byte order: 1 = little-endian
    buf[pos++] = 0x01;

    // Type: PointZM with SRID = 0xC0000001 (little-endian)
    // 0x00000001 = Point, 0x80000000 = hasZ, 0x40000000 = hasM
    uint32_t type = 0xC0000001;
    memcpy(buf + pos, &type, 4); pos += 4;

    // SRID = 0
    uint32_t srid = 0;
    memcpy(buf + pos, &srid, 4); pos += 4;

    // Coordinates (little-endian doubles)
    memcpy(buf + pos, &x, 8); pos += 8;
    memcpy(buf + pos, &y, 8); pos += 8;
    memcpy(buf + pos, &z, 8); pos += 8;
    memcpy(buf + pos, &m, 8); pos += 8;

    return pos;  // 41 bytes total
}

// Bulk insert constants using COPY BINARY (maximum speed)
static int64_t bulk_insert_constants(PGconn* conn, const std::vector<ConstantData>& constants) {
    if (constants.empty()) return 0;

    // COPY BINARY to constant table
    const char* sql =
        "COPY constant (seed_value, seed_type, content_hash, hilbert_high, hilbert_low, geom) "
        "FROM STDIN WITH (FORMAT binary)";

    PGresult* res = PQexec(conn, sql);
    if (PQresultStatus(res) != PGRES_COPY_IN) {
        fprintf(stderr, "COPY start failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return -1;
    }
    PQclear(res);

    // Binary COPY header: "PGCOPY\n\377\r\n\0" + flags (4 bytes) + extension (4 bytes)
    uint8_t header[19] = {'P','G','C','O','P','Y','\n',0xFF,'\r','\n',0x00, 0,0,0,0, 0,0,0,0};
    if (PQputCopyData(conn, (const char*)header, 19) != 1) {
        PQputCopyEnd(conn, "header failed");
        return -1;
    }

    // Pre-allocate large batch buffer (16MB) to minimize PQputCopyData calls
    const size_t ROW_SIZE = 128;
    const size_t BUFFER_SIZE = 16 * 1024 * 1024;  // 16MB
    std::vector<uint8_t> batch_buf(BUFFER_SIZE);
    size_t batch_pos = 0;

    int64_t count = 0;
    for (const auto& c : constants) {
        // Check if we need to flush batch
        if (batch_pos + ROW_SIZE > BUFFER_SIZE) {
            if (PQputCopyData(conn, (const char*)batch_buf.data(), (int)batch_pos) != 1) {
                fprintf(stderr, "COPY batch write failed: %s\n", PQerrorMessage(conn));
                PQputCopyEnd(conn, "error");
                return -1;
            }
            batch_pos = 0;
        }

        uint8_t* row = batch_buf.data() + batch_pos;
        size_t pos = 0;

        // Field count: 6 (big-endian int16)
        row[pos++] = 0x00;
        row[pos++] = 0x06;

        // Field 1: seed_value (int64, 8 bytes)
        write_int32_be(row + pos, 8); pos += 4;
        write_int64_be(row + pos, c.seed_value); pos += 8;

        // Field 2: seed_type (int32, 4 bytes)
        write_int32_be(row + pos, 4); pos += 4;
        write_int32_be(row + pos, c.seed_type); pos += 4;

        // Field 3: content_hash (bytea, 32 bytes)
        write_int32_be(row + pos, 32); pos += 4;
        memcpy(row + pos, c.hash.bytes, 32); pos += 32;

        // Field 4: hilbert_high (int64, 8 bytes)
        write_int32_be(row + pos, 8); pos += 4;
        write_int64_be(row + pos, c.hilbert_high); pos += 8;

        // Field 5: hilbert_low (int64, 8 bytes)
        write_int32_be(row + pos, 8); pos += 4;
        write_int64_be(row + pos, c.hilbert_low); pos += 8;

        // Field 6: geom (EWKB PointZM, 41 bytes)
        write_int32_be(row + pos, 41); pos += 4;
        pos += write_ewkb_pointzm(row + pos, c.x, c.y, c.z, c.m);

        batch_pos += pos;
        count++;
    }

    // Flush remaining data
    if (batch_pos > 0) {
        if (PQputCopyData(conn, (const char*)batch_buf.data(), (int)batch_pos) != 1) {
            fprintf(stderr, "COPY final batch write failed: %s\n", PQerrorMessage(conn));
            PQputCopyEnd(conn, "error");
            return -1;
        }
    }

    // Binary COPY trailer: -1 (int16)
    uint8_t trailer[2] = {0xFF, 0xFF};
    if (PQputCopyData(conn, (const char*)trailer, 2) != 1) {
        PQputCopyEnd(conn, "trailer failed");
        return -1;
    }

    if (PQputCopyEnd(conn, nullptr) != 1) {
        fprintf(stderr, "COPY end failed: %s\n", PQerrorMessage(conn));
        return -1;
    }

    res = PQgetResult(conn);
    if (PQresultStatus(res) != PGRES_COMMAND_OK) {
        fprintf(stderr, "COPY result failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return -1;
    }
    PQclear(res);

    return count;
}

// Lookup existing atoms by content hash
static std::unordered_set<std::string> lookup_existing_hashes(PGconn* conn, const std::vector<ContentHash>& hashes) {
    std::unordered_set<std::string> existing;
    if (hashes.empty()) return existing;
    
    // Build query with hash array
    std::string sql = "SELECT encode(content_hash, 'hex') FROM constant WHERE content_hash IN (";
    for (size_t i = 0; i < hashes.size(); i++) {
        if (i > 0) sql += ",";
        sql += "'\\x";
        char hex[65];
        for (int j = 0; j < 32; j++) {
            sprintf(hex + j * 2, "%02x", hashes[i].bytes[j]);
        }
        hex[64] = '\0';
        sql += hex;
        sql += "'";
    }
    sql += ")";
    
    PGresult* res = PQexec(conn, sql.c_str());
    if (PQresultStatus(res) != PGRES_TUPLES_OK) {
        fprintf(stderr, "Hash lookup failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return existing;
    }
    
    int rows = PQntuples(res);
    for (int i = 0; i < rows; i++) {
        existing.insert(PQgetvalue(res, i, 0));
    }
    
    PQclear(res);
    return existing;
}

extern "C" {

HART_API int64_t hart_seed_unicode(
    PGconn* conn,
    uint32_t start_codepoint,
    uint32_t end_codepoint,
    IngestionProgressCallback progress_callback,
    void* user_data)
{
    if (!conn) return HART_ERROR_DB_CONNECTION;

    auto start_time = std::chrono::high_resolution_clock::now();

    // Build valid codepoint array (excluding surrogates 0xD800-0xDFFF)
    std::vector<uint32_t> codepoints;
    codepoints.reserve(end_codepoint - start_codepoint + 1);
    for (uint32_t cp = start_codepoint; cp <= end_codepoint; cp++) {
        if (cp < 0xD800 || cp > 0xDFFF) {
            codepoints.push_back(cp);
        }
    }
    const size_t valid_count = codepoints.size();

    // Pre-allocate full batch for parallel computation
    std::vector<ConstantData> all_constants(valid_count);

    // PARALLEL COMPUTATION: geometry, hilbert, hash (O(n) now, was O(n²))
    #ifdef _OPENMP
    #pragma omp parallel for schedule(static)
    #endif
    for (int64_t i = 0; i < (int64_t)valid_count; i++) {
        uint32_t cp = codepoints[i];  // Direct O(1) lookup

        ConstantData& c = all_constants[i];
        c.seed_value = cp;
        c.seed_type = SEED_UNICODE;

        // Compute geometry
        AtomSeed seed = seed_from_codepoint(cp);
        Point4D pt = compute_coords_from_seed(&seed);
        c.x = pt.x;
        c.y = pt.y;
        c.z = pt.z;
        c.m = pt.m;

        // Compute Hilbert index
        HilbertIndex hilbert = coords_to_hilbert(pt.x, pt.y, pt.z, pt.m);
        c.hilbert_high = hilbert.high;
        c.hilbert_low = hilbert.low;

        // Compute content hash
        c.hash = compute_seed_hash(cp);
    }

    // SEQUENTIAL DB INSERT: batch COPY
    int64_t total_created = 0;
    for (size_t offset = 0; offset < all_constants.size(); offset += COPY_BATCH_SIZE) {
        size_t batch_size = std::min(COPY_BATCH_SIZE, all_constants.size() - offset);
        std::vector<ConstantData> batch(
            all_constants.begin() + offset,
            all_constants.begin() + offset + batch_size
        );

        int64_t inserted = bulk_insert_constants(conn, batch);
        if (inserted < 0) return inserted;
        total_created += inserted;

        if (progress_callback) {
            progress_callback("Unicode seeding", (int32_t)(offset + batch_size), (int32_t)valid_count, total_created, 0, user_data);
        }
    }

    if (progress_callback) {
        progress_callback("Complete", (int32_t)valid_count, (int32_t)valid_count, total_created, 0, user_data);
    }

    return total_created;
}

HART_API HartResult hart_ingest_safetensor(
    PGconn* conn,
    const char* filepath,
    const char* model_name,
    float sparsity_threshold,
    float target_sparsity_percent,
    IngestionProgressCallback progress_callback,
    void* user_data,
    SafeTensorResult* result)
{
    if (!conn || !filepath || !result) return HART_ERROR_INVALID_INPUT;
    
    memset(result, 0, sizeof(SafeTensorResult));
    auto start_time = std::chrono::high_resolution_clock::now();
    
    // Open file
    FILE* fp = fopen(filepath, "rb");
    if (!fp) {
        snprintf(result->error_message, sizeof(result->error_message), "Cannot open file: %s", filepath);
        return HART_ERROR_INVALID_INPUT;
    }
    
    // Get file size
    fseek(fp, 0, SEEK_END);
    int64_t file_size = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    
    // Parse header
    SafeTensorHeader header;
    int64_t data_offset;
    if (!parse_safetensor_header(fp, header, data_offset)) {
        fclose(fp);
        snprintf(result->error_message, sizeof(result->error_message), "Failed to parse SafeTensor header");
        return HART_ERROR_INVALID_INPUT;
    }
    
    result->tensor_count = (int32_t)header.tensors.size();
    
    if (progress_callback) {
        progress_callback("Parsed header", 0, result->tensor_count, 0, 0, user_data);
    }
    
    // If target_sparsity_percent > 0, do a first pass to compute threshold
    float actual_threshold = sparsity_threshold;
    if (target_sparsity_percent > 0) {
        // Sample values to determine threshold
        std::vector<float> samples;
        samples.reserve(1000000);
        
        for (auto& [name, info] : header.tensors) {
            if (info.dtype != "F32" && info.dtype != "F16") continue;
            
            fseek(fp, data_offset + info.data_offsets[0], SEEK_SET);
            
            int64_t bytes = info.data_offsets[1] - info.data_offsets[0];
            int64_t sample_count = std::min(bytes / 4, (int64_t)100000);
            
            std::vector<float> buf(sample_count);
            fread(buf.data(), sizeof(float), sample_count, fp);
            
            for (float v : buf) {
                samples.push_back(std::abs(v));
            }
        }
        
        // Sort and find percentile threshold
        std::sort(samples.begin(), samples.end());
        size_t idx = (size_t)(samples.size() * target_sparsity_percent / 100.0);
        if (idx < samples.size()) {
            actual_threshold = samples[idx];
        }
    }
    
    // Process each tensor
    int32_t tensors_processed = 0;
    std::vector<ConstantData> batch;
    batch.reserve(COPY_BATCH_SIZE);
    
    // Track unique float bits and their atom IDs
    std::unordered_map<uint32_t, int64_t> float_atom_ids;
    
    for (auto& [tensor_name, info] : header.tensors) {
        if (info.dtype != "F32" && info.dtype != "F16") {
            tensors_processed++;
            continue;
        }
        
        result->total_parameters += info.total_elements;
        
        // Seek to tensor data
        fseek(fp, data_offset + info.data_offsets[0], SEEK_SET);
        
        int64_t byte_count = info.data_offsets[1] - info.data_offsets[0];
        int64_t float_count = byte_count / (info.dtype == "F32" ? 4 : 2);
        
        // Read in chunks
        const size_t chunk_size = 1000000;
        std::vector<float> chunk(chunk_size);
        
        int64_t processed = 0;
        while (processed < float_count) {
            size_t to_read = std::min(chunk_size, (size_t)(float_count - processed));
            
            if (info.dtype == "F32") {
                fread(chunk.data(), sizeof(float), to_read, fp);
            } else {
                // F16 - need conversion
                std::vector<uint16_t> f16_buf(to_read);
                fread(f16_buf.data(), sizeof(uint16_t), to_read, fp);
                // Simple F16 to F32 conversion (not handling all edge cases)
                for (size_t i = 0; i < to_read; i++) {
                    uint32_t h = f16_buf[i];
                    uint32_t sign = (h & 0x8000) << 16;
                    uint32_t exp = (h >> 10) & 0x1F;
                    uint32_t mant = h & 0x3FF;
                    
                    if (exp == 0) {
                        chunk[i] = 0.0f;
                    } else if (exp == 31) {
                        chunk[i] = mant ? NAN : (sign ? -INFINITY : INFINITY);
                    } else {
                        exp = exp + (127 - 15);
                        uint32_t f32 = sign | (exp << 23) | (mant << 13);
                        memcpy(&chunk[i], &f32, sizeof(float));
                    }
                }
            }
            
            // Process chunk
            for (size_t i = 0; i < to_read; i++) {
                float val = chunk[i];
                result->total_values++;
                
                // Sparsity check
                if (std::abs(val) < actual_threshold) {
                    result->skipped_values++;
                    continue;
                }
                
                result->stored_values++;
                
                // Get float bits
                uint32_t bits;
                memcpy(&bits, &val, sizeof(float));
                
                // Check if we already have this float
                if (float_atom_ids.find(bits) != float_atom_ids.end()) {
                    continue;  // Already processed
                }
                
                // Create constant data
                ConstantData c;
                c.seed_value = bits;
                c.seed_type = SEED_FLOAT_BITS;

                // Compute geometry from float bits
                AtomSeed seed;
                seed.type = SEED_FLOAT_BITS;
                seed.value.float_bits = bits;
                Point4D pt = compute_coords_from_seed(&seed);
                c.x = pt.x;
                c.y = pt.y;
                c.z = pt.z;
                c.m = pt.m;

                // Compute Hilbert index
                HilbertIndex hilbert = coords_to_hilbert(pt.x, pt.y, pt.z, pt.m);
                c.hilbert_high = hilbert.high;
                c.hilbert_low = hilbert.low;

                // Compute content hash
                c.hash = compute_seed_hash(bits);

                batch.push_back(c);
                float_atom_ids[bits] = 0;  // Will be updated after insert
                
                // Flush batch if full
                if (batch.size() >= COPY_BATCH_SIZE) {
                    int64_t inserted = bulk_insert_constants(conn, batch);
                    if (inserted < 0) {
                        fclose(fp);
                        snprintf(result->error_message, sizeof(result->error_message), "Bulk insert failed");
                        return HART_ERROR_DB_QUERY;
                    }
                    batch.clear();
                }
            }
            
            processed += to_read;
        }
        
        tensors_processed++;
        
        if (progress_callback) {
            double sparsity = result->total_values > 0 ? 100.0 * result->skipped_values / result->total_values : 0;
            progress_callback(tensor_name.c_str(), tensors_processed, result->tensor_count, 
                result->stored_values, sparsity, user_data);
        }
    }
    
    // Final batch
    if (!batch.empty()) {
        int64_t inserted = bulk_insert_constants(conn, batch);
        if (inserted < 0) {
            fclose(fp);
            snprintf(result->error_message, sizeof(result->error_message), "Final bulk insert failed");
            return HART_ERROR_DB_QUERY;
        }
    }
    
    fclose(fp);
    
    // Calculate final stats
    result->sparsity_percent = result->total_values > 0 ? 100.0 * result->skipped_values / result->total_values : 0;
    
    auto end_time = std::chrono::high_resolution_clock::now();
    result->processing_time_ms = std::chrono::duration_cast<std::chrono::milliseconds>(end_time - start_time).count();
    
    if (progress_callback) {
        progress_callback("Complete", result->tensor_count, result->tensor_count, 
            result->stored_values, result->sparsity_percent, user_data);
    }
    
    return HART_OK;
}

HART_API HartResult hart_ingest_vocabulary(
    PGconn* conn,
    const char* filepath,
    const char* model_name,
    int64_t* result)
{
    // TODO: Implement vocabulary ingestion
    *result = 0;
    return HART_OK;
}

HART_API HartResult hart_batch_lookup_atoms(
    PGconn* conn,
    const ContentHash* hashes,
    size_t count,
    int64_t* out_ids)
{
    if (!conn || !hashes || !out_ids) return HART_ERROR_INVALID_INPUT;
    
    // Initialize output to 0 (not found)
    memset(out_ids, 0, count * sizeof(int64_t));
    
    // Query in batches
    const size_t batch_size = 1000;
    
    for (size_t offset = 0; offset < count; offset += batch_size) {
        size_t batch_count = std::min(batch_size, count - offset);
        
        std::string sql = "SELECT content_hash, id FROM constant WHERE content_hash IN (";
        for (size_t i = 0; i < batch_count; i++) {
            if (i > 0) sql += ",";
            sql += "'\\x";
            char hex[65];
            for (int j = 0; j < 32; j++) {
                sprintf(hex + j * 2, "%02x", hashes[offset + i].bytes[j]);
            }
            hex[64] = '\0';
            sql += hex;
            sql += "'";
        }
        sql += ")";
        
        PGresult* res = PQexec(conn, sql.c_str());
        if (PQresultStatus(res) != PGRES_TUPLES_OK) {
            PQclear(res);
            return HART_ERROR_DB_QUERY;
        }
        
        int rows = PQntuples(res);
        for (int r = 0; r < rows; r++) {
            const char* hash_hex = PQgetvalue(res, r, 0);
            int64_t id = strtoll(PQgetvalue(res, r, 1), nullptr, 10);
            
            // Find matching hash in our input
            ContentHash found_hash;
            for (int j = 0; j < 32; j++) {
                sscanf(hash_hex + j * 2, "%2hhx", &found_hash.bytes[j]);
            }
            
            for (size_t i = 0; i < batch_count; i++) {
                if (memcmp(hashes[offset + i].bytes, found_hash.bytes, 32) == 0) {
                    out_ids[offset + i] = id;
                    break;
                }
            }
        }
        
        PQclear(res);
    }
    
    return HART_OK;
}

} // extern "C"
