#include "kachinco_native.h"
#include "time_math.hpp"
#include <algorithm>
#include <limits>
#include <new>
// Host serializes commands/decisions. The ABI never calls the audio device or a UI callback.
struct kn_playback {
    int64_t generation=0, duration=0, position=0, start=0, total=0, next_audio=0, next_video=0, requested_tick=0;
    int32_t numerator=30, denominator=1;
    bool play=false;
};
namespace {
int32_t frame_index(int64_t tick, int32_t n, int32_t d, int64_t* output) noexcept {
    return kn_time::divide(kn_time::multiply(static_cast<uint64_t>(tick),static_cast<uint32_t>(n)),
        static_cast<uint64_t>(KN_TICKS_PER_SECOND)*static_cast<uint32_t>(d),output);
}
}
int32_t KN_CALL kn_first_sample(int64_t tick, int32_t rate, int64_t* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output=0;
    if (tick < 0 || rate <= 0) return KN_INVALID_ARGUMENT;
    return kn_time::divide(kn_time::add(kn_time::multiply(static_cast<uint64_t>(tick),static_cast<uint32_t>(rate)),
        KN_TICKS_PER_SECOND-1),KN_TICKS_PER_SECOND,output);
}
int32_t KN_CALL kn_playback_create(kn_playback** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output=nullptr;
    try { *output=new kn_playback(); return KN_OK; }
    catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
    catch (...) { return KN_INTERNAL_ERROR; }
}
void KN_CALL kn_playback_destroy(kn_playback* playback) noexcept { delete playback; }
int32_t KN_CALL kn_playback_cancel(kn_playback* p, int64_t* generation) noexcept {
    if (!p || !generation) return KN_INVALID_ARGUMENT;
    if (p->generation == std::numeric_limits<int64_t>::max()) return KN_OVERFLOW;
    p->play=false; *generation=++p->generation; return KN_OK;
}
int32_t KN_CALL kn_playback_accept(const kn_playback* p, int64_t generation) noexcept {
    if (!p) return KN_INVALID_ARGUMENT;
    return p->generation == generation ? KN_OK : KN_CANCELLED;
}
int32_t KN_CALL kn_playback_request(kn_playback* p, int64_t duration, int32_t n, int32_t d, int64_t tick, int32_t play, kn_playback_ticket* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output={};
    if (!p || duration <= 0 || !kn_time::valid_fps(n,d)) return KN_INVALID_ARGUMENT;
    auto next=*p; int64_t generation=0;
    auto status=kn_playback_cancel(&next,&generation); if (status != KN_OK) return status;
    next.duration=duration; next.numerator=n; next.denominator=d;
    next.position=std::clamp(tick,INT64_C(0),duration);
    int64_t render=next.position;
    if (render == duration) {
        int64_t frames=0;
        status=kn_time::count(duration,static_cast<uint32_t>(n),static_cast<uint64_t>(KN_TICKS_PER_SECOND)*static_cast<uint32_t>(d),&frames);
        if (status != KN_OK) return status;
        status=kn_time::rounded(std::max(INT64_C(0),frames-1),static_cast<uint64_t>(KN_TICKS_PER_SECOND)*static_cast<uint32_t>(d),static_cast<uint32_t>(n),&render);
        if (status != KN_OK) return status;
    }
    next.requested_tick=render;
    status=kn_first_sample(render,48000,&next.start); if (status != KN_OK) return status;
    status=kn_time::count(duration,48000,KN_TICKS_PER_SECOND,&next.total); if (status != KN_OK) return status;
    status=frame_index(render,n,d,&next.next_video); if (status != KN_OK) return status;
    if (play && (next.position == duration || next.start >= next.total)) { next.position=0; render=0; next.requested_tick=0; next.start=0; next.next_video=0; }
    ++next.next_video; next.next_audio=next.start; next.play=play != 0;
    *p=next; *output={generation,next.position,render,next.start,next.total}; return KN_OK;
}
int32_t KN_CALL kn_playback_clock(kn_playback* p, int64_t generation, int64_t played, int64_t* position) noexcept {
    if (!p || !position || played < 0) return KN_INVALID_ARGUMENT;
    *position=p->position;
    if (p->generation != generation) return KN_CANCELLED;
    if (!p->play) return KN_OK;
    const auto sample=p->start+std::min(played,p->total-p->start);
    auto status=kn_time::rounded(sample,KN_TICKS_PER_SECOND,48000,position);
    if (status == KN_OVERFLOW) { *position=p->duration; status=KN_OK; }
    if (status == KN_OK) { *position=std::min(*position,p->duration); p->position=*position; }
    return status;
}
int32_t KN_CALL kn_playback_video(kn_playback* p, int64_t generation, int64_t played, int32_t ready_count, int64_t ready_tick, kn_playback_step* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output={0,-1,0,0,0};
    if (ready_count < 0 || ready_count > 3) return KN_INVALID_ARGUMENT;
    auto status=kn_playback_clock(p,generation,played,&output->position); if (status != KN_OK) return status;
    if (!p->play) return KN_OK;
    if (played >= p->total-p->start) { output->ended=1; output->position=p->duration; return KN_OK; }
    if (ready_count && ready_tick >= 0 && ready_tick <= output->position) { output->present=1; return KN_OK; }
    int64_t clock_frame=0;
    status=frame_index(output->position,p->numerator,p->denominator,&clock_frame); if (status != KN_OK) return status;
    if (p->next_video < clock_frame) { output->dropped=clock_frame-p->next_video; p->next_video=clock_frame; }
    if (ready_count >= 3 || p->next_video > clock_frame+3) return KN_OK;
    int64_t at=0;
    status=kn_time::rounded(p->next_video,static_cast<uint64_t>(KN_TICKS_PER_SECOND)*static_cast<uint32_t>(p->denominator),static_cast<uint32_t>(p->numerator),&at);
    if (status == KN_OVERFLOW) return KN_OK;
    if (status != KN_OK) return status;
    if (at < p->duration) { output->video_tick=std::max(p->requested_tick,at); ++p->next_video; }
    return KN_OK;
}
int32_t KN_CALL kn_playback_present(kn_playback* p, int64_t generation, int64_t played,
    int64_t ready_tick, int64_t next_ready_tick, kn_playback_step* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output={0,-1,0,0,0};
    if (ready_tick < -1 || next_ready_tick < -1 ||
        (next_ready_tick >= 0 && (ready_tick < 0 || next_ready_tick < ready_tick))) return KN_INVALID_ARGUMENT;
    auto status=kn_playback_clock(p,generation,played,&output->position); if (status != KN_OK) return status;
    if (!p->play) return KN_OK;
    if (played >= p->total-p->start) { output->ended=1; output->position=p->duration; return KN_OK; }
    if (next_ready_tick >= 0 && next_ready_tick <= output->position) output->dropped=1;
    else if (ready_tick >= 0 && ready_tick <= output->position) output->present=1;
    return KN_OK;
}
int32_t KN_CALL kn_playback_audio(kn_playback* p, int64_t generation, int64_t queued, kn_audio_step* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output={};
    if (!p || queued < 0 || queued > 24000) return KN_INVALID_ARGUMENT;
    if (p->generation != generation) return KN_CANCELLED;
    output->first_sample=p->next_audio;
    const bool ready=p->next_audio-p->start >= 9600 || p->next_audio == p->total;
    output->ready=ready ? 1 : 0;
    output->resume=p->play && (queued >= 9600 || p->next_audio == p->total) ? 1 : 0;
    output->underrun=ready && p->play && queued == 0 && p->next_audio < p->total ? 1 : 0;
    if (p->play && queued <= 24000-4800) {
        output->count=static_cast<int32_t>(std::min(INT64_C(4800),p->total-p->next_audio));
        p->next_audio+=output->count;
        output->ready=p->next_audio-p->start >= 9600 || p->next_audio == p->total ? 1 : 0;
        output->resume=p->play && (queued+output->count >= 9600 || p->next_audio == p->total) ? 1 : 0;
    }
    return KN_OK;
}

int32_t KN_CALL kn_decoder_select(const kn_decoder_candidate* candidates, uint32_t count, int32_t video, int64_t tick, int32_t samples, int64_t* selected, int64_t* oldest) noexcept {
    if (!selected || !oldest) return KN_INVALID_ARGUMENT;
    *selected=0; *oldest=0;
    if ((!candidates && count) || count > 8 || tick < 0 || (!video && (samples < 1 || samples > 48000))) return KN_INVALID_ARGUMENT;
    int64_t best_distance=std::numeric_limits<int64_t>::max(), best_used=-1, oldest_used=std::numeric_limits<int64_t>::max();
    for (uint32_t i=0;i<count;++i) {
        const auto& c=candidates[i];
        if (c.used < oldest_used) { oldest_used=c.used; *oldest=c.id; }
        if (!c.eligible || c.start < 0) continue;
        int64_t distance=0;
        if (video) {
            if (tick < c.last_request || tick < c.start) continue;
            // last_request=-1 is the unconsumed sentinel.
            distance=tick-(c.last_request < 0 ? c.start : c.last_request);
            // Continuous streams keep bounded seek/discard work per request;
            // their process lifetime is not a two-second decode window.
            if (distance >= 2*KN_TICKS_PER_SECOND) continue;
        } else {
            if (c.consumed < 0 || c.consumed > 96000-samples) continue;
            int64_t delta=0; kn_time::rounded(c.consumed,KN_TICKS_PER_SECOND,48000,&delta);
            if (tick < c.start || tick-c.start != delta) continue;
        }
        if (distance < best_distance || (distance == best_distance && c.used > best_used))
        { best_distance=distance; best_used=c.used; *selected=c.id; }
    }
    return KN_OK;
}
