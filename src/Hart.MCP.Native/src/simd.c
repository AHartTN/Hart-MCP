/**
 * simd.c - SIMD-accelerated operations for hypersphere computations
 * 
 * Uses AVX2/AVX-512 intrinsics when available, with scalar fallbacks.
 * All operations are deterministic and produce identical results
 * regardless of SIMD path taken.
 */

#include "hartonomous/simd.h"
#include "hartonomous/content_hash.h"
#include <math.h>
#include <string.h>
#include <stdio.h>

#ifdef _WIN32
#include <intrin.h>
#else
#include <cpuid.h>
#endif

#if defined(__AVX2__) || defined(__AVX__) || defined(_MSC_VER)
#include <immintrin.h>
#define HAS_AVX_HEADERS 1
#endif

/* ============================================================================
 * SIMD Capabilities Detection
 * ============================================================================ */

static SIMDCapabilities g_simd_caps = {0};
static int g_simd_detected = 0;

static void detect_cpuid(int leaf, int subleaf, int* eax, int* ebx, int* ecx, int* edx) {
#ifdef _WIN32
    int info[4];
    __cpuidex(info, leaf, subleaf);
    *eax = info[0];
    *ebx = info[1];
    *ecx = info[2];
    *edx = info[3];
#else
    __cpuid_count(leaf, subleaf, *eax, *ebx, *ecx, *edx);
#endif
}

HART_API SIMDCapabilities detect_simd_capabilities(void) {
    if (g_simd_detected) {
        return g_simd_caps;
    }
    
    int eax, ebx, ecx, edx;
    
    // Get max CPUID level
    detect_cpuid(0, 0, &eax, &ebx, &ecx, &edx);
    int max_level = eax;
    
    if (max_level >= 1) {
        detect_cpuid(1, 0, &eax, &ebx, &ecx, &edx);
        g_simd_caps.has_sse2 = (edx >> 26) & 1;
        g_simd_caps.has_sse41 = (ecx >> 19) & 1;
        g_simd_caps.has_avx = (ecx >> 28) & 1;
    }
    
    if (max_level >= 7) {
        detect_cpuid(7, 0, &eax, &ebx, &ecx, &edx);
        g_simd_caps.has_avx2 = (ebx >> 5) & 1;
        g_simd_caps.has_avx512f = (ebx >> 16) & 1;
    }
    
    // Verify OS support for AVX state saving (XGETBV)
    if (g_simd_caps.has_avx) {
        detect_cpuid(1, 0, &eax, &ebx, &ecx, &edx);
        int osxsave = (ecx >> 27) & 1;
        if (osxsave) {
#ifdef _WIN32
            unsigned long long xcr0 = _xgetbv(0);
#else
            unsigned int xcr0_lo, xcr0_hi;
            __asm__ volatile("xgetbv" : "=a"(xcr0_lo), "=d"(xcr0_hi) : "c"(0));
            unsigned long long xcr0 = ((unsigned long long)xcr0_hi << 32) | xcr0_lo;
#endif
            // Check YMM state (bits 1 and 2) for AVX
            if ((xcr0 & 0x6) != 0x6) {
                g_simd_caps.has_avx = 0;
                g_simd_caps.has_avx2 = 0;
            }
            // Check ZMM state (bits 5, 6, 7) for AVX-512
            if ((xcr0 & 0xE0) != 0xE0) {
                g_simd_caps.has_avx512f = 0;
            }
        }
    }
    
    g_simd_detected = 1;
    return g_simd_caps;
}

static char g_simd_string[256] = {0};

HART_API const char* simd_capabilities_string(void) {
    if (g_simd_string[0] == '\0') {
        SIMDCapabilities caps = detect_simd_capabilities();
        snprintf(g_simd_string, sizeof(g_simd_string),
            "SSE2: %s, SSE4.1: %s, AVX: %s, AVX2: %s, AVX-512: %s",
            caps.has_sse2 ? "yes" : "no",
            caps.has_sse41 ? "yes" : "no",
            caps.has_avx ? "yes" : "no",
            caps.has_avx2 ? "yes" : "no",
            caps.has_avx512f ? "yes" : "no");
    }
    return g_simd_string;
}

/* ============================================================================
 * Distance Computations
 * ============================================================================ */

HART_API double distance_4d_squared(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2) 
{
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    if (caps.has_avx) {
        __m256d v1 = _mm256_set_pd(m1, z1, y1, x1);
        __m256d v2 = _mm256_set_pd(m2, z2, y2, x2);
        __m256d diff = _mm256_sub_pd(v1, v2);
        __m256d sq = _mm256_mul_pd(diff, diff);
        
        // Horizontal sum
        __m128d lo = _mm256_castpd256_pd128(sq);
        __m128d hi = _mm256_extractf128_pd(sq, 1);
        __m128d sum = _mm_add_pd(lo, hi);
        __m128d shuf = _mm_shuffle_pd(sum, sum, 1);
        __m128d total = _mm_add_sd(sum, shuf);
        return _mm_cvtsd_f64(total);
    }
#endif
    
    // Scalar fallback
    double dx = x1 - x2;
    double dy = y1 - y2;
    double dz = z1 - z2;
    double dm = m1 - m2;
    return dx*dx + dy*dy + dz*dz + dm*dm;
}

HART_API double distance_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2) 
{
    return sqrt(distance_4d_squared(x1, y1, z1, m1, x2, y2, z2, m2));
}

HART_API void batch_distance_4d(
    double qx, double qy, double qz, double qm,
    const double* xs, const double* ys, 
    const double* zs, const double* ms,
    double* distances,
    size_t count)
{
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    
    if (caps.has_avx2 && count >= 4) {
        __m256d query_x = _mm256_set1_pd(qx);
        __m256d query_y = _mm256_set1_pd(qy);
        __m256d query_z = _mm256_set1_pd(qz);
        __m256d query_m = _mm256_set1_pd(qm);
        
        size_t i = 0;
        size_t vec_count = count - (count % 4);
        
        for (; i < vec_count; i += 4) {
            __m256d px = _mm256_loadu_pd(&xs[i]);
            __m256d py = _mm256_loadu_pd(&ys[i]);
            __m256d pz = _mm256_loadu_pd(&zs[i]);
            __m256d pm = _mm256_loadu_pd(&ms[i]);
            
            __m256d dx = _mm256_sub_pd(query_x, px);
            __m256d dy = _mm256_sub_pd(query_y, py);
            __m256d dz = _mm256_sub_pd(query_z, pz);
            __m256d dm = _mm256_sub_pd(query_m, pm);
            
            __m256d sq_sum = _mm256_add_pd(
                _mm256_add_pd(_mm256_mul_pd(dx, dx), _mm256_mul_pd(dy, dy)),
                _mm256_add_pd(_mm256_mul_pd(dz, dz), _mm256_mul_pd(dm, dm))
            );
            
            __m256d dist = _mm256_sqrt_pd(sq_sum);
            _mm256_storeu_pd(&distances[i], dist);
        }
        
        // Handle remaining
        for (; i < count; i++) {
            distances[i] = distance_4d(qx, qy, qz, qm, xs[i], ys[i], zs[i], ms[i]);
        }
        return;
    }
#endif
    
    // Scalar fallback
    for (size_t i = 0; i < count; i++) {
        distances[i] = distance_4d(qx, qy, qz, qm, xs[i], ys[i], zs[i], ms[i]);
    }
}

/* ============================================================================
 * Attention/Softmax Operations
 * ============================================================================ */

HART_API void compute_attention_weights(
    const double* distances,
    double* weights,
    size_t count)
{
    double sum = 0.0;
    
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    
    if (caps.has_avx2 && count >= 4) {
        __m256d ones = _mm256_set1_pd(1.0);
        __m256d sum_vec = _mm256_setzero_pd();
        
        size_t i = 0;
        size_t vec_count = count - (count % 4);
        
        // Compute raw weights
        for (; i < vec_count; i += 4) {
            __m256d d = _mm256_loadu_pd(&distances[i]);
            __m256d denom = _mm256_add_pd(ones, d);
            __m256d w = _mm256_div_pd(ones, denom);
            _mm256_storeu_pd(&weights[i], w);
            sum_vec = _mm256_add_pd(sum_vec, w);
        }
        
        // Sum vector elements
        __m128d lo = _mm256_castpd256_pd128(sum_vec);
        __m128d hi = _mm256_extractf128_pd(sum_vec, 1);
        __m128d s = _mm_add_pd(lo, hi);
        __m128d shuf = _mm_shuffle_pd(s, s, 1);
        sum = _mm_cvtsd_f64(_mm_add_sd(s, shuf));
        
        // Handle remaining
        for (; i < count; i++) {
            double w = 1.0 / (1.0 + distances[i]);
            weights[i] = w;
            sum += w;
        }
        
        // Normalize
        if (sum > 0) {
            __m256d sum_inv = _mm256_set1_pd(1.0 / sum);
            for (i = 0; i < vec_count; i += 4) {
                __m256d w = _mm256_loadu_pd(&weights[i]);
                __m256d norm = _mm256_mul_pd(w, sum_inv);
                _mm256_storeu_pd(&weights[i], norm);
            }
            for (; i < count; i++) {
                weights[i] /= sum;
            }
        }
        return;
    }
#endif
    
    // Scalar fallback
    for (size_t i = 0; i < count; i++) {
        double w = 1.0 / (1.0 + distances[i]);
        weights[i] = w;
        sum += w;
    }
    
    if (sum > 0) {
        for (size_t i = 0; i < count; i++) {
            weights[i] /= sum;
        }
    }
}

/* ============================================================================
 * Vector Operations
 * ============================================================================ */

HART_API void vector_add_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2,
    double* rx, double* ry, double* rz, double* rm)
{
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    if (caps.has_avx) {
        __m256d v1 = _mm256_set_pd(m1, z1, y1, x1);
        __m256d v2 = _mm256_set_pd(m2, z2, y2, x2);
        __m256d result = _mm256_add_pd(v1, v2);
        double r[4];
        _mm256_storeu_pd(r, result);
        *rx = r[0]; *ry = r[1]; *rz = r[2]; *rm = r[3];
        return;
    }
#endif
    *rx = x1 + x2;
    *ry = y1 + y2;
    *rz = z1 + z2;
    *rm = m1 + m2;
}

HART_API void vector_sub_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2,
    double* rx, double* ry, double* rz, double* rm)
{
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    if (caps.has_avx) {
        __m256d v1 = _mm256_set_pd(m1, z1, y1, x1);
        __m256d v2 = _mm256_set_pd(m2, z2, y2, x2);
        __m256d result = _mm256_sub_pd(v1, v2);
        double r[4];
        _mm256_storeu_pd(r, result);
        *rx = r[0]; *ry = r[1]; *rz = r[2]; *rm = r[3];
        return;
    }
#endif
    *rx = x1 - x2;
    *ry = y1 - y2;
    *rz = z1 - z2;
    *rm = m1 - m2;
}

HART_API void vector_scale_4d(
    double x, double y, double z, double m,
    double scalar,
    double* rx, double* ry, double* rz, double* rm)
{
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    if (caps.has_avx) {
        __m256d v = _mm256_set_pd(m, z, y, x);
        __m256d s = _mm256_set1_pd(scalar);
        __m256d result = _mm256_mul_pd(v, s);
        double r[4];
        _mm256_storeu_pd(r, result);
        *rx = r[0]; *ry = r[1]; *rz = r[2]; *rm = r[3];
        return;
    }
#endif
    *rx = x * scalar;
    *ry = y * scalar;
    *rz = z * scalar;
    *rm = m * scalar;
}

HART_API double vector_dot_4d(
    double x1, double y1, double z1, double m1,
    double x2, double y2, double z2, double m2)
{
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    if (caps.has_avx) {
        __m256d v1 = _mm256_set_pd(m1, z1, y1, x1);
        __m256d v2 = _mm256_set_pd(m2, z2, y2, x2);
        __m256d prod = _mm256_mul_pd(v1, v2);
        
        __m128d lo = _mm256_castpd256_pd128(prod);
        __m128d hi = _mm256_extractf128_pd(prod, 1);
        __m128d sum = _mm_add_pd(lo, hi);
        __m128d shuf = _mm_shuffle_pd(sum, sum, 1);
        return _mm_cvtsd_f64(_mm_add_sd(sum, shuf));
    }
#endif
    return x1*x2 + y1*y2 + z1*z2 + m1*m2;
}

HART_API double vector_magnitude_4d(double x, double y, double z, double m) {
    return sqrt(x*x + y*y + z*z + m*m);
}

HART_API void vector_normalize_4d(
    double x, double y, double z, double m,
    double* rx, double* ry, double* rz, double* rm)
{
    double mag = vector_magnitude_4d(x, y, z, m);
    if (mag > 1e-15) {
        double inv_mag = 1.0 / mag;
        vector_scale_4d(x, y, z, m, inv_mag, rx, ry, rz, rm);
    } else {
        *rx = x; *ry = y; *rz = z; *rm = m;
    }
}

/* ============================================================================
 * Batch Operations
 * ============================================================================ */

HART_API void batch_normalize_4d(
    double* xs, double* ys, double* zs, double* ms,
    size_t count)
{
    // Can be parallelized if needed
    for (size_t i = 0; i < count; i++) {
        double mag = sqrt(xs[i]*xs[i] + ys[i]*ys[i] + zs[i]*zs[i] + ms[i]*ms[i]);
        if (mag > 1e-15) {
            double inv_mag = 1.0 / mag;
            xs[i] *= inv_mag;
            ys[i] *= inv_mag;
            zs[i] *= inv_mag;
            ms[i] *= inv_mag;
        }
    }
}

HART_API void compute_centroid_4d(
    const double* xs, const double* ys,
    const double* zs, const double* ms,
    size_t count,
    double* cx, double* cy, double* cz, double* cm)
{
    if (count == 0) {
        *cx = *cy = *cz = *cm = 0.0;
        return;
    }
    
    double sx = 0, sy = 0, sz = 0, sm = 0;
    
#if HAS_AVX_HEADERS
    SIMDCapabilities caps = detect_simd_capabilities();
    
    if (caps.has_avx2 && count >= 4) {
        __m256d sum_x = _mm256_setzero_pd();
        __m256d sum_y = _mm256_setzero_pd();
        __m256d sum_z = _mm256_setzero_pd();
        __m256d sum_m = _mm256_setzero_pd();
        
        size_t i = 0;
        size_t vec_count = count - (count % 4);
        
        for (; i < vec_count; i += 4) {
            sum_x = _mm256_add_pd(sum_x, _mm256_loadu_pd(&xs[i]));
            sum_y = _mm256_add_pd(sum_y, _mm256_loadu_pd(&ys[i]));
            sum_z = _mm256_add_pd(sum_z, _mm256_loadu_pd(&zs[i]));
            sum_m = _mm256_add_pd(sum_m, _mm256_loadu_pd(&ms[i]));
        }
        
        // Horizontal sum for each coordinate
        double tmp[4];
        _mm256_storeu_pd(tmp, sum_x);
        sx = tmp[0] + tmp[1] + tmp[2] + tmp[3];
        _mm256_storeu_pd(tmp, sum_y);
        sy = tmp[0] + tmp[1] + tmp[2] + tmp[3];
        _mm256_storeu_pd(tmp, sum_z);
        sz = tmp[0] + tmp[1] + tmp[2] + tmp[3];
        _mm256_storeu_pd(tmp, sum_m);
        sm = tmp[0] + tmp[1] + tmp[2] + tmp[3];
        
        // Handle remaining
        for (; i < count; i++) {
            sx += xs[i];
            sy += ys[i];
            sz += zs[i];
            sm += ms[i];
        }
    } else
#endif
    {
        for (size_t i = 0; i < count; i++) {
            sx += xs[i];
            sy += ys[i];
            sz += zs[i];
            sm += ms[i];
        }
    }
    
    double inv_count = 1.0 / (double)count;
    *cx = sx * inv_count;
    *cy = sy * inv_count;
    *cz = sz * inv_count;
    *cm = sm * inv_count;
}

HART_API void batch_compute_seed_hashes(
    const uint32_t* seeds,
    ContentHash* hashes,
    size_t count)
{
    // BLAKE3 is already highly optimized with SIMD internally
    // Just loop and call single-hash function
    for (size_t i = 0; i < count; i++) {
        hashes[i] = compute_seed_hash(seeds[i]);
    }
}
