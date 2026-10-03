#include "kachinco_native.h"
#include "time_math.hpp"
#include <algorithm>
#include <cmath>
#include <limits>
#include <memory>
#include <map>
#include <tuple>
#include <vector>
static_assert(sizeof(kn_eval_item) == 128 && sizeof(kn_eval_result) == 96);
struct kn_timeline { int64_t duration; std::vector<kn_eval_item> items; std::map<int32_t,std::vector<kn_parameter_point>> curves; };
static double gain_at(const kn_timeline* timeline,const kn_eval_item& item,int64_t local) noexcept {
    const auto found=timeline->curves.find(item.index);
    if(found==timeline->curves.end()||found->second.empty()) return item.gain;
    const auto& points=found->second;
    auto after=std::upper_bound(points.begin(),points.end(),local,[](int64_t tick,const auto& p){return tick<p.tick;});
    if(after==points.begin()) return item.gain*points.front().value;
    if(after==points.end()) return item.gain*points.back().value;
    const auto& a=*(after-1);const auto& b=*after;
    // Ordered subtraction in unsigned space avoids signed overflow at Int64 extrema.
    const auto elapsed=static_cast<uint64_t>(local)-static_cast<uint64_t>(a.tick);
    const auto span=static_cast<uint64_t>(b.tick)-static_cast<uint64_t>(a.tick);
    const double ratio=static_cast<double>(elapsed)/static_cast<double>(span);
    return item.gain*(a.value*(1-ratio)+b.value*ratio);
}
int32_t KN_CALL kn_timeline_set_gain_curve(kn_timeline* timeline,int32_t index,const kn_parameter_point* points,uint32_t count) noexcept {
    if(!timeline||(!points&&count)||count>4096) return KN_INVALID_ARGUMENT;
    auto item=std::find_if(timeline->items.begin(),timeline->items.end(),[&](const auto& v){return v.index==index;});
    if(item==timeline->items.end()||item->kind!=1) return KN_INVALID_ARGUMENT;
    for(uint32_t i=0;i<count;++i) if(!std::isfinite(points[i].value)||points[i].value<0||points[i].value>16||(i&&points[i-1].tick>=points[i].tick)) return KN_INVALID_ARGUMENT;
    try {if(!count) timeline->curves.erase(index);else timeline->curves[index]=std::vector<kn_parameter_point>(points,points+count);return KN_OK;}
    catch(const std::bad_alloc&) {return KN_OUT_OF_MEMORY;}catch(...) {return KN_INTERNAL_ERROR;}
}
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
        value.gain=gain_at(timeline,c,local);
        kn_parameter_at(nullptr,0,local,c.appearance.opacity,&value.appearance.opacity);
        output[(*count)++]=value;
    }
    return KN_OK;
}
int32_t KN_CALL kn_timeline_mix_audio(const kn_timeline* timeline,int32_t index,double* mix,uint32_t mix_count,
    const float* source,uint32_t source_count,uint32_t offset,int64_t first_sample,int32_t rate,int32_t channels) noexcept {
    if(!timeline||(!mix&&mix_count)||(!source&&source_count)||mix_count>96000||offset>mix_count||source_count>mix_count-offset||
       first_sample<0||rate!=48000||channels!=2||source_count%2||offset%2||first_sample>INT64_MAX-source_count/2) return KN_INVALID_ARGUMENT;
    auto item=std::find_if(timeline->items.begin(),timeline->items.end(),[&](const auto& v){return v.index==index;});
    if(item==timeline->items.end()||item->kind!=1) return KN_INVALID_ARGUMENT;
    for(uint32_t i=0;i<source_count;++i) if(!std::isfinite(source[i])) return KN_INVALID_MEDIA;
    if(source_count) {
        int64_t first=0,last=0;
        static_assert(KN_TICKS_PER_SECOND%48000==0);
        constexpr auto step=KN_TICKS_PER_SECOND/48000;
        if(first_sample+source_count/2-1>INT64_MAX/step) return KN_OVERFLOW;
        first=first_sample*step;last=(first_sample+source_count/2-1)*step;
        if(first<item->start||last-item->start>=item->duration) return KN_INVALID_ARGUMENT;
    }
    if(!item->enabled||!item->track_enabled||item->muted||item->gain==0) return KN_OK;
    for(uint32_t frame=0;frame<source_count/2;++frame) {
        const int64_t tick=(first_sample+frame)*(KN_TICKS_PER_SECOND/48000);
        const auto gain=gain_at(timeline,*item,tick-item->start);
        mix[offset+2*frame]+=source[2*frame]*gain;mix[offset+2*frame+1]+=source[2*frame+1]*gain;
    }
    return KN_OK;
}
