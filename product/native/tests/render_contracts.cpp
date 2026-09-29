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
    kn_playback_destroy(playback);
    kn_decoder_candidate candidates[3]={{1,1,0,0,0,1},{2,2,0,10,0,1},{3,0,0,0,0,0}};
    int64_t selected,oldest;REQUIRE(kn_decoder_select(candidates,3,1,20,0,&selected,&oldest)==KN_OK && selected==2 && oldest==3);
    REQUIRE(kn_decoder_select(candidates,3,0,0,4800,&selected,&oldest)==KN_OK && selected==2);
    std::puts("Native raster, PCM, snapshot and playback policy contracts passed.");
}
