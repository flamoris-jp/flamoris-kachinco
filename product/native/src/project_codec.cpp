#include "editor_domain.hpp"
#include "kachinco_native.h"
#include <algorithm>
#include <charconv>
#include <set>
#include <vector>
namespace kn_editor {
json parse(const std::string& text,bool duplicates) {
    std::vector<std::set<std::string>> keys;
    return json::parse(text,[&](int depth,json::parse_event_t event,json& value){
        if(depth>64) throw std::invalid_argument("depth");
        if(event==json::parse_event_t::object_start) keys.emplace_back();
        else if(event==json::parse_event_t::object_end) keys.pop_back();
        else if(event==json::parse_event_t::key&&duplicates&&!keys.back().insert(value.get<std::string>()).second) throw std::invalid_argument("duplicate");
        return true;
    });
}
static void fields(const json& v,std::initializer_list<const char*> names) {
    if(!v.is_object()||v.size()!=names.size()) throw std::invalid_argument("fields");
    for(const auto* name:names) if(!v.contains(name)) throw std::invalid_argument("required");
}
static void array(const json& v) { if(!v.is_array()) throw std::invalid_argument("array"); }
static std::string text(const json& v) { if(!v.is_string()) throw std::invalid_argument("string"); return v.get<std::string>(); }
static bool boolean(const json& v) { if(!v.is_boolean()) throw std::invalid_argument("boolean"); return v.get<bool>(); }
static int32_t i32(const json& v) { auto n=integer(v); if(n<INT32_MIN||n>INT32_MAX) throw std::overflow_error("int32"); return static_cast<int32_t>(n); }
static json optional_i32(const json& v) { return v.is_null()?json(nullptr):json(i32(v)); }
static json optional_guid(const json& v) { return v.is_null()?json(nullptr):json(guid(v)); }
static double number(const json& v) { if(!v.is_number()) throw std::invalid_argument("number"); return v.get<double>(); }
static int64_t ticks(const json& v) {
    auto s=text(v); if(s.empty()) throw std::invalid_argument("tick"); bool plus=s[0]=='+'; const char* begin=s.data()+(plus?1:0); if(begin==s.data()+s.size()||(plus&&*begin=='-')) throw std::invalid_argument("tick");
    int64_t n=0; auto r=std::from_chars(begin,s.data()+s.size(),n); if(r.ec!=std::errc()||r.ptr!=s.data()+s.size()) throw std::invalid_argument("tick"); return n;
}
static const std::vector<std::string> media={"Mov","Wav"},track={"Video","Audio","Subtitle"},blend={"Normal","Screen"},geometry={"Point","Rectangle"};
static int enum_value(const json& v,const std::vector<std::string>& names) {
    auto name=text(v); for(char& c:name) if(c>='A'&&c<='Z') c=static_cast<char>(c+32);
    int result=0;size_t start=0;
    while(start<name.size()) {
        auto comma=name.find(',',start);auto token=name.substr(start,comma==std::string::npos?std::string::npos:comma-start);
        auto first=token.find_first_not_of(" \t\r\n"),last=token.find_last_not_of(" \t\r\n");if(first==std::string::npos) throw std::invalid_argument("enum");token=token.substr(first,last-first+1);
        bool found=false;
        for(size_t i=0;i<names.size();++i) {auto expected=names[i];for(char& c:expected) if(c>='A'&&c<='Z') c=static_cast<char>(c+32);if(token==expected) {result|=static_cast<int>(i);found=true;break;}}
        if(!found) throw std::invalid_argument("enum");
        if(comma==std::string::npos)return result;
        start=comma+1;
    }
    throw std::invalid_argument("enum");
}
static json sorted(json values,bool timed=false) {
    std::stable_sort(values.begin(),values.end(),[&](const json& a,const json& b){if(timed&&integer(a.at("startTicks"))!=integer(b.at("startTicks"))) return integer(a.at("startTicks"))<integer(b.at("startTicks")); return a.at("id").get<std::string>()<b.at("id").get<std::string>();}); return values;
}
static json encode_clip(json c,bool curves) {
    auto& audio=c["audio"];
    if(curves) {
        if(!audio.contains("volumePoints")) audio["volumePoints"]=json::array();
        for(auto& point:audio["volumePoints"]) point["tick"]=std::to_string(integer(point.at("tick")));
    } else audio.erase("volumePoints");
    c["startTicks"]=std::to_string(integer(c.at("startTicks"))); c["sourceInTicks"]=std::to_string(integer(c.at("sourceInTicks"))); c["durationTicks"]=std::to_string(integer(c.at("durationTicks"))); c["appearance"]["blend"]=blend.at(static_cast<size_t>(integer(c.at("appearance").at("blend")))); return c;
}
json encode_file(const json& p) {
    bool curves=false;
    for(const auto& s:p.at("sequences")) for(const auto& t:s.at("tracks")) for(const auto& c:t.at("clips"))
        if(c.at("audio").contains("volumePoints")&&!c.at("audio").at("volumePoints").empty()) curves=true;
    json result={{"format","flamoris-kachinco"},{"schemaVersion",curves?3:2},{"timebase",std::to_string(KN_TICKS_PER_SECOND)},{"project",{{"id",p.at("id")},{"name",p.at("name")},{"assets",json::array()},{"sequences",json::array()}}},{"authoring",json::array()},{"generatedAssets",json::array()}};
    for(auto a:sorted(p.at("assets"))) {
        const auto pr=a.value("provenance",json(nullptr)); a.erase("provenance"); a["kind"]=media.at(static_cast<size_t>(integer(a.at("kind")))); a["durationTicks"]=std::to_string(integer(a.at("durationTicks"))); result["project"]["assets"].push_back(a);
        if(!pr.is_null()) result["generatedAssets"].push_back({{"mediaAssetId",a.at("id")},{"recipeId",pr.at("recipeId")},{"recipeRevision",pr.at("recipeRevision")},{"sourceSha256",pr.at("sourceSha256")},{"outputSha256",pr.at("outputSha256")}});
    }
    for(auto s:sorted(p.at("sequences"))) {
        auto clappers=sorted(s.at("clappers")),recipes=sorted(s.at("recipes"));
        for(auto& c:clappers) {c["startTicks"]=std::to_string(integer(c.at("startTicks"))); c["durationTicks"]=std::to_string(integer(c.at("durationTicks"))); if(!c.at("geometry").is_null()) c["geometry"]["kind"]=geometry.at(static_cast<size_t>(integer(c.at("geometry").at("kind"))));}
        result["authoring"].push_back({{"sequenceId",s.at("id")},{"clappers",clappers},{"recipes",recipes}}); s.erase("clappers"); s.erase("recipes");
        auto fps=s.at("settings").at("frameRate"); s["settings"].erase("frameRate"); s["settings"]["fpsNumerator"]=fps.at("numerator"); s["settings"]["fpsDenominator"]=fps.at("denominator"); s["durationTicks"]=std::to_string(integer(s.at("durationTicks")));
        for(auto& t:s["tracks"]) {t["kind"]=track.at(static_cast<size_t>(integer(t.at("kind")))); auto clips=sorted(t.at("clips"),true),captions=sorted(t.at("captions"),true); for(auto& c:clips) c=encode_clip(c,curves); for(auto& c:captions) {c["startTicks"]=std::to_string(integer(c.at("startTicks")));c["durationTicks"]=std::to_string(integer(c.at("durationTicks")));} t["clips"]=clips;t["captions"]=captions;}
        result["project"]["sequences"].push_back(s);
    }
    return result;
}
json decode_file(const std::string& raw) {
    auto first=parse(raw,false);
    if(!first.is_object()||!first.contains("schemaVersion")||!first.at("schemaVersion").is_number_integer()) throw rejected{error("INVALID_ENVELOPE","An integer schemaVersion is required.")};
    int32_t schema;
    try { schema=i32(first.at("schemaVersion")); }
    catch(...) { throw rejected{error("INVALID_ENVELOPE","An integer schemaVersion is required.")}; }
    if(schema!=1&&schema!=2&&schema!=3) throw rejected{error("SCHEMA_UNSUPPORTED","Schema "+std::to_string(schema)+" is not supported.")};
    auto root=parse(raw);
    if(schema==1) fields(root,{"format","schemaVersion","timebase","project"}); else fields(root,{"format","schemaVersion","timebase","project","authoring","generatedAssets"});
    if(text(root.at("format"))!="flamoris-kachinco"||ticks(root.at("timebase"))!=KN_TICKS_PER_SECOND) throw std::invalid_argument("envelope");
    auto p=root.at("project"); fields(p,{"id","name","assets","sequences"}); p["id"]=guid(p.at("id")); p["name"]=text(p.at("name")); array(p.at("assets"));array(p.at("sequences"));
    for(auto& a:p["assets"]) {
        fields(a,{"id","name","sourcePath","kind","durationTicks","sampleRate","channels"}); a["id"]=guid(a.at("id"));a["name"]=text(a.at("name"));a["sourcePath"]=text(a.at("sourcePath")); a["kind"]=enum_value(a.at("kind"),media); a["durationTicks"]=ticks(a.at("durationTicks")); a["sampleRate"]=optional_i32(a.at("sampleRate"));a["channels"]=optional_i32(a.at("channels"));a["provenance"]=nullptr;
    }
    for(auto& s:p["sequences"]) {
        fields(s,{"id","name","settings","durationTicks","tracks"});s["id"]=guid(s.at("id"));s["name"]=text(s.at("name"));s["durationTicks"]=ticks(s.at("durationTicks"));
        auto settings=s.at("settings"); fields(settings,{"width","height","fpsNumerator","fpsDenominator"}); s["settings"]={{"width",i32(settings.at("width"))},{"height",i32(settings.at("height"))},{"frameRate",{{"numerator",i32(settings.at("fpsNumerator"))},{"denominator",i32(settings.at("fpsDenominator"))}}}}; array(s.at("tracks"));
        for(auto& t:s["tracks"]) {
            fields(t,{"id","name","kind","enabled","clips","captions"});t["id"]=guid(t.at("id"));t["name"]=text(t.at("name"));t["kind"]=enum_value(t.at("kind"),track);t["enabled"]=boolean(t.at("enabled"));array(t.at("clips"));array(t.at("captions"));
            for(auto& c:t["clips"]) {
                fields(c,{"id","mediaAssetId","startTicks","sourceInTicks","durationTicks","enabled","appearance","audio"});c["id"]=guid(c.at("id"));c["mediaAssetId"]=guid(c.at("mediaAssetId"));for(const char* k:{"startTicks","sourceInTicks","durationTicks"}) c[k]=ticks(c.at(k));c["enabled"]=boolean(c.at("enabled"));
                auto& a=c["appearance"];fields(a,{"transform","opacity","blend"});a["opacity"]=number(a.at("opacity"));a["blend"]=enum_value(a.at("blend"),blend);auto& tr=a["transform"];fields(tr,{"x","y","scaleX","scaleY","rotationDegrees"});for(const char* k:{"x","y","scaleX","scaleY","rotationDegrees"}) tr[k]=number(tr.at(k));
                auto& audio=c["audio"];
                if(schema==3) fields(audio,{"gain","muted","volumePoints"});else fields(audio,{"gain","muted"});
                audio["gain"]=number(audio.at("gain"));audio["muted"]=boolean(audio.at("muted"));
                if(schema<3) audio["volumePoints"]=json::array();
                else {array(audio.at("volumePoints"));for(auto& point:audio["volumePoints"]) {fields(point,{"id","tick","multiplier"});point["id"]=guid(point.at("id"));point["tick"]=ticks(point.at("tick"));point["multiplier"]=number(point.at("multiplier"));}}
            }
            for(auto& c:t["captions"]) {fields(c,{"id","startTicks","durationTicks","text","enabled"});c["id"]=guid(c.at("id"));c["startTicks"]=ticks(c.at("startTicks"));c["durationTicks"]=ticks(c.at("durationTicks"));c["text"]=text(c.at("text"));c["enabled"]=boolean(c.at("enabled"));}
        }
        s["clappers"]=json::array();s["recipes"]=json::array();
    }
    if(schema>=2) {
        const auto& authoring=root.at("authoring"); const auto& generated=root.at("generatedAssets");array(authoring);array(generated);
        if(authoring.size()!=p.at("sequences").size()) throw std::invalid_argument("authoring");
        std::set<std::string> seen;
        for(auto a:authoring) {
            fields(a,{"sequenceId","clappers","recipes"});auto id=guid(a.at("sequenceId"));if(!seen.insert(id).second) throw std::invalid_argument("authoring");
            auto s=std::find_if(p["sequences"].begin(),p["sequences"].end(),[&](const json& v){return v.at("id")==id;});if(s==p["sequences"].end()) throw std::invalid_argument("sequence");array(a.at("clappers"));array(a.at("recipes"));
            for(auto& c:a["clappers"]) {
                fields(c,{"id","name","startTicks","durationTicks","geometry","targetTrackId","sourceClipId","notes"}); c["id"]=guid(c.at("id"));c["name"]=text(c.at("name"));c["startTicks"]=ticks(c.at("startTicks"));c["durationTicks"]=ticks(c.at("durationTicks"));c["targetTrackId"]=optional_guid(c.at("targetTrackId"));c["sourceClipId"]=optional_guid(c.at("sourceClipId"));
                // v2 authoring nullable strings are validated by domain rules.
                if(!c.at("notes").is_null()) c["notes"]=text(c.at("notes"));
                if(!c.at("geometry").is_null()) {auto& g=c["geometry"];fields(g,{"kind","x","y","width","height"});g["kind"]=enum_value(g.at("kind"),geometry);for(const char* k:{"x","y","width","height"}) g[k]=number(g.at(k));}
            }
            for(auto& r:a["recipes"]) {fields(r,{"id","clapperId","source","revision","seed","apiVersion","rendererVersion"});r["id"]=guid(r.at("id"));r["clapperId"]=guid(r.at("clapperId"));r["revision"]=i32(r.at("revision"));r["seed"]=i32(r.at("seed"));for(const char* k:{"source","apiVersion","rendererVersion"}) if(!r.at(k).is_null()) r[k]=text(r.at(k));}
            (*s)["clappers"]=a.at("clappers");(*s)["recipes"]=a.at("recipes");
        }
        seen.clear();
        for(const auto& g:generated) {
            fields(g,{"mediaAssetId","recipeId","recipeRevision","sourceSha256","outputSha256"});auto id=guid(g.at("mediaAssetId"));if(!seen.insert(id).second) throw std::invalid_argument("generated");auto a=std::find_if(p["assets"].begin(),p["assets"].end(),[&](const json& v){return v.at("id")==id;});if(a==p["assets"].end()) throw std::invalid_argument("asset");
            for(const char* k:{"sourceSha256","outputSha256"}) if(!g.at(k).is_null()) (void)text(g.at(k));
            (*a)["provenance"]={{"recipeId",guid(g.at("recipeId"))},{"recipeRevision",i32(g.at("recipeRevision"))},{"sourceSha256",g.at("sourceSha256")},{"outputSha256",g.at("outputSha256")}};
        }
    }
    return p;
}
}
