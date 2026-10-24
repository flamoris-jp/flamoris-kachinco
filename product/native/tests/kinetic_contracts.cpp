#include "kachinco_native.h"
#include "../third_party/nlohmann/json.hpp"
#include <vector>
#include <iostream>
#include <cmath>
#include <algorithm>
using json=nlohmann::ordered_json;
#define REQUIRE(x) do{if(!(x)){std::cerr<<__LINE__<<": "<<#x<<'\n';return 1;}}while(false)
static json request(kn_text_session* session,json value) {auto s=value.dump();kn_buffer* buffer=nullptr;if(kn_text_request(session,reinterpret_cast<const uint8_t*>(s.data()),static_cast<uint32_t>(s.size()),&buffer)!=KN_OK)throw std::runtime_error("request");uint32_t size=0;kn_buffer_size(buffer,&size);std::vector<uint8_t> data(size);kn_buffer_copy(buffer,data.data(),size);kn_buffer_destroy(buffer);return json::parse(data);}
int main(){
    kn_text_session* session=nullptr;REQUIRE(kn_text_create(&session)==KN_OK);
    auto first=request(session,{{"action","get"}});auto d=first.at("document");
    json clip={{"id","22222222-2222-4222-8222-222222222222"},{"text","日本語 A\xCC\x81 \xF0\x9F\x90\xB1 black"},{"startTicks","0"},{"durationTicks","176400000"},{"fontFamily","Yu Gothic"},{"fontSize",48},{"color",json::array({0,0,0,1})},{"strokeColor",json::array({1,1,1,1})},{"strokeWidth",1},{"shadowColor",json::array({0,0,0,0.5})},{"shadowX",3},{"shadowY",3},{"x",30},{"y",50},{"seed",123},{"source","shake(amplitude=12, frequency=7)"},{"provenance","contract"},{"program",{{"version",1},{"ops",json::array({{{"op","shake"},{"amplitude",12},{"frequency",7}}})}}}};
    d["clips"].push_back(clip);auto loaded=request(session,{{"action","replace"},{"expectedRevision",0},{"document",d}});REQUIRE(loaded.at("success")==true);
    auto changed=d;changed["clips"][0]["text"]="changed";
    REQUIRE(request(session,{{"action","edit"},{"expectedRevision",0},{"document",changed}}).at("success")==false);
    REQUIRE(request(session,{{"action","edit"},{"expectedRevision",1},{"document",changed}}).at("success")==true);
    REQUIRE(request(session,{{"action","undo"},{"expectedRevision",2}}).at("document")==d);
    REQUIRE(request(session,{{"action","redo"},{"expectedRevision",3}}).at("document")==changed);
    auto bad=d;bad["clips"][0]["program"]["version"]=2;REQUIRE(request(session,{{"action","edit"},{"expectedRevision",4},{"document",bad}}).at("success")==false);
    REQUIRE(request(session,{{"action","get"}}).at("document")==changed);
    auto source=d.dump();kn_text_motion a{},b{},other{};auto bytes=reinterpret_cast<const uint8_t*>(source.data());auto size=static_cast<uint32_t>(source.size());
    REQUIRE(kn_text_evaluate(bytes,size,12345678,1,&a)==KN_OK);REQUIRE(kn_text_evaluate(bytes,size,88888888,3,&other)==KN_OK);REQUIRE(kn_text_evaluate(bytes,size,12345678,1,&b)==KN_OK);
    REQUIRE(a.x==b.x&&a.y==b.y&&std::abs(a.x)<=12&&std::abs(a.y)<=12);REQUIRE(kn_text_evaluate(bytes,size,176400000,0,&b)==KN_OK&&b.opacity==0);
    uint8_t bgra[]={0,0,0,128,20,40,60,128,255,255,255,0},rgba[12]{},rgb[9]{},mask[9]{};REQUIRE(kn_text_convert(bgra,rgba,rgb,mask,3)==KN_OK);REQUIRE(rgba[0]==0&&rgba[3]==128&&mask[0]==128);REQUIRE(rgb[3]==60&&rgba[4]==120&&rgba[8]==0);
    uint8_t masked[12]{};uint8_t mrgb[]={0,0,0,255,60,40,20,255,0,0,0,255};uint8_t mrgba[]={128,128,128,255,128,128,128,255,0,0,0,255};REQUIRE(kn_text_apply_mask(mrgb,mrgba,masked,3)==KN_OK);REQUIRE(masked[3]==128&&masked[0]==0&&masked[4]==120&&masked[11]==0);
    kn_text_renderer* renderer=nullptr;int32_t error=0;
#ifdef _WIN32
    REQUIRE(kn_text_renderer_create(1,&renderer,&error)==KN_OK);std::vector<uint8_t> pixels(1280*720*4),again(pixels.size());
    REQUIRE(kn_text_render(renderer,bytes,size,12345678,1280,720,pixels.data(),static_cast<uint32_t>(pixels.size()),&error)==KN_OK);
    REQUIRE(std::any_of(pixels.begin(),pixels.end(),[](uint8_t n){return n!=0;}));
    REQUIRE(kn_text_render(renderer,bytes,size,0,1280,720,again.data(),static_cast<uint32_t>(again.size()),&error)==KN_OK);
    REQUIRE(kn_text_render(renderer,bytes,size,12345678,1280,720,again.data(),static_cast<uint32_t>(again.size()),&error)==KN_OK);REQUIRE(pixels==again);
    REQUIRE(kn_text_render(renderer,bytes,size,176400000,1280,720,again.data(),static_cast<uint32_t>(again.size()),&error)==KN_OK);REQUIRE(std::all_of(again.begin(),again.end(),[](uint8_t n){return n==0;}));
    auto translucent=d;translucent["clips"][0]["text"]="M";translucent["clips"][0]["program"]["ops"]=json::array({{{"op","opacity"},{"value",0.5}}});auto ts=translucent.dump();
    REQUIRE(kn_text_render(renderer,reinterpret_cast<const uint8_t*>(ts.data()),static_cast<uint32_t>(ts.size()),0,1280,720,again.data(),static_cast<uint32_t>(again.size()),&error)==KN_OK);
    uint8_t maximum_alpha=0;for(size_t i=3;i<again.size();i+=4)maximum_alpha=std::max(maximum_alpha,again[i]);REQUIRE(maximum_alpha>0&&maximum_alpha<=129);
    kn_text_renderer_destroy(renderer);
#else
    REQUIRE(kn_text_renderer_create(0,&renderer,&error)==KN_GPU_UNSUPPORTED&&renderer==nullptr);
#endif
    kn_text_destroy(session);return 0;
}
