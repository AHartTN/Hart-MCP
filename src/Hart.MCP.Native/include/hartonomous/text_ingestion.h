#ifndef HARTONOMOUS_TEXT_INGESTION_H
#define HARTONOMOUS_TEXT_INGESTION_H

#include "types.h"
#include <libpq-fe.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Ingest UTF-8 text and return root document atom ID.
 * 
 * Process:
 * 1. Decode UTF-8 to codepoints
 * 2. Create constant atoms for each character
 * 3. Build LineString composition connecting characters
 * 4. Return composition atom ID
 * 
 * @param conn Database connection
 * @param text UTF-8 text
 * @param length Length in bytes
 * @param out_atom_id Returned root atom ID
 * @return HART_OK on success
 */
HartResult hart_ingest_text(
    PGconn* conn,
    const char* text,
    size_t length,
    int64_t* out_atom_id
);

/**
 * Reconstruct text from atom ID.
 * 
 * @param conn Database connection
 * @param atom_id Root atom ID
 * @param out_text Reconstructed UTF-8 text (caller must free)
 * @param out_length Length in bytes
 * @return HART_OK on success
 */
HartResult hart_reconstruct_text(
    PGconn* conn,
    int64_t atom_id,
    char** out_text,
    size_t* out_length
);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_TEXT_INGESTION_H
