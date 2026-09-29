#include "kachinco_native.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <memory>
#include <tuple>
#include <vector>
static_assert(sizeof(kn_eval_item) == 128 && sizeof(kn_eval_result) == 96);
struct kn_timeline { int64_t duration; std::vector<kn_eval_item> items; };
int32_t KN_CALL kn_parameter_at(const kn_parameter_point* points, uint32_t count, int64_t tick, double fallback, double* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = 0;
    if ((!points && count) || tick < 0 || !std::isfinite(fallback)) return KN_INVALID_ARGUMENT;
    for (uint32_t i=0; i<count; ++i)
        if (points[i].tick < 0 || !std::isfinite(points[i].value) || (i && points[i-1].tick >= points[i].tick)) return KN_INVALID_ARGUMENT;
    if (!count) { *output=fallback; return KN_OK; }
    if (tick <= points[0].tick) { *output=points[0].value; return KN_OK; }
    for (uint32_t i=1; i<count; ++i) if (tick < points[i].tick) {
        const auto& a=points[i-1]; const auto& b=points[i];
        const double fraction=static_cast<double>(tick-a.tick)/static_cast<double>(b.tick-a.tick);
        *output=a.value*(1-fraction)+b.value*fraction;
        return std::isfinite(*output) ? KN_OK : KN_OVERFLOW;
    }
    *output=points[count-1].value; return KN_OK;
}
int32_t KN_CALL kn_timeline_create(int64_t duration, const kn_eval_item* items, uint32_t count, kn_timeline** output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output=nullptr;
    if (duration <= 0 || (!items && count)) return KN_INVALID_ARGUMENT;
    try {
        auto timeline=std::make_unique<kn_timeline>(); timeline->duration=duration;
        for (uint32_t i=0; i<count; ++i) {
            const auto& c=items[i]; const auto& a=c.appearance;
            if (c.start < 0 || c.duration <= 0 || c.start > duration || c.duration > duration-c.start || c.source < 0 ||
                c.duration > std::numeric_limits<int64_t>::max()-c.source || c.track < 0 || c.index < 0 || c.kind < 0 || c.kind > 2 ||
                !std::isfinite(c.gain) || c.gain < 0 || c.gain > 16 || !std::isfinite(a.opacity) || a.opacity < 0 || a.opacity > 1 ||
                !std::isfinite(a.x) || !std::isfinite(a.y) || !std::isfinite(a.scale_x) || !std::isfinite(a.scale_y) ||
                a.scale_x <= 0 || a.scale_y <= 0 || !std::isfinite(a.rotation) || a.blend < 0 || a.blend > 1) return KN_INVALID_ARGUMENT;
        }
        if (count) timeline->items.assign(items,items+count);
        std::stable_sort(timeline->items.begin(),timeline->items.end(),[](const auto& a,const auto& b) {
            return std::tie(a.track,a.start,a.id_high,a.id_low) < std::tie(b.track,b.start,b.id_high,b.id_low);
        });
        *output=timeline.release(); return KN_OK;
    } catch (const std::bad_alloc&) { return KN_OUT_OF_MEMORY; }
      catch (...) { return KN_INTERNAL_ERROR; }
}
void KN_CALL kn_timeline_destroy(kn_timeline* timeline) noexcept { delete timeline; }
int32_t KN_CALL kn_timeline_evaluate(const kn_timeline* timeline, int64_t tick, int64_t duration, kn_eval_result* output, uint32_t capacity, uint32_t* count) noexcept {
    if (!count) return KN_INVALID_ARGUMENT;
    *count=0;
    if (!timeline || tick < 0 || tick >= timeline->duration || duration < 0 || duration > timeline->duration-tick ||
        (!output && capacity) || capacity < timeline->items.size()) return KN_INVALID_ARGUMENT;
    for (const auto& c: timeline->items) {
        if (!c.enabled || !c.track_enabled || (c.kind == 1 && (c.muted || c.gain <= 0))) continue;
        int64_t start=tick, length=0;
        if (duration) {
            if (c.kind != 1) continue;
            start=std::max(tick,c.start); const auto end=std::min(tick+duration,c.start+c.duration);
            if (start >= end) continue;
            length=end-start;
        } else if (tick < c.start || tick-c.start >= c.duration) continue;
        const auto local=start-c.start;
        kn_eval_result value{c.index,c.kind,start,c.source+local,length,c.appearance,c.gain};
        // Constant today; the same explicit clip-local boundary accepts future curves.
        kn_parameter_at(nullptr,0,local,c.gain,&value.gain);
        kn_parameter_at(nullptr,0,local,c.appearance.opacity,&value.appearance.opacity);
        output[(*count)++]=value;
    }
    return KN_OK;
}
