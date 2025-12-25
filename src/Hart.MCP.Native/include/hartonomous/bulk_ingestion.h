#ifndef HARTONOMOUS_BULK_INGESTION_H
#define HARTONOMOUS_BULK_INGESTION_H

#include "types.h"
#include "db_connection.h"
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * SafeTensor ingestion result
 */
typedef struct {
    int64_t root_atom_id;
    int32_t tensor_count;
    int64_t total_parameters;
    int64_t total_values;
    int64_t stored_values;
    int64_t skipped_values;
    double sparsity_percent;
    int64_t processing_time_ms;
    char error_message[512];
} SafeTensorResult;

/**
 * Progress callback for ingestion
 */
typedef void (*IngestionProgressCallback)(
    const char* phase,
    int32_t tensors_processed,
    int32_t tensors_total,
    int64_t values_processed,
    double sparsity_percent,
    void* user_data
);

/**
 * Bulk ingest a SafeTensor file using PostgreSQL COPY.
 * 
 * This is the FAST path - direct binary protocol to PostgreSQL.
 * - Streams file from disk
 * - Computes hashes/geometry in parallel (AVX2)
 * - Uses COPY binary format for bulk insert
 * - Batches by tensor for progress reporting
 * 
 * @param conn PostgreSQL connection
 * @param filepath Path to .safetensors file
 * @param model_name Name for this model
 * @param sparsity_threshold Skip values where |x| < threshold (use 0.0 for none, 0.01 for ~25% sparse)
 * @param target_sparsity_percent If > 0, auto-compute threshold to achieve this sparsity (overrides sparsity_threshold)
 * @param progress_callback Optional callback for progress updates
 * @param user_data User data passed to callback
 * @param result Output result structure
 * @return HART_OK on success
 */
HART_API HartResult hart_ingest_safetensor(
    PGconn* conn,
    const char* filepath,
    const char* model_name,
    float sparsity_threshold,
    float target_sparsity_percent,
    IngestionProgressCallback progress_callback,
    void* user_data,
    SafeTensorResult* result
);

/**
 * Bulk create Unicode codepoint atoms (BMP or full Unicode).
 * Uses COPY for maximum speed.
 * 
 * @param conn PostgreSQL connection
 * @param start_codepoint Starting codepoint (usually 0)
 * @param end_codepoint Ending codepoint (65535 for BMP, 0x10FFFF for full)
 * @param progress_callback Optional progress callback
 * @param user_data User data for callback
 * @return Number of atoms created, or negative error code
 */
HART_API int64_t hart_seed_unicode(
    PGconn* conn,
    uint32_t start_codepoint,
    uint32_t end_codepoint,
    IngestionProgressCallback progress_callback,
    void* user_data
);

/**
 * Bulk ingest vocabulary from tokenizer.json.
 * 
 * @param conn PostgreSQL connection
 * @param filepath Path to tokenizer.json
 * @param model_name Model name for metadata
 * @param result Output: number of tokens ingested
 * @return HART_OK on success
 */
HART_API HartResult hart_ingest_vocabulary(
    PGconn* conn,
    const char* filepath,
    const char* model_name,
    int64_t* result
);

/**
 * Query existing atoms by content hash (batch).
 * Returns mapping of hash -> atom_id for existing atoms.
 * 
 * @param conn PostgreSQL connection
 * @param hashes Array of content hashes to look up
 * @param count Number of hashes
 * @param out_ids Output array of atom IDs (0 if not found)
 * @return HART_OK on success
 */
HART_API HartResult hart_batch_lookup_atoms(
    PGconn* conn,
    const ContentHash* hashes,
    size_t count,
    int64_t* out_ids
);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_BULK_INGESTION_H
