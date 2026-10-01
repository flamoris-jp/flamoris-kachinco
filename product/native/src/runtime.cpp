#include "kachinco_native.h"
#include "time_math.hpp"
#include <cstddef>
#include <limits>
#include <new>
#include <numeric>

struct kn_runtime { uint32_t abi = KN_ABI_VERSION; };
static_assert(sizeof(void*) == 8, "Only 64-bit hosts are supported");
static_assert(sizeof(kn_runtime_info) == 24 && offsetof(kn_runtime_info, capabilities) == 8);
static_assert(sizeof(kn_media_value) == 32 && offsetof(kn_media_value, width) == 16);
using namespace kn_time;
uint32_t KN_CALL kn_abi_version(void) noexcept { return KN_ABI_VERSION; }
const char* KN_CALL kn_status_message(int32_t status) noexcept {
    switch (status) {
    case KN_OK: return "OK";
    case KN_INVALID_ARGUMENT: return "NATIVE_INVALID_ARGUMENT";
    case KN_ABI_MISMATCH: return "NATIVE_ABI_MISMATCH";
    case KN_OVERFLOW: return "NATIVE_TIME_OVERFLOW";
    case KN_OUT_OF_MEMORY: return "NATIVE_OUT_OF_MEMORY";
    case KN_INTERNAL_ERROR: return "NATIVE_INTERNAL_ERROR";
    case KN_IO_ERROR: return "NATIVE_IO_ERROR";
    case KN_CANCELLED: return "NATIVE_CANCELLED";
    case KN_TIMEOUT: return "NATIVE_TIMEOUT";
    case KN_END_OF_STREAM: return "NATIVE_END_OF_STREAM";
    case KN_INVALID_MEDIA: return "NATIVE_INVALID_MEDIA";
    default: return "NATIVE_UNKNOWN_STATUS";
    }
}
int32_t KN_CALL kn_runtime_create(uint32_t requested_abi, kn_runtime** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = nullptr;
    if (requested_abi != KN_ABI_VERSION) return KN_ABI_MISMATCH;
    try { *output = new kn_runtime(); return KN_OK; }
    catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
    catch (...) { return KN_INTERNAL_ERROR; }
}
void KN_CALL kn_runtime_destroy(kn_runtime* runtime) noexcept { delete runtime; }
int32_t KN_CALL kn_runtime_get_info(const kn_runtime* runtime, kn_runtime_info* output, uint32_t output_size) noexcept {
    if (!output || output_size != sizeof(*output)) return KN_INVALID_ARGUMENT;
    *output = {};
    if (!runtime) return KN_INVALID_ARGUMENT;
    *output = {runtime->abi, 0, KN_CAP_TIME | KN_CAP_VALUE | KN_CAP_PROCESS | KN_CAP_CACHE | KN_CAP_DECODED_MEDIA | KN_CAP_TIMELINE | KN_CAP_COMPOSITION | KN_CAP_PLAYBACK | KN_CAP_EDITOR | KN_CAP_PROJECT_CODEC | KN_CAP_TIME_QUERIES | KN_CAP_PREVIEW_PRESENTATION, KN_TICKS_PER_SECOND};
    return KN_OK;
}
int32_t KN_CALL kn_value_roundtrip(const kn_runtime* runtime, const kn_media_value* input, uint32_t input_size, kn_media_value* output, uint32_t output_size) noexcept {
    if (!output || output_size != sizeof(*output)) return KN_INVALID_ARGUMENT;
    // Aliased input/output is supported. Copy only after verifying input size.
    if (!runtime || !input || input_size != sizeof(*input)) { *output = {}; return KN_INVALID_ARGUMENT; }
    *output = *input;
    return KN_OK;
}
int32_t KN_CALL kn_frame_rate_is_valid(const kn_runtime* runtime, int32_t numerator, int32_t denominator, int32_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime) return KN_INVALID_ARGUMENT;
    *output = valid_fps(numerator, denominator) ? 1 : 0;
    return KN_OK;
}
int32_t KN_CALL kn_frame_to_ticks(const kn_runtime* runtime, int64_t index, int32_t numerator, int32_t denominator, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime || index < 0 || !valid_fps(numerator, denominator)) return KN_INVALID_ARGUMENT;
    return rounded(index, static_cast<uint64_t>(KN_TICKS_PER_SECOND) * static_cast<uint32_t>(denominator), static_cast<uint32_t>(numerator), output);
}
int32_t KN_CALL kn_ticks_to_frame(const kn_runtime* runtime, int64_t tick, int32_t numerator, int32_t denominator, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime || tick < 0 || !valid_fps(numerator, denominator)) return KN_INVALID_ARGUMENT;
    return rounded(tick, static_cast<uint32_t>(numerator), static_cast<uint64_t>(KN_TICKS_PER_SECOND) * static_cast<uint32_t>(denominator), output);
}
int32_t KN_CALL kn_frame_count(const kn_runtime* runtime, int64_t duration, int32_t numerator, int32_t denominator, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime || duration < 0 || !valid_fps(numerator, denominator)) return KN_INVALID_ARGUMENT;
    return count(duration, static_cast<uint32_t>(numerator), static_cast<uint64_t>(KN_TICKS_PER_SECOND) * static_cast<uint32_t>(denominator), output);
}
int32_t KN_CALL kn_sample_to_ticks(const kn_runtime* runtime, int64_t index, int32_t sample_rate, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime || index < 0 || sample_rate <= 0) return KN_INVALID_ARGUMENT;
    return rounded(index, KN_TICKS_PER_SECOND, static_cast<uint32_t>(sample_rate), output);
}
int32_t KN_CALL kn_sample_count(const kn_runtime* runtime, int64_t duration, int32_t sample_rate, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime || duration < 0 || sample_rate <= 0) return KN_INVALID_ARGUMENT;
    return count(duration, static_cast<uint32_t>(sample_rate), KN_TICKS_PER_SECOND, output);
}
