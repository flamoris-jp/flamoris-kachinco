#include "kachinco_native.h"
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
extern "C" int kn_c_header_check(void);
#define CHECK(x) do { if (!(x)) { std::cerr << "Failed at line " << __LINE__ << ": " << #x << '\n'; std::exit(1); } } while (false)
int main() {
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
        CHECK(info.abi_version == 1 && info.capabilities == 3 && info.ticks_per_second == KN_TICKS_PER_SECOND);
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
    int64_t ticks = -1;
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
