#include "kachinco_native.h"
#include <cstddef>
#include <limits>
#include <new>
#include <numeric>

struct kn_runtime { uint32_t abi = KN_ABI_VERSION; };
static_assert(sizeof(void*) == 8, "Only 64-bit hosts are supported");
static_assert(sizeof(kn_runtime_info) == 24 && offsetof(kn_runtime_info, capabilities) == 8);
static_assert(sizeof(kn_media_value) == 32 && offsetof(kn_media_value, width) == 16);
namespace {
// Only nonnegative time arithmetic is needed. All ABI divisors are < 2^63.
struct wide { uint64_t high; uint64_t low; };
wide multiply(uint64_t a, uint64_t b) noexcept {
    const uint64_t a0 = static_cast<uint32_t>(a), a1 = a >> 32;
    const uint64_t b0 = static_cast<uint32_t>(b), b1 = b >> 32;
    const uint64_t w0 = a0 * b0;
    const uint64_t t = a1 * b0 + (w0 >> 32);
    const uint64_t w1 = (t & UINT32_MAX) + a0 * b1;
    return {a1 * b1 + (t >> 32) + (w1 >> 32), (w1 << 32) | (w0 & UINT32_MAX)};
}
wide add(wide a, uint64_t b) noexcept {
    const uint64_t low = a.low + b;
    return {a.high + (low < a.low ? 1U : 0U), low};
}
int32_t divide(wide value, uint64_t divisor, int64_t* output) noexcept {
    // Long division retains the exact remainder, never converting to floating point.
    uint64_t remainder = 0, quotient = 0;
    bool overflow = false;
    for (int bit = 127; bit >= 0; --bit) {
        const uint64_t next = bit >= 64 ? (value.high >> (bit - 64)) & 1U : (value.low >> bit) & 1U;
        remainder = (remainder << 1) | next;
        if (remainder >= divisor) {
            remainder -= divisor;
            if (bit >= 63) overflow = true;
            else quotient |= UINT64_C(1) << bit;
        }
    }
    if (overflow) return KN_OVERFLOW;
    *output = static_cast<int64_t>(quotient);
    return KN_OK;
}
bool valid_fps(int32_t n, int32_t d) noexcept {
    return n > 0 && d > 0 && n >= d && static_cast<int64_t>(n) <= static_cast<int64_t>(d) * 240 && std::gcd(n, d) == 1;
}
int32_t rounded(int64_t index, uint64_t multiplier, uint64_t divisor, int64_t* output) noexcept {
    return divide(add(multiply(static_cast<uint64_t>(index), multiplier), divisor / 2), divisor, output);
}
int32_t count(int64_t duration, uint64_t rate, uint64_t scale, int64_t* output) noexcept {
    if (duration == 0) return KN_OK;
    const uint64_t twice_minus_one = static_cast<uint64_t>(duration) * 2 - 1;
    const uint64_t divisor = 2 * scale;
    return divide(add(multiply(twice_minus_one, rate), divisor - 1), divisor, output);
}
}
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
    *output = {runtime->abi, 0, KN_CAP_TIME | KN_CAP_VALUE | KN_CAP_PROCESS | KN_CAP_CACHE, KN_TICKS_PER_SECOND};
    return KN_OK;
}
int32_t KN_CALL kn_value_roundtrip(const kn_runtime* runtime, const kn_media_value* input, uint32_t input_size, kn_media_value* output, uint32_t output_size) noexcept {
    if (!output || output_size != sizeof(*output)) return KN_INVALID_ARGUMENT;
    // Aliased input/output is supported. Copy only after verifying input size.
    if (!runtime || !input || input_size != sizeof(*input)) { *output = {}; return KN_INVALID_ARGUMENT; }
    *output = *input;
    return KN_OK;
}
int32_t KN_CALL kn_frame_to_ticks(const kn_runtime* runtime, int64_t index, int32_t numerator, int32_t denominator, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if (!runtime || index < 0 || !valid_fps(numerator, denominator)) return KN_INVALID_ARGUMENT;
    return rounded(index, static_cast<uint64_t>(KN_TICKS_PER_SECOND) * static_cast<uint32_t>(denominator), static_cast<uint32_t>(numerator), output);
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
