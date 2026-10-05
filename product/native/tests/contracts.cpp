#include "kachinco_native.h"
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
extern "C" int kn_c_header_check(void);
#define CHECK(x) do { if (!(x)) { std::cerr << "Failed at line " << __LINE__ << ": " << #x << '\n'; std::exit(1); } } while (false)
int main() {
    kn_buffer* decoded = nullptr;
    const uint8_t pixel[] = {10, 20, 30, 40};
    CHECK(kn_decode_rgba(pixel, 4, 1, 1, &decoded) == KN_OK && decoded != nullptr);
    kn_buffer_destroy(decoded);
    CHECK(kn_decode_rgba(nullptr, 0, 1, 1, &decoded) == KN_END_OF_STREAM && decoded == nullptr);
    CHECK(kn_decode_rgba(pixel, 3, 1, 1, &decoded) == KN_INVALID_MEDIA && decoded == nullptr);
    const uint8_t pcm[] = {0, 0, 0, 63, 0, 0, 128, 190};
    CHECK(kn_decode_pcm(pcm, 8, 2, 2, &decoded) == KN_OK);
    uint8_t decoded_samples[16];
    CHECK(kn_buffer_copy(decoded, decoded_samples, 16) == KN_OK);
    CHECK(std::memcmp(pcm, decoded_samples, 8) == 0);
    for (int i = 8; i < 16; ++i) CHECK(decoded_samples[i] == 0);
    kn_buffer_destroy(decoded);
    const uint8_t nan[] = {0, 0, 192, 127};
    CHECK(kn_decode_pcm(nan, 4, 1, 1, &decoded) == KN_INVALID_MEDIA && decoded == nullptr);
    CHECK(kn_decode_pcm(pcm, 4, 1, 2, &decoded) == KN_INVALID_MEDIA && decoded == nullptr);
    kn_cache* cache = nullptr;
    CHECK(kn_cache_create(10, 2, &cache) == KN_OK);
    const uint8_t data[] = {1, 2, 3, 4, 5};
    CHECK(kn_cache_put(cache, "a", data, 5, 5) == KN_OK);
    CHECK(kn_cache_put(cache, "b", data, 5, 5) == KN_OK);
    kn_buffer* lease = nullptr;
    CHECK(kn_cache_get(cache, "a", &lease) == KN_OK && lease != nullptr);
    CHECK(kn_cache_put(cache, "c", data, 5, 5) == KN_OK);
    kn_buffer* miss = nullptr;
    CHECK(kn_cache_get(cache, "b", &miss) == KN_OK && miss == nullptr);
    kn_cache_statistics stats{};
    CHECK(kn_cache_stats(cache, &stats, sizeof(stats)) == KN_OK);
    CHECK(stats.bytes == 10 && stats.entries == 2 && stats.hits == 1 && stats.misses == 1 && stats.evictions == 1);
    CHECK(kn_cache_clear(cache) == KN_OK);
    kn_cache_destroy(cache);
    uint8_t copy[5]{}; uint32_t size = 0;
    CHECK(kn_buffer_size(lease, &size) == KN_OK && size == 5);
    CHECK(kn_buffer_copy(lease, copy, 4) == KN_INVALID_ARGUMENT);
    CHECK(kn_buffer_copy(lease, copy, 5) == KN_OK && std::memcmp(data, copy, 5) == 0);
    kn_buffer_destroy(lease);
    CHECK(kn_cache_create(0, 1, &cache) == KN_OK);
    for (int i = 0; i < 10000; ++i) CHECK(kn_cache_put(cache, i % 2 ? "a" : "b", nullptr, 0, 0) == KN_OK);
    CHECK(kn_cache_stats(cache, &stats, sizeof(stats)) == KN_OK && stats.bytes == 0 && stats.entries == 1);
    kn_cache_destroy(cache);
    CHECK(kn_c_header_check());
    CHECK(kn_abi_version() == 1);
    CHECK(std::strcmp(kn_status_message(KN_ABI_MISMATCH), "NATIVE_ABI_MISMATCH") == 0);
    CHECK(std::strcmp(kn_status_message(99), "NATIVE_UNKNOWN_STATUS") == 0);
    CHECK(kn_runtime_create(1, nullptr) == KN_INVALID_ARGUMENT);
    kn_runtime* runtime = nullptr;
    CHECK(kn_runtime_create(2, &runtime) == KN_ABI_MISMATCH && runtime == nullptr);
    kn_runtime_destroy(nullptr);
    for (int i = 0; i < 10000; ++i) {
        CHECK(kn_runtime_create(1, &runtime) == KN_OK && runtime != nullptr);
        kn_runtime_info info{};
        CHECK(kn_runtime_get_info(runtime, &info, sizeof(info)) == KN_OK);
        CHECK(info.abi_version == 1 && info.capabilities == 16383 && info.ticks_per_second == KN_TICKS_PER_SECOND);
        info.abi_version = 42;
        CHECK(kn_runtime_get_info(runtime, &info, 1) == KN_INVALID_ARGUMENT && info.abi_version == 42);
        CHECK(kn_runtime_get_info(nullptr, &info, sizeof(info)) == KN_INVALID_ARGUMENT && info.abi_version == 0);
        kn_media_value value{INT64_C(9007199254740993), 35280000, 1080, 1920, 30000, 1001}, output{};
        CHECK(kn_value_roundtrip(runtime, &value, sizeof(value), &output, sizeof(output)) == KN_OK);
        CHECK(std::memcmp(&value, &output, sizeof(value)) == 0);
        CHECK(kn_value_roundtrip(runtime, &value, sizeof(value), &value, sizeof(value)) == KN_OK);
        CHECK(kn_value_roundtrip(runtime, nullptr, sizeof(value), &output, sizeof(output)) == KN_INVALID_ARGUMENT && output.duration_ticks == 0);
        kn_runtime_destroy(runtime);
    }
    CHECK(kn_runtime_create(1, &runtime) == KN_OK);
    int32_t valid = -1;
    CHECK(kn_frame_rate_is_valid(runtime, 30000, 1001, &valid) == KN_OK && valid == 1);
    CHECK(kn_frame_rate_is_valid(runtime, 60, 2, &valid) == KN_OK && valid == 0);
    CHECK(kn_frame_rate_is_valid(nullptr, 30, 1, &valid) == KN_INVALID_ARGUMENT && valid == 0);
    CHECK(kn_frame_rate_is_valid(runtime, 30, 1, nullptr) == KN_INVALID_ARGUMENT);
    int64_t ticks = -1;
    CHECK(kn_ticks_to_frame(runtime, 588000, 30, 1, &ticks) == KN_OK && ticks == 1);
    CHECK(kn_ticks_to_frame(runtime, -1, 30, 1, &ticks) == KN_INVALID_ARGUMENT && ticks == 0);
    CHECK(kn_ticks_to_frame(runtime, 1, 60, 2, &ticks) == KN_INVALID_ARGUMENT);
    CHECK(kn_ticks_to_frame(nullptr, 1, 30, 1, &ticks) == KN_INVALID_ARGUMENT);
    CHECK(kn_ticks_to_frame(runtime, 1, 30, 1, nullptr) == KN_INVALID_ARGUMENT);
    CHECK(kn_frame_to_ticks(runtime, 30000, 30000, 1001, &ticks) == KN_OK && ticks == INT64_C(35315280000));
    CHECK(kn_frame_to_ticks(runtime, INT64_MAX, 1, 1, &ticks) == KN_OVERFLOW && ticks == 0);
    CHECK(kn_frame_to_ticks(runtime, 1, 60, 2, &ticks) == KN_INVALID_ARGUMENT && ticks == 0);
    CHECK(kn_frame_to_ticks(runtime, -1, 30, 1, &ticks) == KN_INVALID_ARGUMENT);
    CHECK(kn_frame_to_ticks(nullptr, 1, 30, 1, &ticks) == KN_INVALID_ARGUMENT);
    CHECK(kn_frame_to_ticks(runtime, 1, 30, 1, nullptr) == KN_INVALID_ARGUMENT);
    CHECK(kn_frame_count(runtime, 1, 30, 1, &ticks) == KN_OK && ticks == 1);
    CHECK(kn_frame_count(runtime, 35280000, 30, 1, &ticks) == KN_OK && ticks == 30);
    CHECK(kn_frame_count(runtime, 0, 30, 1, &ticks) == KN_OK && ticks == 0);
    CHECK(kn_sample_to_ticks(runtime, 48000, 48000, &ticks) == KN_OK && ticks == 35280000);
    CHECK(kn_sample_to_ticks(runtime, 1, 70560000, &ticks) == KN_OK && ticks == 1);
    CHECK(kn_sample_count(runtime, INT64_MAX, INT32_MAX, &ticks) == KN_OVERFLOW && ticks == 0);
    CHECK(kn_sample_count(runtime, INT64_MAX, 35280000, &ticks) == KN_OK && ticks == INT64_MAX);
    CHECK(kn_sample_count(runtime, 1, 0, &ticks) == KN_INVALID_ARGUMENT && ticks == 0);
    kn_runtime_destroy(runtime);
    std::cout << "Native C ABI/time/lifetime contracts: PASS\n";
}
