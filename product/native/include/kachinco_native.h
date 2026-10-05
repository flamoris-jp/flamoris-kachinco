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
#define KN_CAP_PROCESS UINT64_C(4)
#define KN_CAP_CACHE UINT64_C(8)
#define KN_CAP_DECODED_MEDIA UINT64_C(16)
#define KN_CAP_TIMELINE UINT64_C(32)
#define KN_CAP_COMPOSITION UINT64_C(64)
#define KN_CAP_PLAYBACK UINT64_C(128)
#define KN_CAP_EDITOR UINT64_C(256)
#define KN_CAP_PROJECT_CODEC UINT64_C(512)
#define KN_CAP_TIME_QUERIES UINT64_C(1024)
#define KN_CAP_PREVIEW_PRESENTATION UINT64_C(2048)
#define KN_CAP_VOLUME_AUTOMATION UINT64_C(4096)
#define KN_CAP_VISUAL_AUTOMATION UINT64_C(8192)
#define KN_TICKS_PER_SECOND INT64_C(35280000)
#define KN_OK INT32_C(0)
#define KN_INVALID_ARGUMENT INT32_C(1)
#define KN_ABI_MISMATCH INT32_C(2)
#define KN_OVERFLOW INT32_C(3)
#define KN_OUT_OF_MEMORY INT32_C(4)
#define KN_INTERNAL_ERROR INT32_C(5)
#define KN_IO_ERROR INT32_C(6)
#define KN_CANCELLED INT32_C(7)
#define KN_TIMEOUT INT32_C(8)
#define KN_END_OF_STREAM INT32_C(9)
#define KN_INVALID_MEDIA INT32_C(10)
#define KN_GPU_UNSUPPORTED INT32_C(11)
#define KN_GPU_FAILURE INT32_C(12)
#define KN_GPU_BUDGET INT32_C(13)

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
KN_API int32_t KN_CALL kn_frame_rate_is_valid(const kn_runtime* runtime, int32_t numerator, int32_t denominator, int32_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_frame_to_ticks(const kn_runtime* runtime, int64_t index, int32_t numerator, int32_t denominator, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_ticks_to_frame(const kn_runtime* runtime, int64_t tick, int32_t numerator, int32_t denominator, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_frame_count(const kn_runtime* runtime, int64_t duration, int32_t numerator, int32_t denominator, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_sample_to_ticks(const kn_runtime* runtime, int64_t index, int32_t sample_rate, int64_t* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_sample_count(const kn_runtime* runtime, int64_t duration, int32_t sample_rate, int64_t* output) KN_NOEXCEPT;
/* Process arguments are borrowed NUL-terminated UTF-8 for the duration of start.
   Reads never retain buffers. Dispose must cancel before racing active readers. */
typedef struct kn_process kn_process;
KN_API int32_t KN_CALL kn_process_start(const char* executable, const char* const* arguments, uint32_t argument_count, kn_process** output, int32_t* os_error) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_process_read(kn_process* process, uint32_t channel, uint8_t* buffer, uint32_t capacity, uint32_t timeout_ms, uint32_t* bytes_read) KN_NOEXCEPT;
/* Worker-only blocking pipe read. Host deadline/cancellation must call process_cancel;
   terminating the owned writer unblocks the OS read. No callback or retained buffer. */
KN_API int32_t KN_CALL kn_process_read_wait(kn_process* process, uint32_t channel, uint8_t* buffer, uint32_t capacity, uint32_t* bytes_read) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_process_wait(kn_process* process, uint32_t timeout_ms, int32_t* exit_code) KN_NOEXCEPT;
KN_API void KN_CALL kn_process_cancel(kn_process* process) KN_NOEXCEPT;
KN_API void KN_CALL kn_process_destroy(kn_process* process) KN_NOEXCEPT;
typedef struct kn_cache kn_cache;
typedef struct kn_buffer kn_buffer;
typedef struct kn_editor_session kn_editor_session;
typedef struct kn_cache_statistics { int64_t bytes, entries, hits, misses, evictions; } kn_cache_statistics;
/* Cache byte budgets charge payload; each value may include at most 32 metadata bytes. */
KN_API int32_t KN_CALL kn_cache_create(int64_t byte_limit, int32_t entry_limit, kn_cache** output) KN_NOEXCEPT;
KN_API void KN_CALL kn_cache_destroy(kn_cache* cache) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_cache_put(kn_cache* cache, const char* key, const uint8_t* data, uint32_t size, int64_t charge) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_cache_get(kn_cache* cache, const char* key, kn_buffer** output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_cache_clear(kn_cache* cache) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_cache_stats(kn_cache* cache, kn_cache_statistics* output, uint32_t size) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_decode_rgba(const uint8_t* data, uint32_t size, int32_t width, int32_t height, kn_buffer** output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_decode_pcm(const uint8_t* data, uint32_t size, int32_t sample_count, int32_t channels, kn_buffer** output) KN_NOEXCEPT;
KN_API void KN_CALL kn_buffer_destroy(kn_buffer* buffer) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_buffer_size(const kn_buffer* buffer, uint32_t* size) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_buffer_copy(const kn_buffer* buffer, uint8_t* output, uint32_t capacity) KN_NOEXCEPT;

/* Straight-alpha encoded SDR, nearest pixel centers. Inputs borrowed only in call. */
typedef struct kn_appearance {
    double x, y, scale_x, scale_y, rotation, opacity;
    int32_t blend, reserved;
} kn_appearance;
typedef struct kn_rgba { double r, g, b, a; } kn_rgba;
KN_API int32_t KN_CALL kn_blend(kn_rgba backdrop, kn_rgba source, int32_t mode, double opacity, kn_rgba* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_composite_rows(uint8_t* output, const uint8_t* source, uint32_t size, int32_t width, int32_t height, const kn_appearance* appearance, int32_t first_row, int32_t row_count) KN_NOEXCEPT;
/* Presentation-only channel copy; input/output may alias. Straight alpha is preserved. */
KN_API int32_t KN_CALL kn_rgba_to_bgra(uint8_t* output, const uint8_t* source, uint32_t size) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_mix_add(double* mix, uint32_t mix_count, const float* source, uint32_t source_count, uint32_t offset, double gain) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_mix_finish(const double* mix, float* output, uint32_t count) KN_NOEXCEPT;

/* Optional preview adapter. Calls on one handle (including destroy) must be serialized.
   Begin clears opaque black; layers borrow canvas-sized RGBA8 buffers only during the call.
   Four reusable textures plus the constant buffer are charged against the <=256 MiB
   payload budget. Driver/device/shader bookkeeping is outside the reported payload.
   Hardware is the default; force_warp=1 is a test-only software D3D11 adapter.
   error receives an HRESULT on Windows or zero for validation/unsupported hosts. */
typedef struct kn_gpu_preview kn_gpu_preview;
KN_API int32_t KN_CALL kn_gpu_create(uint64_t budget_bytes, int32_t force_warp, kn_gpu_preview** output, int32_t* error) KN_NOEXCEPT;
KN_API void KN_CALL kn_gpu_destroy(kn_gpu_preview* preview) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_gpu_begin(kn_gpu_preview* preview, int32_t width, int32_t height, int32_t* error) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_gpu_composite(kn_gpu_preview* preview, const uint8_t* source, uint32_t size, const kn_appearance* appearance, int32_t* error) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_gpu_read(kn_gpu_preview* preview, uint8_t* output, uint32_t size, int32_t* error) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_gpu_reset(kn_gpu_preview* preview, int32_t* error) KN_NOEXCEPT;
KN_API uint64_t KN_CALL kn_gpu_allocated_bytes(const kn_gpu_preview* preview) KN_NOEXCEPT;
/* Borrowed thread-local diagnostic, valid until this thread's next GPU operation. */
KN_API const char* KN_CALL kn_gpu_diagnostic(void) KN_NOEXCEPT;
/* Borrowed compiled shader diagnostic, valid until preview destruction. */
KN_API const char* KN_CALL kn_gpu_shader_diagnostics(const kn_gpu_preview* preview) KN_NOEXCEPT;
/* Forced-WARP diagnostic only: recompile the same repository-owned shader without
   optimization to isolate compiler defects. Never changes hardware/default policy. */
KN_API int32_t KN_CALL kn_gpu_diagnostic_without_optimization(kn_gpu_preview* preview, int32_t* error) KN_NOEXCEPT;


/* Snapshot-only evaluation input; IDs are canonical UUID hex halves for ordinal sorting. */
typedef struct kn_eval_item {
    int64_t start, duration, source;
    uint64_t id_high, id_low;
    int32_t track, index, kind, enabled;
    kn_appearance appearance;
    double gain;
    int32_t muted, track_enabled;
} kn_eval_item;
typedef struct kn_eval_result {
    int32_t index, kind;
    int64_t timeline_start, source_start, duration;
    kn_appearance appearance;
    double gain;
} kn_eval_result;
typedef struct kn_timeline kn_timeline;
KN_API int32_t KN_CALL kn_timeline_create(int64_t duration, const kn_eval_item* items, uint32_t count, kn_timeline** output) KN_NOEXCEPT;
KN_API void KN_CALL kn_timeline_destroy(kn_timeline* timeline) KN_NOEXCEPT;
/* duration=0 evaluates one frame; duration>0 evaluates intersecting audio ranges. */
KN_API int32_t KN_CALL kn_timeline_evaluate(const kn_timeline* timeline, int64_t tick, int64_t duration, kn_eval_result* output, uint32_t capacity, uint32_t* count) KN_NOEXCEPT;
/* Future authoring seam: deterministic clip-local linear parameters; no persistence changes. */
typedef struct kn_parameter_point { int64_t tick; double value; } kn_parameter_point;
KN_API int32_t KN_CALL kn_parameter_at_signed(const kn_parameter_point*, uint32_t count, int64_t tick, double fallback, double* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_parameter_at(const kn_parameter_point* points, uint32_t count, int64_t tick, double fallback, double* output) KN_NOEXCEPT;
/* Immutable snapshot setup: bounded signed clip-relative volume multiplier curve. */
KN_API int32_t KN_CALL kn_timeline_set_property_curve(kn_timeline*, int32_t index, int32_t property, const kn_parameter_point*, uint32_t count) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_timeline_set_gain_curve(kn_timeline* timeline, int32_t index, const kn_parameter_point* points, uint32_t count) KN_NOEXCEPT;
/* Shared preview/export PCM: envelope sampled at the canonical timeline sample tick. */
KN_API int32_t KN_CALL kn_timeline_mix_audio(const kn_timeline* timeline, int32_t index, double* mix, uint32_t mix_count,
    const float* source, uint32_t source_count, uint32_t offset, int64_t first_sample, int32_t rate, int32_t channels) KN_NOEXCEPT;


typedef struct kn_playback kn_playback;
typedef struct kn_playback_ticket { int64_t generation, position, render_tick, start_sample, total_samples; } kn_playback_ticket;
typedef struct kn_playback_step { int64_t position, video_tick, dropped; int32_t ended, present; } kn_playback_step;
typedef struct kn_audio_step { int64_t first_sample; int32_t count, underrun, ready, resume; } kn_audio_step;
KN_API int32_t KN_CALL kn_playback_create(kn_playback** output) KN_NOEXCEPT;
KN_API void KN_CALL kn_playback_destroy(kn_playback* playback) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_playback_request(kn_playback* playback, int64_t duration, int32_t fps_numerator, int32_t fps_denominator, int64_t tick, int32_t play, kn_playback_ticket* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_playback_cancel(kn_playback* playback, int64_t* generation) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_playback_accept(const kn_playback* playback, int64_t generation) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_playback_clock(kn_playback* playback, int64_t generation, int64_t played_frames, int64_t* position) KN_NOEXCEPT;
/* ready_tick=-1 means no ready video. Decisions consume at most one request. */
KN_API int32_t KN_CALL kn_playback_video(kn_playback* playback, int64_t generation, int64_t played_frames, int32_t ready_count, int64_t ready_tick, kn_playback_step* output) KN_NOEXCEPT;
/* Presentation never reserves a decode request. Drop the head only when the next frame is due. */
KN_API int32_t KN_CALL kn_playback_present(kn_playback* playback, int64_t generation, int64_t played_frames, int64_t ready_tick, int64_t next_ready_tick, kn_playback_step* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_playback_audio(kn_playback* playback, int64_t generation, int64_t queued_frames, kn_audio_step* output) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_first_sample(int64_t tick, int32_t rate, int64_t* output) KN_NOEXCEPT;


typedef struct kn_decoder_candidate { int64_t id, used, start, last_request; int32_t consumed, eligible; } kn_decoder_candidate;
KN_API int32_t KN_CALL kn_decoder_select(const kn_decoder_candidate* candidates, uint32_t count, int32_t video, int64_t tick, int32_t sample_count, int64_t* selected, int64_t* oldest) KN_NOEXCEPT;

/* Typed editing/persistence wire: bounded UTF-8 request, owned response buffer. */
KN_API int32_t KN_CALL kn_editor_create(int32_t history_limit, kn_editor_session** output) KN_NOEXCEPT;
KN_API void KN_CALL kn_editor_destroy(kn_editor_session* session) KN_NOEXCEPT;
KN_API int32_t KN_CALL kn_editor_request(kn_editor_session* session, const uint8_t* data, uint32_t size, kn_buffer** output) KN_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#endif
