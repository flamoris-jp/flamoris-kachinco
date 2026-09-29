#ifndef KACHINCO_NATIVE_H
#define KACHINCO_NATIVE_H
#include <stdint.h>
#if defined(_WIN32)
#define KN_CALL __cdecl
#if defined(KN_BUILD)
#define KN_API __declspec(dllexport)
#else
#define KN_API __declspec(dllimport)
#endif
#else
#define KN_CALL
#define KN_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
#define KN_NOEXCEPT noexcept
extern "C" {
#else
#define KN_NOEXCEPT
#endif
#define KN_ABI_VERSION UINT32_C(1)
#define KN_CAP_TIME UINT64_C(1)
#define KN_CAP_VALUE UINT64_C(2)
#define KN_TICKS_PER_SECOND INT64_C(35280000)
#define KN_OK INT32_C(0)
#define KN_INVALID_ARGUMENT INT32_C(1)
#define KN_ABI_MISMATCH INT32_C(2)
#define KN_OVERFLOW INT32_C(3)
#define KN_OUT_OF_MEMORY INT32_C(4)
#define KN_INTERNAL_ERROR INT32_C(5)

typedef struct kn_runtime kn_runtime;
typedef struct kn_runtime_info {
    uint32_t abi_version;
    uint32_t reserved;
    uint64_t capabilities;
    int64_t ticks_per_second;
} kn_runtime_info;
/* Transport values only; no asset registration or editing authority. */
typedef struct kn_media_value {
    int64_t duration_ticks;
    int64_t source_in_ticks;
    int32_t width;
    int32_t height;
    int32_t fps_numerator;
    int32_t fps_denominator;
} kn_media_value;

KN_API uint32_t KN_CALL kn_abi_version(void) KN_NOEXCEPT;
KN_API const char* KN_CALL kn_status_message(int32_t status) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_runtime_create(uint32_t requested_abi, kn_runtime** output) KN_NOEXCEPT;
KN_API void KN_CALL kn_runtime_destroy(kn_runtime* runtime) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_runtime_get_info(const kn_runtime* runtime, kn_runtime_info* output, uint32_t output_size) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_value_roundtrip(const kn_runtime* runtime, const kn_media_value* input, uint32_t input_size, kn_media_value* output, uint32_t output_size) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_frame_to_ticks(const kn_runtime* runtime, int64_t index, int32_t numerator, int32_t denominator, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_frame_count(const kn_runtime* runtime, int64_t duration, int32_t numerator, int32_t denominator, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_sample_to_ticks(const kn_runtime* runtime, int64_t index, int32_t sample_rate, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_sample_count(const kn_runtime* runtime, int64_t duration, int32_t sample_rate, int64_t* output) KN_NOEXCEPT;
#ifdef __cplusplus
}
#endif
#endif
