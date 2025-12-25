#include "hartonomous/db_connection.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

PGconn* hart_db_connect(const char* conninfo) {
    PGconn* conn = PQconnectdb(conninfo);
    
    if (PQstatus(conn) != CONNECTION_OK) {
        fprintf(stderr, "Connection to database failed: %s\n", PQerrorMessage(conn));
        PQfinish(conn);
        return NULL;
    }
    
    return conn;
}

void hart_db_disconnect(PGconn* conn) {
    if (conn) {
        PQfinish(conn);
    }
}

HartResult hart_db_create_schema(PGconn* conn) {
    if (!conn) return HART_ERROR_DB_CONNECTION;
    
    const char* sql = 
        "CREATE TABLE IF NOT EXISTS atom ("
        "    id BIGSERIAL PRIMARY KEY,"
        "    hilbert_high BIGINT NOT NULL,"
        "    hilbert_low BIGINT NOT NULL,"
        "    geom GEOMETRY(GEOMETRYZM, 0) NOT NULL,"
        "    content_hash BYTEA NOT NULL UNIQUE"
        ");"
        
        "CREATE INDEX IF NOT EXISTS idx_atom_geom ON atom USING GIST (geom);"
        "CREATE INDEX IF NOT EXISTS idx_atom_hilbert ON atom (hilbert_high, hilbert_low);"
        "CREATE INDEX IF NOT EXISTS idx_atom_hash ON atom USING HASH (content_hash);";
    
    PGresult* res = PQexec(conn, sql);
    
    if (PQresultStatus(res) != PGRES_COMMAND_OK) {
        fprintf(stderr, "Schema creation failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return HART_ERROR_DB_QUERY;
    }
    
    PQclear(res);
    return HART_OK;
}

HartResult hart_db_upsert_atom(
    PGconn* conn,
    const HilbertIndex* hilbert,
    const char* geom_wkt,
    const ContentHash* hash,
    int64_t* out_id)
{
    if (!conn || !hilbert || !geom_wkt || !hash || !out_id) {
        return HART_ERROR_INVALID_INPUT;
    }
    
    // Build SQL with binary parameters for hash
    const char* sql = 
        "INSERT INTO atom (hilbert_high, hilbert_low, geom, content_hash) "
        "VALUES ($1, $2, ST_GeomFromText($3, 0), $4) "
        "ON CONFLICT (content_hash) DO UPDATE SET id = atom.id "
        "RETURNING id";
    
    // Convert parameters to strings
    char hilbert_high_str[32];
    char hilbert_low_str[32];
    snprintf(hilbert_high_str, sizeof(hilbert_high_str), "%llu", (unsigned long long)hilbert->high);
    snprintf(hilbert_low_str, sizeof(hilbert_low_str), "%llu", (unsigned long long)hilbert->low);
    
    const char* param_values[4] = {
        hilbert_high_str,
        hilbert_low_str,
        geom_wkt,
        (const char*)hash->bytes
    };
    
    int param_lengths[4] = {
        0, 0, 0, 32  // Binary hash
    };
    
    int param_formats[4] = {
        0, 0, 0, 1  // Binary format for hash
    };
    
    PGresult* res = PQexecParams(conn, sql, 4, NULL, param_values, param_lengths, param_formats, 0);
    
    if (PQresultStatus(res) != PGRES_TUPLES_OK) {
        fprintf(stderr, "Atom upsert failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return HART_ERROR_DB_QUERY;
    }
    
    if (PQntuples(res) == 0) {
        PQclear(res);
        return HART_ERROR_NOT_FOUND;
    }
    
    *out_id = atoll(PQgetvalue(res, 0, 0));
    PQclear(res);
    
    return HART_OK;
}

HartResult hart_db_get_atom_geom(PGconn* conn, int64_t atom_id, char** out_wkt) {
    if (!conn || !out_wkt) return HART_ERROR_INVALID_INPUT;
    
    const char* sql = "SELECT ST_AsText(geom) FROM atom WHERE id = $1";
    
    char id_str[32];
    snprintf(id_str, sizeof(id_str), "%lld", (long long)atom_id);
    
    const char* param_values[1] = { id_str };
    
    PGresult* res = PQexecParams(conn, sql, 1, NULL, param_values, NULL, NULL, 0);
    
    if (PQresultStatus(res) != PGRES_TUPLES_OK) {
        fprintf(stderr, "Get atom geometry failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return HART_ERROR_DB_QUERY;
    }
    
    if (PQntuples(res) == 0) {
        PQclear(res);
        return HART_ERROR_NOT_FOUND;
    }
    
    const char* wkt = PQgetvalue(res, 0, 0);
    *out_wkt = strdup(wkt);
    
    PQclear(res);
    return HART_OK;
}

HartResult hart_db_knn_search(
    PGconn* conn,
    const char* query_geom_wkt,
    int k,
    int64_t** out_ids,
    double** out_distances)
{
    if (!conn || !query_geom_wkt || k <= 0 || !out_ids || !out_distances) {
        return HART_ERROR_INVALID_INPUT;
    }
    
    char sql[512];
    snprintf(sql, sizeof(sql),
        "SELECT id, ST_Distance(geom, ST_GeomFromText($1, 0)) as dist "
        "FROM atom "
        "ORDER BY geom <-> ST_GeomFromText($1, 0) "
        "LIMIT %d", k);
    
    const char* param_values[1] = { query_geom_wkt };
    
    PGresult* res = PQexecParams(conn, sql, 1, NULL, param_values, NULL, NULL, 0);
    
    if (PQresultStatus(res) != PGRES_TUPLES_OK) {
        fprintf(stderr, "KNN search failed: %s\n", PQerrorMessage(conn));
        PQclear(res);
        return HART_ERROR_DB_QUERY;
    }
    
    int n = PQntuples(res);
    
    *out_ids = (int64_t*)malloc(sizeof(int64_t) * n);
    *out_distances = (double*)malloc(sizeof(double) * n);
    
    if (!*out_ids || !*out_distances) {
        free(*out_ids);
        free(*out_distances);
        PQclear(res);
        return HART_ERROR_OUT_OF_MEMORY;
    }
    
    for (int i = 0; i < n; i++) {
        (*out_ids)[i] = atoll(PQgetvalue(res, i, 0));
        (*out_distances)[i] = atof(PQgetvalue(res, i, 1));
    }
    
    PQclear(res);
    return HART_OK;
}
