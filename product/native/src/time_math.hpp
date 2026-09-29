#pragma once
#include "kachinco_native.h"
#include <numeric>
namespace kn_time {
// Only nonnegative time arithmetic is needed. All ABI divisors are < 2^63.
struct wide { uint64_t high; uint64_t low; };
inline wide multiply(uint64_t a, uint64_t b) noexcept {
    const uint64_t a0 = static_cast<uint32_t>(a), a1 = a >> 32;
    const uint64_t b0 = static_cast<uint32_t>(b), b1 = b >> 32;
    const uint64_t w0 = a0 * b0;
    const uint64_t t = a1 * b0 + (w0 >> 32);
    const uint64_t w1 = (t & UINT32_MAX) + a0 * b1;
    return {a1 * b1 + (t >> 32) + (w1 >> 32), (w1 << 32) | (w0 & UINT32_MAX)};
}
inline wide add(wide a, uint64_t b) noexcept {
    const uint64_t low = a.low + b;
    return {a.high + (low < a.low ? 1U : 0U), low};
}
inline int32_t divide(wide value, uint64_t divisor, int64_t* output) noexcept {
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
inline bool valid_fps(int32_t n, int32_t d) noexcept {
    return n > 0 && d > 0 && n >= d && static_cast<int64_t>(n) <= static_cast<int64_t>(d) * 240 && std::gcd(n, d) == 1;
}
inline int32_t rounded(int64_t index, uint64_t multiplier, uint64_t divisor, int64_t* output) noexcept {
    return divide(add(multiply(static_cast<uint64_t>(index), multiplier), divisor / 2), divisor, output);
}
inline int32_t count(int64_t duration, uint64_t rate, uint64_t scale, int64_t* output) noexcept {
    if (duration == 0) return KN_OK;
    const uint64_t twice_minus_one = static_cast<uint64_t>(duration) * 2 - 1;
    const uint64_t divisor = 2 * scale;
    return divide(add(multiply(twice_minus_one, rate), divisor - 1), divisor, output);
}
}
