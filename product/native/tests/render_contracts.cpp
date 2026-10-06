#include "kachinco_native.h"
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <limits>
#define REQUIRE(x) do { if (!(x)) { std::fprintf(stderr,"Failed line %d: %s\n",__LINE__,#x); std::abort(); } } while (false)
int main() {
    kn_rgba result{};
    REQUIRE(kn_blend({.2,.4,.6,1},{0,0,0,1},1,1,&result)==KN_OK);
    REQUIRE(result.r > .199999 && result.r < .200001);
    kn_appearance a{0,0,1,1,0,1,0,0}; uint8_t out[8]={0,0,0,255,0,0,0,255},src[8]={255,0,0,255,0,255,0,255};
    REQUIRE(kn_composite_rows(out,src,8,2,1,&a,0,1)==KN_OK && out[0]==255 && out[5]==255);
    REQUIRE(kn_composite_rows(out,src,7,2,1,&a,0,1)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_composite_rows(out,src,8,2,1,&a,1,1)==KN_INVALID_ARGUMENT);
    a.scale_x=0; REQUIRE(kn_composite_rows(out,src,8,2,1,&a,0,1)==KN_INVALID_ARGUMENT); a.scale_x=1;
    double mix[2]={0,0}; float samples[2]={.75f,-.75f},pcm[2];
    REQUIRE(kn_mix_add(mix,2,samples,2,0,2)==KN_OK);
    REQUIRE(kn_mix_finish(mix,pcm,2)==KN_OK && pcm[0]==1 && pcm[1]==-1);
    REQUIRE(kn_mix_add(mix,2,samples,2,1,1)==KN_INVALID_ARGUMENT);
    kn_eval_item items[2]={{0,20,7,0,2,0,0,0,1,a,1,0,1},{0,20,3,0,1,0,1,0,1,a,1,0,1}};
    kn_timeline* timeline=nullptr; REQUIRE(kn_timeline_create(20,items,2,&timeline)==KN_OK);
    items[0].start=19; // Input is copied.
    kn_eval_result values[2]; uint32_t count=0;
    REQUIRE(kn_timeline_evaluate(timeline,19,0,values,2,&count)==KN_OK && count==2);
    REQUIRE(values[0].index==1 && values[0].source_start==22 && values[1].source_start==26);
    REQUIRE(kn_timeline_evaluate(timeline,20,0,values,2,&count)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_evaluate(timeline,0,0,values,1,&count)==KN_INVALID_ARGUMENT);
    kn_parameter_point visual_curve[]={{-10,0},{10,20}};
    REQUIRE(kn_timeline_set_property_curve(timeline,0,0,visual_curve,2)==KN_OK);
    visual_curve[1].value=999; // Native snapshot owns a copy.
    REQUIRE(kn_timeline_evaluate(timeline,0,0,values,2,&count)==KN_OK && values[1].appearance.x==10);
    REQUIRE(kn_timeline_set_property_curve(timeline,0,6,visual_curve,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_set_property_curve(timeline,0,2,visual_curve,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_set_property_curve(timeline,0,5,visual_curve,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_set_property_curve(timeline,0,0,nullptr,4097)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_set_property_curve(timeline,0,0,nullptr,0)==KN_OK);
    REQUIRE(kn_timeline_evaluate(timeline,0,0,values,2,&count)==KN_OK && values[1].appearance.x==0);
    kn_timeline_destroy(timeline);
    kn_eval_item curve_item{0,KN_TICKS_PER_SECOND,0,0,1,0,0,1,1,a,2,0,1};
    REQUIRE(kn_timeline_create(KN_TICKS_PER_SECOND,&curve_item,1,&timeline)==KN_OK);
    kn_parameter_point curve[]={{0,0},{KN_TICKS_PER_SECOND,1}};
    REQUIRE(kn_timeline_set_gain_curve(timeline,0,curve,2)==KN_OK);
    curve[1].value=0; // Curves are copied; no borrowed mutable memory.
    REQUIRE(kn_timeline_evaluate(timeline,KN_TICKS_PER_SECOND/2,0,values,2,&count)==KN_OK && values[0].gain==1);
    double envelope_mix[4]={};float envelope_source[4]={1,1,1,1};
    REQUIRE(kn_timeline_mix_audio(timeline,0,envelope_mix,4,envelope_source,4,0,24000,48000,2)==KN_OK);
    REQUIRE(envelope_mix[0]==1 && envelope_mix[2]>1 && envelope_mix[2]<1.0001);
    REQUIRE(kn_timeline_mix_audio(timeline,0,envelope_mix,4,envelope_source,4,1,0,48000,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_mix_audio(timeline,0,nullptr,4,envelope_source,4,0,0,48000,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_mix_audio(timeline,0,envelope_mix,3,envelope_source,2,0,0,48000,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_mix_audio(timeline,0,envelope_mix,4,envelope_source,4,0,47999,48000,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_mix_audio(timeline,0,envelope_mix,4,envelope_source,4,0,INT64_MAX-1,48000,2)==KN_INVALID_ARGUMENT);
    envelope_source[3]=std::numeric_limits<float>::quiet_NaN();double untouched=envelope_mix[0];
    REQUIRE(kn_timeline_mix_audio(timeline,0,envelope_mix,4,envelope_source,4,0,0,48000,2)==KN_INVALID_MEDIA && envelope_mix[0]==untouched);
    curve[1].tick=0;REQUIRE(kn_timeline_set_gain_curve(timeline,0,curve,2)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_set_gain_curve(timeline,0,curve,4097)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_timeline_set_gain_curve(timeline,0,nullptr,2)==KN_INVALID_ARGUMENT);
    curve[0]={INT64_MIN,0};curve[1]={INT64_MAX,1};
    REQUIRE(kn_timeline_set_gain_curve(timeline,0,curve,2)==KN_OK);
    REQUIRE(kn_timeline_evaluate(timeline,0,0,values,2,&count)==KN_OK && values[0].gain==1);
    kn_timeline_destroy(timeline);
    kn_playback* playback=nullptr;REQUIRE(kn_playback_create(&playback)==KN_OK);
    kn_playback_ticket ticket;REQUIRE(kn_playback_request(playback,KN_TICKS_PER_SECOND,30,1,0,1,&ticket)==KN_OK);
    kn_audio_step audio; int64_t queued=0;
    for(int i=0;i<5;++i) { REQUIRE(kn_playback_audio(playback,ticket.generation,queued,&audio)==KN_OK && audio.count==4800);queued+=audio.count; }
    REQUIRE(kn_playback_audio(playback,ticket.generation,queued,&audio)==KN_OK && audio.count==0);
    kn_playback_step step;REQUIRE(kn_playback_video(playback,ticket.generation,48000,0,-1,&step)==KN_OK && step.ended);
    int64_t generation;REQUIRE(kn_playback_cancel(playback,&generation)==KN_OK);
    REQUIRE(kn_playback_accept(playback,ticket.generation)==KN_CANCELLED);
    REQUIRE(kn_playback_request(playback,1,30,1,1,1,&ticket)==KN_OK && ticket.start_sample==0);
    REQUIRE(kn_playback_request(playback,std::numeric_limits<int64_t>::max(),30000,1001,0,0,&ticket)==KN_OK);
    REQUIRE(kn_playback_request(playback,KN_TICKS_PER_SECOND,30,1,0,1,&ticket)==KN_OK);
    REQUIRE(kn_playback_present(playback,ticket.generation,0,1176000,2352000,&step)==KN_OK && !step.present && !step.dropped);
    REQUIRE(kn_playback_video(playback,ticket.generation,0,0,-1,&step)==KN_OK && step.video_tick==1176000);
    REQUIRE(kn_playback_present(playback,ticket.generation,3200,1176000,2352000,&step)==KN_OK && step.dropped==1 && !step.present);
    REQUIRE(kn_playback_present(playback,ticket.generation,3200,2352000,-1,&step)==KN_OK && step.present && !step.dropped);
    REQUIRE(kn_playback_present(playback,ticket.generation,3200,-1,2352000,&step)==KN_INVALID_ARGUMENT);
    kn_playback_destroy(playback);
    uint8_t rgba[8]={251,2,3,17,5,200,7,255}, bgra[8]={};
    REQUIRE(kn_rgba_to_bgra(bgra,rgba,8)==KN_OK && bgra[0]==3 && bgra[2]==251 && bgra[3]==17 && rgba[0]==251);
    REQUIRE(kn_rgba_to_bgra(rgba,rgba,8)==KN_OK && rgba[0]==3 && rgba[2]==251 && rgba[3]==17);
    REQUIRE(kn_rgba_to_bgra(bgra,rgba,7)==KN_INVALID_ARGUMENT);
    REQUIRE(kn_rgba_to_bgra(nullptr,rgba,8)==KN_INVALID_ARGUMENT);
    kn_decoder_candidate candidates[3]={{1,1,0,0,0,1},{2,2,0,10,0,1},{3,0,0,0,0,0}};
    int64_t selected,oldest;REQUIRE(kn_decoder_select(candidates,3,1,20,0,&selected,&oldest)==KN_OK && selected==2 && oldest==3);
    REQUIRE(kn_decoder_select(candidates,3,0,0,4800,&selected,&oldest)==KN_OK && selected==2);
    kn_decoder_candidate continuous={4,4,0,10*KN_TICKS_PER_SECOND,0,1};
    REQUIRE(kn_decoder_select(&continuous,1,1,11*KN_TICKS_PER_SECOND,0,&selected,&oldest)==KN_OK && selected==4);
    REQUIRE(kn_decoder_select(&continuous,1,1,12*KN_TICKS_PER_SECOND,0,&selected,&oldest)==KN_OK && selected==0);
    REQUIRE(kn_decoder_select(&continuous,1,1,9*KN_TICKS_PER_SECOND,0,&selected,&oldest)==KN_OK && selected==0);
    std::puts("Native raster, PCM, snapshot and playback policy contracts passed.");
}
