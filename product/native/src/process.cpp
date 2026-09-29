#include "kachinco_native.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <stdexcept>
#include <thread>
#include <vector>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <cerrno>
#include <csignal>
#include <fcntl.h>
#include <poll.h>
#include <spawn.h>
#include <sys/wait.h>
#include <unistd.h>
extern char** environ;
#endif

namespace {
using clock_type = std::chrono::steady_clock;
bool expired(clock_type::time_point deadline) noexcept { return clock_type::now() >= deadline; }
#ifdef _WIN32
struct owned_handle {
    HANDLE value = nullptr;
    owned_handle() = default;
    ~owned_handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    owned_handle(const owned_handle&) = delete;
    owned_handle& operator=(const owned_handle&) = delete;
};
std::wstring utf16(const char* value) {
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, nullptr, 0);
    if (!count) throw std::invalid_argument("UTF-8");
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, result.data(), count)) throw std::invalid_argument("UTF-8");
    result.pop_back(); return result;
}
std::wstring quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t ch : value) {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(slashes * (ch == L'"' ? 2U : 1U), L'\\'); slashes = 0;
        if (ch == L'"') result += L'\\';
        result += ch;
    }
    result.append(slashes * 2, L'\\'); result += L'"'; return result;
}
#else
struct owned_fd {
    int value = -1;
    owned_fd() = default;
    ~owned_fd() { if (value >= 0) close(value); }
    owned_fd(const owned_fd&) = delete;
    owned_fd& operator=(const owned_fd&) = delete;
};
#endif
}

struct kn_process {
    std::atomic<bool> cancelled{false};
    std::array<std::mutex, 2> readers;
    std::mutex state;
    bool exit_observed = false;
    int32_t exit_code = 0;
#ifdef _WIN32
    owned_handle child, job;
    std::array<owned_handle, 2> pipes;
#else
    pid_t pid = -1;
    std::array<owned_fd, 2> pipes;
#endif
    ~kn_process() {
        kn_process_cancel(this);
#ifdef _WIN32
        if (child.value) WaitForSingleObject(child.value, INFINITE);
#else
        if (pid > 0) { int status; while (waitpid(pid, &status, 0) < 0 && errno == EINTR) { } }
#endif
    }
};

int32_t KN_CALL kn_process_start(const char* executable, const char* const* arguments, uint32_t argument_count, kn_process** output, int32_t* os_error) noexcept {
    if (!output || !os_error) return KN_INVALID_ARGUMENT;
    *output = nullptr; *os_error = 0;
    if (!executable || !*executable || argument_count > 256 || (argument_count && !arguments)) return KN_INVALID_ARGUMENT;
    try {
        size_t total = std::strlen(executable);
        for (uint32_t i = 0; i < argument_count; ++i) {
            if (!arguments[i]) return KN_INVALID_ARGUMENT;
            total += std::strlen(arguments[i]);
            if (total > 1024 * 1024) return KN_INVALID_ARGUMENT;
        }
        auto process = std::make_unique<kn_process>();
#ifdef _WIN32
        std::wstring command = quote(utf16(executable));
        for (uint32_t i = 0; i < argument_count; ++i) { command += L' '; command += quote(utf16(arguments[i])); }
        if (command.size() >= 32767) return KN_INVALID_ARGUMENT;
        SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
        std::array<owned_handle, 2> writers;
        for (size_t i = 0; i < 2; ++i) {
            if (!CreatePipe(&process->pipes[i].value, &writers[i].value, &security, 64 * 1024) ||
                !SetHandleInformation(process->pipes[i].value, HANDLE_FLAG_INHERIT, 0)) {
                *os_error = static_cast<int32_t>(GetLastError()); return KN_IO_ERROR;
            }
        }
        owned_handle input;
        input.value = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr);
        if (input.value == INVALID_HANDLE_VALUE) { *os_error = static_cast<int32_t>(GetLastError()); return KN_IO_ERROR; }
        process->job.value = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{}; limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!process->job.value || !SetInformationJobObject(process->job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
            *os_error = static_cast<int32_t>(GetLastError()); return KN_IO_ERROR;
        }
        SIZE_T attribute_size = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attribute_size);
        std::vector<uint8_t> storage(attribute_size);
        auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
        if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attribute_size)) { *os_error = static_cast<int32_t>(GetLastError()); return KN_IO_ERROR; }
        struct attribute_guard { LPPROC_THREAD_ATTRIBUTE_LIST value; ~attribute_guard() { DeleteProcThreadAttributeList(value); } } guard{attributes};
        HANDLE inherited[] = {input.value, writers[0].value, writers[1].value};
        if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof(inherited), nullptr, nullptr)) {
            *os_error = static_cast<int32_t>(GetLastError()); return KN_IO_ERROR;
        }
        STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = attributes;
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = input.value; startup.StartupInfo.hStdOutput = writers[0].value; startup.StartupInfo.hStdError = writers[1].value;
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT,
                nullptr, nullptr, &startup.StartupInfo, &child)) { *os_error = static_cast<int32_t>(GetLastError()); return KN_IO_ERROR; }
        process->child.value = child.hProcess;
        owned_handle thread; thread.value = child.hThread;
        if (!AssignProcessToJobObject(process->job.value, child.hProcess) || ResumeThread(child.hThread) == static_cast<DWORD>(-1)) {
            *os_error = static_cast<int32_t>(GetLastError()); TerminateProcess(child.hProcess, 1); return KN_IO_ERROR;
        }
#else
        std::array<owned_fd, 2> writers;
        for (size_t i = 0; i < 2; ++i) {
            int pair[2];
            if (pipe2(pair, O_CLOEXEC) != 0) { *os_error = errno; return KN_IO_ERROR; }
            process->pipes[i].value = pair[0]; writers[i].value = pair[1];
            if (fcntl(pair[0], F_SETFL, O_NONBLOCK) < 0) { *os_error = errno; return KN_IO_ERROR; }
        }
        posix_spawn_file_actions_t actions;
        posix_spawnattr_t attributes;
        int code = posix_spawn_file_actions_init(&actions);
        if (code) { *os_error = code; return KN_IO_ERROR; }
        struct action_guard { posix_spawn_file_actions_t* value; ~action_guard() { posix_spawn_file_actions_destroy(value); } } action_cleanup{&actions};
        code = posix_spawnattr_init(&attributes);
        if (code) { *os_error = code; return KN_IO_ERROR; }
        struct attr_guard { posix_spawnattr_t* value; ~attr_guard() { posix_spawnattr_destroy(value); } } attr_cleanup{&attributes};
        code = posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0);
        if (!code) code = posix_spawn_file_actions_adddup2(&actions, writers[0].value, STDOUT_FILENO);
        if (!code) code = posix_spawn_file_actions_adddup2(&actions, writers[1].value, STDERR_FILENO);
        // Linux Product test host uses glibc: close all unrelated inherited FDs.
        if (!code) code = posix_spawn_file_actions_addclosefrom_np(&actions, 3);
        if (!code) code = posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETPGROUP);
        if (!code) code = posix_spawnattr_setpgroup(&attributes, 0);
        if (code) { *os_error = code; return KN_IO_ERROR; }
        std::vector<char*> argv; argv.reserve(static_cast<size_t>(argument_count) + 2);
        argv.push_back(const_cast<char*>(executable));
        for (uint32_t i = 0; i < argument_count; ++i) argv.push_back(const_cast<char*>(arguments[i]));
        argv.push_back(nullptr);
        pid_t pid = -1;
        code = posix_spawnp(&pid, executable, &actions, &attributes, argv.data(), environ);
        if (code) { *os_error = code; return KN_IO_ERROR; }
        process->pid = pid;
#endif
        *output = process.release(); return KN_OK;
    } catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
      catch (const std::invalid_argument&) { return KN_INVALID_ARGUMENT; }
      catch (...) { return KN_INTERNAL_ERROR; }
}

void KN_CALL kn_process_cancel(kn_process* process) noexcept {
    if (!process) return;
    try {
        std::lock_guard<std::mutex> lock(process->state);
        if (process->cancelled.exchange(true)) return;
#ifdef _WIN32
        if (process->job.value) TerminateJobObject(process->job.value, 1);
#else
        if (process->pid > 0) kill(-process->pid, SIGKILL);
#endif
    } catch (...) { /* No exception may cross a cancellation callback. */ }
}
void KN_CALL kn_process_destroy(kn_process* process) noexcept { delete process; }

int32_t KN_CALL kn_process_read(kn_process* process, uint32_t channel, uint8_t* buffer, uint32_t capacity, uint32_t timeout_ms, uint32_t* bytes_read) noexcept {
    if (!bytes_read) return KN_INVALID_ARGUMENT;
    *bytes_read = 0;
    if (!process || channel > 1 || !buffer || !capacity || capacity > 64 * 1024 * 1024 || timeout_ms > 60000) return KN_INVALID_ARGUMENT;
    try {
        std::lock_guard<std::mutex> lock(process->readers[channel]);
        const auto deadline = clock_type::now() + std::chrono::milliseconds(timeout_ms);
        while (!process->cancelled.load()) {
#ifdef _WIN32
            DWORD available = 0;
            if (!PeekNamedPipe(process->pipes[channel].value, nullptr, 0, nullptr, &available, nullptr))
                return GetLastError() == ERROR_BROKEN_PIPE ? KN_OK : KN_IO_ERROR;
            if (available) {
                DWORD count = 0;
                if (!ReadFile(process->pipes[channel].value, buffer, std::min<DWORD>(available, capacity), &count, nullptr))
                    return GetLastError() == ERROR_BROKEN_PIPE ? KN_OK : KN_IO_ERROR;
                *bytes_read = count; return KN_OK;
            }
#else
            const ssize_t count = read(process->pipes[channel].value, buffer, capacity);
            if (count >= 0) { *bytes_read = static_cast<uint32_t>(count); return KN_OK; }
            if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) return KN_IO_ERROR;
#endif
            if (expired(deadline)) return KN_TIMEOUT;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        return KN_CANCELLED;
    } catch (...) { return KN_INTERNAL_ERROR; }
}
int32_t KN_CALL kn_process_wait(kn_process* process, uint32_t timeout_ms, int32_t* exit_code) noexcept {
    if (!exit_code) return KN_INVALID_ARGUMENT;
    *exit_code = 0;
    if (!process || timeout_ms > 60000) return KN_INVALID_ARGUMENT;
    try {
        const auto deadline = clock_type::now() + std::chrono::milliseconds(timeout_ms);
        for (;;) {
            {
                std::lock_guard<std::mutex> lock(process->state);
                if (!process->exit_observed) {
#ifdef _WIN32
                    DWORD wait = WaitForSingleObject(process->child.value, 0);
                    if (wait == WAIT_FAILED) return KN_IO_ERROR;
                    if (wait == WAIT_OBJECT_0) {
                        DWORD code;
                        if (!GetExitCodeProcess(process->child.value, &code)) return KN_IO_ERROR;
                        process->exit_code = static_cast<int32_t>(code); process->exit_observed = true;
                    }
#else
                    // Observe without reaping: reserve the group leader PID until destroy,
                    // so cancellation cannot target a recycled PID/process group.
                    siginfo_t info{};
                    const int result = waitid(P_PID, static_cast<id_t>(process->pid), &info, WEXITED | WNOHANG | WNOWAIT);
                    if (result < 0 && errno != EINTR) return KN_IO_ERROR;
                    if (result == 0 && info.si_pid != 0) {
                        process->exit_code = info.si_code == CLD_EXITED ? info.si_status : 128 + info.si_status;
                        process->exit_observed = true;
                    }
#endif
                }
                if (process->exit_observed) { *exit_code = process->exit_code; return KN_OK; }
            }
            if (process->cancelled.load()) return KN_CANCELLED;
            if (expired(deadline)) return KN_TIMEOUT;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    } catch (...) { return KN_INTERNAL_ERROR; }
}
