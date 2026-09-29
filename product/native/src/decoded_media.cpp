#include "kachinco_native.h"
#include "buffer.hpp"
#include <cmath>
#include <cstring>
#include <limits>
static_assert(sizeof(float) == 4 && std::numeric_limits<float>::is_iec559);
namespace {
constexpr uint32_t max_decoded_bytes = 256 * 1024 * 1024;
}
int32_t KN_CALL kn_decode_rgba(const uint8_t* data, uint32_t size, int32_t width, int32_t height, kn_buffer** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = nullptr;
    if ((!data && size) || width <= 0 || height <= 0) return KN_INVALID_ARGUMENT;
    const uint64_t expected = static_cast<uint64_t>(width) * static_cast<uint32_t>(height) * 4;
    if (expected > max_decoded_bytes) return KN_INVALID_ARGUMENT;
    if (!size) return KN_END_OF_STREAM;
    if (size != expected) return KN_INVALID_MEDIA;
    try {
        auto buffer = std::make_unique<kn_buffer>();
        buffer->data = std::make_shared<std::vector<uint8_t>>(data, data + size);
        *output = buffer.release(); return KN_OK;
    } catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
      catch (...) { return KN_INTERNAL_ERROR; }
}
int32_t KN_CALL kn_decode_pcm(const uint8_t* data, uint32_t size, int32_t sample_count, int32_t channels, kn_buffer** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = nullptr;
    if ((!data && size) || sample_count < 0 || channels <= 0) return KN_INVALID_ARGUMENT;
    const uint64_t frame_bytes = static_cast<uint64_t>(channels) * 4;
    const uint64_t expected = static_cast<uint64_t>(sample_count) * frame_bytes;
    if (expected > max_decoded_bytes) return KN_INVALID_ARGUMENT;
    if (size > expected || size % frame_bytes != 0) return KN_INVALID_MEDIA;
    try {
        auto bytes = std::make_shared<std::vector<uint8_t>>(static_cast<size_t>(expected), 0);
        for (uint32_t i = 0; i < size; i += 4) {
            uint32_t bits = static_cast<uint32_t>(data[i]) | static_cast<uint32_t>(data[i + 1]) << 8 |
                static_cast<uint32_t>(data[i + 2]) << 16 | static_cast<uint32_t>(data[i + 3]) << 24;
            float sample; std::memcpy(&sample, &bits, sizeof(sample));
            if (!std::isfinite(sample)) return KN_INVALID_MEDIA;
        }
        if (size) std::memcpy(bytes->data(), data, size);
        auto buffer = std::make_unique<kn_buffer>(); buffer->data = std::move(bytes);
        *output = buffer.release(); return KN_OK;
    } catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
      catch (...) { return KN_INTERNAL_ERROR; }
}
