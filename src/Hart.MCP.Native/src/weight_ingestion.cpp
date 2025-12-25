// Stub implementations
#include "hartonomous/types.h"
#include <libpq-fe.h>

// Placeholder for weight storage
int64_t hart_store_weight_edge(PGconn* conn, int64_t input_id, int64_t output_id, float weight) {
    return -1;  // TODO
}
