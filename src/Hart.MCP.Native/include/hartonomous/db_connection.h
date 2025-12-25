#ifndef HARTONOMOUS_DB_CONNECTION_H
#define HARTONOMOUS_DB_CONNECTION_H

#include "types.h"
#include <libpq-fe.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Initialize database connection.
 * 
 * @param conninfo PostgreSQL connection string (e.g., "host=localhost port=5432 dbname=HART-MCP user=hartonomous password=hartonomous")
 * @return Connection handle or NULL on failure
 */
PGconn* hart_db_connect(const char* conninfo);

/**
 * Close database connection.
 */
void hart_db_disconnect(PGconn* conn);

/**
 * Create atom table schema if it doesn't exist.
 * 
 * @param conn Database connection
 * @return HART_OK on success
 */
HartResult hart_db_create_schema(PGconn* conn);

/**
 * Insert or get existing atom (upsert by content_hash).
 * 
 * @param conn Database connection
 * @param hilbert Hilbert index (for B-tree indexing)
 * @param geom_wkt WKT representation of geometry (PointZM, LineString ZM, etc.)
 * @param hash Content hash (BLAKE3-256)
 * @param out_id Returned atom ID
 * @return HART_OK on success
 */
HartResult hart_db_upsert_atom(
    PGconn* conn,
    const HilbertIndex* hilbert,
    const char* geom_wkt,
    const ContentHash* hash,
    int64_t* out_id
);

/**
 * Get atom geometry by ID.
 * 
 * @param conn Database connection
 * @param atom_id Atom ID
 * @param out_wkt Output WKT string (caller must free)
 * @return HART_OK on success
 */
HartResult hart_db_get_atom_geom(PGconn* conn, int64_t atom_id, char** out_wkt);

/**
 * Find K nearest neighbors to a query point.
 * 
 * @param conn Database connection
 * @param query_geom_wkt Query geometry (WKT)
 * @param k Number of neighbors
 * @param out_ids Output array of atom IDs (caller must free)
 * @param out_distances Output array of distances (caller must free)
 * @return HART_OK on success
 */
HartResult hart_db_knn_search(
    PGconn* conn,
    const char* query_geom_wkt,
    int k,
    int64_t** out_ids,
    double** out_distances
);

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_DB_CONNECTION_H
