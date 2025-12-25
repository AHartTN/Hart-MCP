cmake_minimum_required(VERSION 3.20)
project(hartonomous_native VERSION 1.0.0 LANGUAGES CXX C)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 11)

# Build type
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# Compiler flags for maximum correctness (no unsafe optimizations) + SIMD
if(MSVC)
    add_compile_options(/W4 /fp:precise /GS /arch:AVX2)
else()
    add_compile_options(-Wall -Wextra -ffp-contract=off -mavx2 -mfma)
endif()

# Find PostgreSQL
set(PostgreSQL_ROOT "D:/PostgreSQL/18")
set(PostgreSQL_INCLUDE_DIR "${PostgreSQL_ROOT}/include")
set(PostgreSQL_LIBRARY_DIR "${PostgreSQL_ROOT}/lib")
include_directories(${PostgreSQL_INCLUDE_DIR})
link_directories(${PostgreSQL_LIBRARY_DIR})

# Include directories
include_directories(
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/external/blake3/c
)

# BLAKE3 source files (reference implementation)
set(BLAKE3_SOURCES
    external/blake3/c/blake3.c
    external/blake3/c/blake3_dispatch.c
    external/blake3/c/blake3_portable.c
)

# Platform-specific BLAKE3 optimizations for Windows x64
if(WIN32 AND CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
    enable_language(ASM_MASM)
    set(BLAKE3_SOURCES ${BLAKE3_SOURCES}
        external/blake3/c/blake3_sse2_x86-64_windows_msvc.asm
        external/blake3/c/blake3_sse41_x86-64_windows_msvc.asm
        external/blake3/c/blake3_avx2_x86-64_windows_msvc.asm
        external/blake3/c/blake3_avx512_x86-64_windows_msvc.asm
    )
endif()

# Hart.MCP.Native source files (correct filenames)
set(HARTONOMOUS_SOURCES
    src/atom_seed.c
    src/hilbert.c
    src/content_hash.c
    src/simd.c
    src/db_connection.cpp
    src/bulk_ingestion.cpp
)

# Build shared library
add_library(hartonomous_native SHARED
    ${HARTONOMOUS_SOURCES}
    ${BLAKE3_SOURCES}
)

# Define HARTONOMOUS_EXPORTS when building the DLL
target_compile_definitions(hartonomous_native PRIVATE HARTONOMOUS_EXPORTS)

# Link PostgreSQL
target_link_libraries(hartonomous_native
    libpq
)

# Set output name and generate import library
set_target_properties(hartonomous_native PROPERTIES
    OUTPUT_NAME "hartonomous_native"
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
    WINDOWS_EXPORT_ALL_SYMBOLS ON
)

# Tests
enable_testing()

add_executable(test_atom_seed tests/test_atom_seed.c)
target_link_libraries(test_atom_seed hartonomous_native)
add_test(NAME test_atom_seed COMMAND test_atom_seed)

add_executable(test_hilbert tests/test_hilbert.c)
target_link_libraries(test_hilbert hartonomous_native)
add_test(NAME test_hilbert COMMAND test_hilbert)

# Install targets
install(TARGETS hartonomous_native
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
    RUNTIME DESTINATION bin
)

install(DIRECTORY include/
    DESTINATION include
    FILES_MATCHING PATTERN "*.h"
)
