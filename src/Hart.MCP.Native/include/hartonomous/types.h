#ifndef HARTONOMOUS_TYPES_H
#define HARTONOMOUS_TYPES_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * DLL Export/Import macros
 * When building the DLL, define HARTONOMOUS_EXPORTS
 * When using the DLL, leave it undefined
 */
#ifdef _WIN32
    #ifdef HARTONOMOUS_EXPORTS
        #define HART_API __declspec(dllexport)
    #else
        #define HART_API __declspec(dllimport)
    #endif
#else
    #define HART_API
#endif

// 128-bit Hilbert index split into two 64-bit values
typedef struct {
    uint64_t high;
    uint64_t low;
} HilbertIndex;

// 4D point on hypersphere (EXACT double precision, NO quantization)
typedef struct {
    double x;
    double y;
    double z;
    double m;
} Point4D;

// BLAKE3-256 content hash (32 bytes)
typedef struct {
    uint8_t bytes[32];
} ContentHash;

// Character category for landmark projection
typedef enum {
    CHAR_LETTER_UPPER,
    CHAR_LETTER_LOWER,
    CHAR_DIGIT,
    CHAR_PUNCTUATION,
    CHAR_WHITESPACE,
    CHAR_SYMBOL,
    CHAR_CONTROL,
    CHAR_OTHER
} CharCategory;

// Result codes
typedef enum {
    HART_OK = 0,
    HART_ERROR_DB_CONNECTION = -1,
    HART_ERROR_DB_QUERY = -2,
    HART_ERROR_INVALID_INPUT = -3,
    HART_ERROR_OUT_OF_MEMORY = -4,
    HART_ERROR_HASH_COLLISION = -5,
    HART_ERROR_NOT_FOUND = -6
} HartResult;

#ifdef __cplusplus
}
#endif

#endif // HARTONOMOUS_TYPES_H
