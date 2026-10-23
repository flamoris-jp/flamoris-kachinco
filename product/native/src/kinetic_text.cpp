#include "kinetic_text.hpp"
#include "kachinco_native.h"
#include "buffer.hpp"
#include <algorithm>
#include <cmath>
#include <memory>
#include <mutex>
#include <set>
#include <vector>
namespace kn_text {
namespace {
void fields(const json& v,std::initializer_list<const char*> names) {
    if(!v.is_object()||v.size()!=names.size()) throw std::invalid_argument("Unexpected kinetic fields");
    for(auto k:names) if(!v.contains(k)) throw std::invalid_argument("Missing kinetic field");
}
double number(const json& v,double low,double high) {
    if(!v.is_number()) throw std::invalid_argument("Kinetic number required");
    double n=v.get<double>();if(!std::isfinite(n)||n<low||n>high) throw std::invalid_argument("Kinetic number out of range");return n;
}
int64_t ticks(const json& v,bool positive=false) {
    if(!v.is_string()) throw std::invalid_argument("Kinetic ticks must be decimal strings");
    auto s=v.get<std::string>();if(s.empty()||s.size()>19||s.find_first_not_of("0123456789")!=std::string::npos) throw std::invalid_argument("Kinetic ticks invalid");
    size_t end=0;auto n=std::stoll(s,&end);if(end!=s.size()||(positive&&n==0)) throw std::invalid_argument("Kinetic tick range");return n;
}
void text(const json& v,size_t max) {if(!v.is_string()||v.get_ref<const std::string&>().empty()||v.get_ref<const std::string&>().size()>max||v.get_ref<const std::string&>().find('\0')!=std::string::npos) throw std::invalid_argument("Kinetic text size");}
void color(const json& v) {if(!v.is_array()||v.size()!=4) throw std::invalid_argument("RGBA color required");for(const auto& c:v) number(c,0,1);}
uint32_t hash(uint32_t n) {n^=n>>16;n*=0x7feb352du;n^=n>>15;n*=0x846ca68bu;return n^(n>>16);}
double noise(uint32_t seed,uint32_t unit,int64_t frame,uint32_t axis) {return static_cast<double>(hash(seed^hash(unit+axis)^hash(static_cast<uint32_t>(frame))^hash(static_cast<uint32_t>(static_cast<uint64_t>(frame)>>32))))/4294967295.0*2-1;}
}
void validate(const json& d) {
    if(d.dump().size()>16*1024*1024)throw std::invalid_argument("Text document limit");
    fields(d,{"version","id","width","height","fpsNumerator","fpsDenominator","durationTicks","clips"});
    if(!d.at("version").is_number_integer()||d.at("version")!=1) throw std::invalid_argument("Unsupported kinetic document version");
    if(kn_editor::guid(d.at("id"))=="00000000-0000-0000-0000-000000000000") throw std::invalid_argument("Empty kinetic document ID");
    for(auto k:{"width","height"}) {auto n=kn_editor::integer(d.at(k));if(n<16||n>4096||n%2) throw std::invalid_argument("Even kinetic dimensions 16..4096 required");}
    kn_runtime* runtime=nullptr;kn_runtime_create(KN_ABI_VERSION,&runtime);int32_t valid=0;
    auto n=kn_editor::integer(d.at("fpsNumerator")),den=kn_editor::integer(d.at("fpsDenominator"));
    auto status=n>INT32_MAX||den>INT32_MAX||n<1||den<1?KN_INVALID_ARGUMENT:kn_frame_rate_is_valid(runtime,static_cast<int32_t>(n),static_cast<int32_t>(den),&valid);kn_runtime_destroy(runtime);
    if(status!=KN_OK||!valid) throw std::invalid_argument("Invalid kinetic FPS");
    auto duration=ticks(d.at("durationTicks"),true);if(duration>KN_TICKS_PER_SECOND*86400) throw std::invalid_argument("Kinetic duration exceeds one day");
    const auto& clips=d.at("clips");if(!clips.is_array()||clips.size()>10000) throw std::invalid_argument("Kinetic clip limit");
    std::set<std::string> ids;std::vector<std::pair<int64_t,int>> events;
    for(const auto& c:clips) {
        fields(c,{"id","text","startTicks","durationTicks","fontFamily","fontSize","color","strokeColor","strokeWidth","shadowColor","shadowX","shadowY","x","y","seed","program","source","provenance"});
        auto id=kn_editor::guid(c.at("id"));if(id=="00000000-0000-0000-0000-000000000000"||!ids.insert(id).second) throw std::invalid_argument("Duplicate kinetic clip ID");
        text(c.at("text"),4096);text(c.at("fontFamily"),256);text(c.at("provenance"),2048);
        if(!c.at("source").is_string()||c.at("source").get_ref<const std::string&>().size()>65536)throw std::invalid_argument("Script source limit");
        auto start=ticks(c.at("startTicks")),length=ticks(c.at("durationTicks"),true);if(!kn_editor::valid_range(start,length,duration)) throw std::invalid_argument("Kinetic clip range");
        events.emplace_back(start,1);events.emplace_back(start+length,-1);
        number(c.at("fontSize"),1,2048);number(c.at("strokeWidth"),0,64);color(c.at("color"));color(c.at("strokeColor"));color(c.at("shadowColor"));
        for(auto k:{"x","y","shadowX","shadowY"}) number(c.at(k),-16384,16384);
        auto seed=kn_editor::integer(c.at("seed"));if(seed<0||seed>UINT32_MAX) throw std::invalid_argument("Kinetic seed");
        const auto& p=c.at("program");fields(p,{"version","ops"});if(!p.at("version").is_number_integer()||p.at("version")!=1||!p.at("ops").is_array()||p.at("ops").size()>32) throw std::invalid_argument("Kinetic IR version/limit");
        for(const auto& op:p.at("ops")) {
            auto type=op.at("op").get<std::string>();
            if(type=="translate"||type=="flight") {fields(op,type=="flight"?std::initializer_list<const char*>{"op","x","y","durationTicks"}:std::initializer_list<const char*>{"op","x","y"});number(op.at("x"),-16384,16384);number(op.at("y"),-16384,16384);if(type=="flight") ticks(op.at("durationTicks"),true);}
            else if(type=="scale") {fields(op,{"op","value"});number(op.at("value"),0.01,100);}
            else if(type=="rotate") {fields(op,{"op","degrees"});number(op.at("degrees"),-36000,36000);}
            else if(type=="opacity") {fields(op,{"op","value"});number(op.at("value"),0,1);}
            else if(type=="shake") {fields(op,{"op","amplitude","frequency"});number(op.at("amplitude"),0,2048);number(op.at("frequency"),0,240);}
            else if(type=="stagger") {fields(op,{"op","delayTicks"});if(ticks(op.at("delayTicks"))>KN_TICKS_PER_SECOND*60) throw std::invalid_argument("Stagger delay limit");}
            else throw std::invalid_argument("Unknown kinetic opcode");
        }
    }
    std::sort(events.begin(),events.end());int active=0;for(auto event:events) {active+=event.second;if(active>128) throw std::invalid_argument("128 active kinetic clips maximum");}
}
json initial() {return {{"version",1},{"id","11111111-1111-4111-8111-111111111111"},{"width",1280},{"height",720},{"fpsNumerator",30},{"fpsDenominator",1},{"durationTicks",std::to_string(5*KN_TICKS_PER_SECOND)},{"clips",json::array()}};}
motion evaluate(const json& c,int64_t tick,uint32_t unit) {
    motion m;auto start=ticks(c.at("startTicks")),duration=ticks(c.at("durationTicks"));
    if(tick<start||tick-start>=duration) {m.opacity=0;return m;}auto local=tick-start;
    const auto& ops=c.at("program").at("ops");
    for(const auto& op:ops) if(op.at("op")=="stagger") {auto delay=ticks(op.at("delayTicks"));if(unit!=0&&delay>local/static_cast<int64_t>(unit)) {m.opacity=0;return m;}local-=delay*unit;}
    for(const auto& op:ops) {
        const auto type=op.at("op").get<std::string>();
        if(type=="translate") {m.x+=op.at("x").get<double>();m.y+=op.at("y").get<double>();}
        else if(type=="scale") m.scale*=op.at("value").get<double>();
        else if(type=="rotate") m.rotation+=op.at("degrees").get<double>();
        else if(type=="opacity") m.opacity*=op.at("value").get<double>();
        else if(type=="flight") {double t=std::min(1.0,static_cast<double>(local)/static_cast<double>(ticks(op.at("durationTicks"))));double decay=std::pow(1-t,3);m.x+=op.at("x").get<double>()*decay;m.y+=op.at("y").get<double>()*decay;}
        else if(type=="shake") {double phase=static_cast<double>(local)/KN_TICKS_PER_SECOND*op.at("frequency").get<double>();auto frame=static_cast<int64_t>(phase);double f=phase-static_cast<double>(frame);f=f*f*(3-2*f);auto seed=static_cast<uint32_t>(kn_editor::integer(c.at("seed")));double a=op.at("amplitude").get<double>();m.x+=a*(noise(seed,unit,frame,101)*(1-f)+noise(seed,unit,frame+1,101)*f);m.y+=a*(noise(seed,unit,frame,211)*(1-f)+noise(seed,unit,frame+1,211)*f);}
    }
    return m;
}
}
struct kn_text_session {std::mutex gate;kn_text::json document;int64_t revision=0;std::vector<kn_text::json> undo,redo;};
namespace {
void response(const kn_text::json& value,kn_buffer** output) {auto s=value.dump();auto b=std::make_unique<kn_buffer>();b->data=std::make_shared<const std::vector<uint8_t>>(s.begin(),s.end());*output=b.release();}
}
int32_t KN_CALL kn_text_create(kn_text_session** output) noexcept {if(!output)return KN_INVALID_ARGUMENT;*output=nullptr;try {auto s=std::make_unique<kn_text_session>();s->document=kn_text::initial();*output=s.release();return KN_OK;}catch(...){return KN_OUT_OF_MEMORY;}}
void KN_CALL kn_text_destroy(kn_text_session* session) noexcept {delete session;}
int32_t KN_CALL kn_text_request(kn_text_session* s,const uint8_t* data,uint32_t size,kn_buffer** output) noexcept {
    if(!s||!output||!data||!size||size>16*1024*1024)return KN_INVALID_ARGUMENT;
    *output=nullptr;
    try {
        auto r=kn_editor::parse(std::string(reinterpret_cast<const char*>(data),size));std::lock_guard<std::mutex> lock(s->gate);
        auto action=r.at("action").get<std::string>();auto candidate=s->document;auto undo=s->undo,redo=s->redo;auto revision=s->revision;
        if(action!="get"&&r.at("expectedRevision").get<int64_t>()!=revision) {response({{"success",false},{"error","REVISION_CONFLICT"},{"revision",revision}},output);return KN_OK;}
        if(action=="replace"||action=="edit"||action=="deserialize") {candidate=action=="deserialize"?kn_editor::parse(r.at("text").get<std::string>()):r.at("document");kn_text::validate(candidate);if(action=="replace"||action=="deserialize"){undo.clear();redo.clear();}else {undo.push_back(kn_text::json::diff(candidate,s->document));redo.clear();}}
        else if(action=="undo"||action=="redo") {auto& from=action=="undo"?undo:redo;auto& to=action=="undo"?redo:undo;if(from.empty())throw std::invalid_argument("History empty");auto next=candidate.patch(from.back());from.pop_back();to.push_back(kn_text::json::diff(next,candidate));candidate=std::move(next);}
        else if(action!="get") throw std::invalid_argument("Unknown text command");
        if(action!="get") {if(revision==INT64_MAX) throw std::overflow_error("revision");++revision;}
        if(undo.size()>100)undo.erase(undo.begin());
        if(redo.size()>100)redo.erase(redo.begin());
        size_t history_bytes=0;for(const auto& entry:undo)history_bytes+=entry.dump().size();for(const auto& entry:redo)history_bytes+=entry.dump().size();
        while(history_bytes>32*1024*1024&&!undo.empty()){history_bytes-=undo.front().dump().size();undo.erase(undo.begin());}
        while(history_bytes>32*1024*1024&&!redo.empty()){history_bytes-=redo.front().dump().size();redo.erase(redo.begin());}
        response({{"success",true},{"revision",revision},{"document",candidate},{"canUndo",!undo.empty()},{"canRedo",!redo.empty()}},output);
        s->document.swap(candidate);s->undo.swap(undo);s->redo.swap(redo);s->revision=revision;return KN_OK;
    }catch(const std::bad_alloc&){return KN_OUT_OF_MEMORY;}catch(const std::exception& e){try{response({{"success",false},{"error",e.what()}},output);return KN_OK;}catch(...){return KN_OUT_OF_MEMORY;}}
}
int32_t KN_CALL kn_text_evaluate(const uint8_t* data,uint32_t size,int64_t tick,uint32_t unit,kn_text_motion* output) noexcept {
    if(!data||!size||size>16*1024*1024||!output)return KN_INVALID_ARGUMENT;
    try{auto d=kn_editor::parse(std::string(reinterpret_cast<const char*>(data),size));kn_text::validate(d);if(d.at("clips").empty())return KN_INVALID_ARGUMENT;auto m=kn_text::evaluate(d.at("clips")[0],tick,unit);*output={m.x,m.y,m.scale,m.rotation,m.opacity};return KN_OK;}catch(...){return KN_INVALID_ARGUMENT;}
}
int32_t KN_CALL kn_text_convert(const uint8_t* bgra,uint8_t* rgba,uint8_t* rgb,uint8_t* mask,uint32_t pixels) noexcept {
    if(!bgra||!rgba||!rgb||!mask||pixels>4096u*4096u)return KN_INVALID_ARGUMENT;
    for(uint32_t i=0;i<pixels;++i){auto p=i*4;uint32_t a=bgra[p+3];for(uint32_t k=0;k<3;++k){auto c=bgra[p+2-k];rgb[i*3+k]=c;mask[i*3+k]=static_cast<uint8_t>(a);rgba[p+k]=a==0?0:static_cast<uint8_t>(std::min(255u,(static_cast<uint32_t>(c)*255u+a/2u)/a));}rgba[p+3]=static_cast<uint8_t>(a);}return KN_OK;
}
int32_t KN_CALL kn_text_apply_mask(const uint8_t* rgb,const uint8_t* mask,uint8_t* rgba,uint32_t pixels) noexcept {
    if(!rgb||!mask||!rgba||pixels>4096u*4096u)return KN_INVALID_ARGUMENT;
    for(uint32_t i=0;i<pixels;++i){auto p=i*4;uint32_t a=mask[p];for(uint32_t k=0;k<3;++k)rgba[p+k]=a==0?0:static_cast<uint8_t>(std::min(255u,(static_cast<uint32_t>(rgb[p+k])*255u+a/2u)/a));rgba[p+3]=static_cast<uint8_t>(a);}return KN_OK;
}
