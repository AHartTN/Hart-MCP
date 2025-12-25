// Stub implementations - to be completed
#include "hartonomous/types.h"
#include <libpq-fe.h>

// Placeholder for embedding ingestion
int64_t hart_ingest_embedding(PGconn* conn, const float* values, size_t dims) {
    return -1;  // TODO
}

// Placeholder for weight ingestion
int64_t hart_ingest_weights(PGconn* conn, const float* weights, size_t rows, size_t cols) {
    return -1;  // TODO
}
