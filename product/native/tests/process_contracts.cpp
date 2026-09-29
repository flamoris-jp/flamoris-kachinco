#include "kachinco_native.h"
#include <array>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <string>
#include <thread>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif
#define CHECK(x) do { if (!(x)) { std::cerr << "Failed at line " << __LINE__ << ": " << #x << '\n'; std::exit(1); } } while (false)
std::string drain(kn_process* process, uint32_t channel) {
    std::string output;
    std::array<uint8_t, 8192> bytes{};
    for (;;) {
        uint32_t count = 0;
        CHECK(kn_process_read(process, channel, bytes.data(), static_cast<uint32_t>(bytes.size()), 10000, &count) == KN_OK);
        if (!count) return output;
        output.append(reinterpret_cast<char*>(bytes.data()), count);
        CHECK(output.size() <= 2 * 1024 * 1024);
    }
}
int main(int argc, char** argv) {
    if (argc > 1) {
        std::string mode = argv[1];
        if (mode == "--echo") { std::cout << argv[2]; std::cerr << "stderr"; return 37; }
        if (mode == "--fill") { std::cout << std::string(1024 * 1024, 'o'); std::cerr << std::string(1024 * 1024, 'e'); return 0; }
        if (mode == "--wait") { std::this_thread::sleep_for(std::chrono::seconds(60)); return 0; }
        return 2;
    }
    kn_process* process = nullptr; int32_t os_error = 0;
    CHECK(kn_process_start("kachinco-impossible-executable-31033", nullptr, 0, &process, &os_error) == KN_IO_ERROR);
    CHECK(process == nullptr && os_error != 0);
#ifdef _WIN32
    DWORD initial_handles = 0, warm_handles = 0; CHECK(GetProcessHandleCount(GetCurrentProcess(), &initial_handles));
#endif
    for (int i = 0; i < 32; ++i) {
        const char* arguments[] = {"--echo", "spaces \"quote\" \\ trailing\\"};
        CHECK(kn_process_start(argv[0], arguments, 2, &process, &os_error) == KN_OK);
        std::string out, err;
        std::thread reader([&] { err = drain(process, 1); });
        out = drain(process, 0); reader.join();
        int32_t exit = 0; CHECK(kn_process_wait(process, 10000, &exit) == KN_OK && exit == 37);
        CHECK(out == arguments[1] && err == "stderr");
        CHECK(kn_process_wait(process, 0, &exit) == KN_OK && exit == 37);
        kn_process_destroy(process);
#ifdef _WIN32
        DWORD count = 0; CHECK(GetProcessHandleCount(GetCurrentProcess(), &count));
        // MSVC/Windows initializes five process-wide handles on the first threaded
        // read. Compare every subsequent cycle with that warmed baseline, not zero-use CRT.
        if (i == 0) warm_handles = count;
        else CHECK(count <= warm_handles);
        if (i == 0 || i == 4 || i == 31)
            std::cout << "Handle count after iteration " << i << ": " << count << std::endl;
#endif
    }
    const char* filling[] = {"--fill"};
    CHECK(kn_process_start(argv[0], filling, 1, &process, &os_error) == KN_OK);
    std::string out, err;
    std::thread reader([&] { err = drain(process, 1); });
    out = drain(process, 0); reader.join();
    CHECK(out == std::string(1024 * 1024, 'o') && err == std::string(1024 * 1024, 'e'));
    kn_process_destroy(process);
    const char* waiting[] = {"--wait"};
    CHECK(kn_process_start(argv[0], waiting, 1, &process, &os_error) == KN_OK);
    int32_t exit = 0; CHECK(kn_process_wait(process, 0, &exit) == KN_TIMEOUT);
    auto at = std::chrono::steady_clock::now();
    int32_t read_result = KN_OK;
    std::thread blocked([&] { uint8_t byte; uint32_t count; read_result = kn_process_read(process, 0, &byte, 1, 60000, &count); });
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    kn_process_cancel(process); blocked.join();
    CHECK(read_result == KN_CANCELLED);
    kn_process_destroy(process); kn_process_destroy(nullptr);
    CHECK(std::chrono::steady_clock::now() - at < std::chrono::seconds(5));
#ifdef _WIN32
    DWORD final_handles = 0; CHECK(GetProcessHandleCount(GetCurrentProcess(), &final_handles));
    std::cout << "Initial handles: " << initial_handles << ", final handles: " << final_handles << std::endl;
    CHECK(final_handles <= warm_handles);
#endif
    std::cout << "Native process quoting/streams/exit/cancellation/lifetime: PASS\n";
}
