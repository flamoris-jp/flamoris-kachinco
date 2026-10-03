#include "editor_domain.hpp"
#include "time_math.hpp"
#include <algorithm>
#include <cmath>
#include <limits>
#include <set>
#include <map>
namespace kn_editor {
json error(const std::string& code, const std::string& message, const json& id, const json& path) {
    return {{"code",code},{"severity",0},{"message",message},{"entityId",id},{"path",path}};
}
int64_t integer(const json& v) {
    if (v.is_number_unsigned()) { auto n=v.get<uint64_t>(); if(n>uint64_t(INT64_MAX)) throw std::overflow_error("integer"); return static_cast<int64_t>(n); }
    if (!v.is_number_integer()) throw std::invalid_argument("integer");
    return v.get<int64_t>();
}
int64_t add(int64_t a,int64_t b) {
    if((b>0 && a>INT64_MAX-b)||(b<0 && a<INT64_MIN-b)) throw std::overflow_error("integer");
    return a+b;
}
bool valid_range(int64_t s,int64_t d,int64_t limit) { return s>=0&&d>0&&s<=limit&&d<=limit-s; }
std::string guid(const json& v) {
    if (!v.is_string()) throw std::invalid_argument("guid");
    const std::string s=v.get<std::string>();
    // Project JSON uses System.Text.Json's strict D-format GUID contract.
    if(s.size()!=36) throw std::invalid_argument("guid");
    for(size_t i=0;i<s.size();++i)
        if((i==8||i==13||i==18||i==23)!=(s[i]=='-')) throw std::invalid_argument("guid");
    std::string result;
    for(char c:s) { if(c=='-') continue; if(c>='A'&&c<='F') c=static_cast<char>(c-'A'+'a'); if(!((c>='0'&&c<='9')||(c>='a'&&c<='f'))) throw std::invalid_argument("guid"); result+=c; }
    if(result.size()!=32) throw std::invalid_argument("guid");
    return result.substr(0,8)+"-"+result.substr(8,4)+"-"+result.substr(12,4)+"-"+result.substr(16,4)+"-"+result.substr(20);
}
static const json empty_array=json::array();
static const json null_value=nullptr;
static const json& field(const json& v,const char* name) { auto i=v.find(name); return i==v.end()?null_value:*i; }
static const json& items(const json& v) { return v.is_array()?v:empty_array; }
static bool finite(const json& v) { return v.is_number()&&std::isfinite(v.get<double>()); }
static bool whitespace(uint32_t c) { return (c>=9&&c<=13)||c==32||c==0x85||c==0xa0||c==0x1680||(c>=0x2000&&c<=0x200a)||c==0x2028||c==0x2029||c==0x202f||c==0x205f||c==0x3000; }
static std::pair<size_t,bool> text_info(const json& v) {
    if(!v.is_string()) return {0,true};
    const auto& s=v.get_ref<const std::string&>(); size_t length=0; bool blank=true;
    for(size_t i=0;i<s.size();) {
        uint32_t c=static_cast<unsigned char>(s[i++]);
        if(c>=0xc0) { int count=c<0xe0?1:c<0xf0?2:3; c&=count==1?31:count==2?15:7; while(count-->0&&i<s.size()) c=(c<<6)|(static_cast<unsigned char>(s[i++])&63); }
        length+=c>0xffff?2:1; blank=blank&&whitespace(c);
    }
    return {length,blank};
}
static bool text_ok(const json& v,size_t max,bool allow_blank=false) { auto t=text_info(v); return v.is_string()&&t.first<=max&&(allow_blank||!t.second); }
static bool has_id(const json& values,const json& id) { for(const auto& v:items(values)) if(!v.is_null()&&field(v,"id")==id) return true; return false; }
static bool has_clip(const json& tracks,const json& id) { for(const auto& t:items(tracks)) if(has_id(field(t,"clips"),id)) return true; return false; }
static bool hash(const json& v) { if(!v.is_string()) return false; const auto& s=v.get_ref<const std::string&>(); return s.size()==64&&std::all_of(s.begin(),s.end(),[](char c){return(c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F');}); }
json validate(const json& p) {
    json errors=json::array(); std::set<std::string> ids;
    auto e=[&](const char* code,const char* message,const json& id=null_value,const json& path=null_value){errors.push_back(error(code,message,id,path));};
    auto identity=[&](const json& id,const json& name,const char* path){
        const auto key=guid(id);
        if(key=="00000000-0000-0000-0000-000000000000"||!ids.insert(key).second) e("INVALID_ID","ID must be nonempty and globally unique.",id,path);
        if(!text_ok(name,256)) e("INVALID_NAME","Name must contain 1–256 characters.",id,path);
    };
    if(p.is_null()) return json::array({error("PROJECT_REQUIRED","Project is required.")});
    identity(p.at("id"),field(p,"name"),"project");
    if(!field(p,"assets").is_array()||!field(p,"sequences").is_array()) return json::array({error("INVALID_COLLECTION","Assets and sequences must be initialized.",p.at("id"))});
    std::map<std::string,const json*> assets;
    for(const auto& a:p.at("assets")) {
        if(a.is_null()) { e("INVALID_ASSET","Null media asset."); continue; }
        const auto& id=a.at("id"); identity(id,field(a,"name"),"assets"); assets.emplace(guid(id),&a);
        auto kind=integer(a.at("kind"));
        if(kind<0||kind>1) e("INVALID_MEDIA_KIND","Unknown media kind.",id);
        if(!valid_range(0,integer(a.at("durationTicks")))) e("INVALID_MEDIA_DURATION","Media duration must be positive.",id);
        const auto& path=field(a,"sourcePath"); bool supported=false;
        if(text_ok(path,32768)) {
            const auto s=path.get<std::string>(); auto dot=s.find_last_of('.'); auto slash=s.find_last_of("/\\");
            std::string ext=dot!=std::string::npos&&(slash==std::string::npos||dot>slash)?s.substr(dot):"";
            for(char& c:ext) if(c>='A'&&c<='Z') c=static_cast<char>(c+32);
            supported=s.find('\0')==std::string::npos&&s.find('\r')==std::string::npos&&s.find('\n')==std::string::npos&&s.find("://")==std::string::npos&&
                (kind==0?(ext==".mov"||ext==".mp4"):kind==1?(ext==".wav"||ext==".mp3"||ext==".m4a"):false);
        }
        if(!supported) e("UNSUPPORTED_MEDIA_SOURCE","Register a local MOV/MP4 video or WAV/MP3/M4A audio path matching its media kind.",id,"sourcePath");
        const auto& rate=field(a,"sampleRate"); const auto& channels=field(a,"channels");
        if((!rate.is_null()&&(integer(rate)<=0||integer(rate)>384000))||(!channels.is_null()&&(integer(channels)<=0||integer(channels)>32))) e("INVALID_AUDIO_METADATA","Invalid sample rate or channel count.",id);
    }
    for(const auto& s:p.at("sequences")) {
        if(s.is_null()) { e("INVALID_SEQUENCE","Null sequence."); continue; }
        const auto& id=s.at("id"); identity(id,field(s,"name"),"sequences"); const auto& settings=field(s,"settings");
        bool good_settings=false;
        if(settings.is_object()) {
            auto w=integer(settings.at("width")),h=integer(settings.at("height")); const auto& fps=settings.at("frameRate");
            auto num=integer(fps.at("numerator")),den=integer(fps.at("denominator"));
            good_settings=num<=INT32_MAX&&den<=INT32_MAX&&kn_time::valid_fps(static_cast<int32_t>(num),static_cast<int32_t>(den))&&((w==1920&&h==1080)||(w==1080&&h==1920));
        }
        if(!good_settings) e("INVALID_SEQUENCE_SETTINGS","Use a landscape/portrait preset and reduced rational FPS between 1 and 240.",id);
        const auto duration=integer(s.at("durationTicks"));
        if(!valid_range(0,duration)) e("INVALID_SEQUENCE_DURATION","Sequence duration must be positive.",id);
        const auto& tracks=field(s,"tracks"); if(!tracks.is_array()) { e("INVALID_COLLECTION","Tracks must be initialized.",id); continue; }
        const auto& clappers=field(s,"clappers"); const auto& recipes=field(s,"recipes");
        if(!clappers.is_array()||!recipes.is_array()) { e("INVALID_AUTHORING","Authoring arrays must be initialized."); continue; }
        std::set<std::string> names;
        for(const auto& c:clappers) {
            if(c.is_null()) { e("INVALID_CLAPPER","Clapper is null."); continue; }
            const auto& cid=c.at("id"); const auto& name=field(c,"name"); identity(cid,name,"clappers");
            if(!names.insert(name.dump()).second) e("CLAPPER_NAME_CONFLICT","Clapper names must be unique within the sequence.",cid);
            if(!valid_range(integer(c.at("startTicks")),integer(c.at("durationTicks")),duration)) e("INVALID_CLAPPER_RANGE","Clapper must fit the sequence.",cid);
            if(!text_ok(field(c,"notes"),65536,true)) e("INVALID_CLAPPER_NOTES","Invalid Clapper notes.",cid);
            if(!field(c,"targetTrackId").is_null()&&!has_id(tracks,c.at("targetTrackId"))) e("CLAPPER_TRACK_MISSING","Target track not found.",cid);
            if(!field(c,"sourceClipId").is_null()&&!has_clip(tracks,c.at("sourceClipId"))) e("CLAPPER_CLIP_MISSING","Source clip not found.",cid);
            const auto& g=field(c,"geometry");
            if(!g.is_null()) {
                bool good=settings.is_object()&&integer(g.at("kind"))>=0&&integer(g.at("kind"))<=1;
                for(const char* k:{"x","y","width","height"}) good=good&&finite(field(g,k));
                if(good) { auto x=g.at("x").get<double>(),y=g.at("y").get<double>(),w=g.at("width").get<double>(),h=g.at("height").get<double>(); auto sw=settings.at("width").get<double>(),sh=settings.at("height").get<double>();
                    good=x>=0&&y>=0&&x<=sw&&y<=sh&&(integer(g.at("kind"))==0?(w==0&&h==0):(w>0&&h>0&&w<=sw-x&&h<=sh-y)); }
                if(!good) e("INVALID_CLAPPER_GEOMETRY","Use point/rectangle in project pixels.",cid);
            }
        }
        for(const auto& r:recipes) {
            if(r.is_null()) { e("INVALID_RECIPE","Recipe is null."); continue; }
            const auto& rid=r.at("id"); identity(rid,"recipe","recipes");
            if(!has_id(clappers,r.at("clapperId"))) e("RECIPE_CLAPPER_MISSING","Recipe Clapper not found.",rid);
            if(!text_ok(field(r,"source"),65536)||integer(r.at("revision"))<1||field(r,"apiVersion")!="1"||field(r,"rendererVersion")!="1") e("INVALID_RECIPE","Recipe source/version is unsupported.",rid);
        }
        for(const auto& t:tracks) {
            if(t.is_null()) { e("INVALID_TRACK","Null track."); continue; }
            const auto& tid=t.at("id"); identity(tid,field(t,"name"),"tracks"); auto kind=integer(t.at("kind"));
            if(kind<0||kind>2) e("INVALID_TRACK_KIND","Unknown track kind.",tid);
            const auto& clips=field(t,"clips"); const auto& captions=field(t,"captions");
            if(!clips.is_array()||!captions.is_array()) { e("INVALID_COLLECTION","Track items must be initialized.",tid); continue; }
            if((kind==2&&!clips.empty())||(kind!=2&&!captions.empty())) e("TRACK_ITEM_MISMATCH","Captions belong to subtitle tracks; media clips do not.",tid);
            for(const auto& c:clips) {
                if(c.is_null()) { e("INVALID_CLIP","Null clip."); continue; }
                const auto& cid=c.at("id"); identity(cid,"clip","clips"); auto cd=integer(c.at("durationTicks"));
                if(!valid_range(integer(c.at("startTicks")),cd,duration)) e("INVALID_TIMELINE_RANGE","Clip must fit inside the sequence.",cid);
                auto it=assets.find(guid(c.at("mediaAssetId")));
                if(it==assets.end()) e("MEDIA_NOT_FOUND","Clip references missing media.",cid,"mediaAssetId");
                else { const auto& a=*it->second; if(!valid_range(integer(c.at("sourceInTicks")),cd,integer(a.at("durationTicks")))) e("INVALID_SOURCE_RANGE","Source range must fit inside the registered media.",cid);
                    if((kind==0&&integer(a.at("kind"))!=0)||(kind==1&&integer(a.at("kind"))!=1)) e("TRACK_MEDIA_MISMATCH","Video tracks accept video assets; audio tracks accept audio assets.",cid); }
                const auto& a=field(c,"appearance"); const auto& tr=field(a,"transform");
                bool good=a.is_object()&&tr.is_object()&&integer(a.at("blend"))>=0&&integer(a.at("blend"))<=1&&finite(field(a,"opacity"));
                if(good) good=a.at("opacity").get<double>()>=0&&a.at("opacity").get<double>()<=1;
                for(const char* k:{"x","y","rotationDegrees","scaleX","scaleY"}) good=good&&finite(field(tr,k));
                if(good) good=tr.at("scaleX").get<double>()>0&&tr.at("scaleY").get<double>()>0;
                if(!good) e("INVALID_APPEARANCE","Transform must be finite, scales positive and opacity within [0,1].",cid);
                const auto& audio=field(c,"audio"); bool good_audio=audio.is_object()&&finite(field(audio,"gain"));
                if(good_audio) good_audio=audio.at("gain").get<double>()>=0&&audio.at("gain").get<double>()<=16;
                if(!good_audio) e("INVALID_AUDIO_PROPERTIES","Gain must be finite and within [0,16].",cid);
                const auto& points=field(audio,"volumePoints");
                if(!points.is_null()) {
                    if(!points.is_array()||points.size()>4096) e("INVALID_VOLUME_CURVE","Volume curve must be an initialized array of at most 4096 points.",cid);
                    else {
                        std::set<std::string> point_ids; int64_t previous=0; bool first=true;
                        for(const auto& point:points) {
                            if(!point.is_object()) { e("INVALID_VOLUME_POINT","Volume point is required.",cid); continue; }
                            auto key=guid(point.at("id"));auto tick=integer(point.at("tick"));
                            if(key=="00000000-0000-0000-0000-000000000000"||!point_ids.insert(key).second||(!first&&tick<=previous)||
                               !finite(field(point,"multiplier"))||point.at("multiplier").get<double>()<0||point.at("multiplier").get<double>()>16)
                                e("INVALID_VOLUME_POINT","Use clip-scoped unique IDs, strictly ordered ticks and finite multiplier [0,16].",cid);
                            first=false;previous=tick;
                        }
                    }
                } else if(audio.contains("volumePoints")) e("INVALID_VOLUME_CURVE","Volume curve must be initialized.",cid);

            }
            for(const auto& c:captions) {
                if(c.is_null()) { e("INVALID_CAPTION","Null caption."); continue; }
                const auto& cid=c.at("id"); identity(cid,"caption","captions");
                if(!valid_range(integer(c.at("startTicks")),integer(c.at("durationTicks")),duration)) e("INVALID_CAPTION_RANGE","Caption must fit inside the sequence.",cid);
                if(!text_ok(field(c,"text"),65536)) e("INVALID_CAPTION_TEXT","Caption text must contain 1–65536 characters.",cid);
            }
        }
    }
    for(const auto& a:p.at("assets")) if(a.is_object()&&!field(a,"provenance").is_null()) {
        const auto& pr=a.at("provenance"); const json* recipe=nullptr;
        for(const auto& s:p.at("sequences")) for(const auto& r:items(field(s,"recipes"))) if(r.is_object()&&r.at("id")==pr.at("recipeId")) {recipe=&r; break;}
        if(!recipe||integer(pr.at("recipeRevision"))<1||integer(pr.at("recipeRevision"))>integer(recipe->at("revision"))||!hash(field(pr,"sourceSha256"))||!hash(field(pr,"outputSha256"))) e("INVALID_PROVENANCE","Generated provenance is invalid.",a.at("id"));
    }
    return errors;
}
static void reject(const char* code,const char* message,const json& id=null_value) { throw rejected{error(code,message,id)}; }
static size_t find(const json& array,const json& id,const char* code,const char* message,bool with_id=true) {
    for(size_t i=0;i<array.size();++i) if(array[i].is_object()&&array[i].at("id")==id) return i;
    reject(code,message,with_id?id:null_value); return 0;
}
json apply(json p,const json& request) {
    const auto type=request.at("type").get<std::string>(); const auto& c=request.at("value");
    if(type=="CreateProject") { if(!p.is_null()) reject("PROJECT_EXISTS","Use an explicit new session/project replacement first.",p.at("id")); return {{"id",c.at("projectId")},{"name",c.at("name")},{"assets",json::array()},{"sequences",json::array()}}; }
    if(p.is_null()) reject("PROJECT_REQUIRED","Create or open a project first.");
    if(type=="RegisterMedia") { p["assets"].push_back(c.at("asset")); return p; }
    if(type=="RelinkMedia"||type=="SetGeneratedProvenance") {
        auto& a=p["assets"][find(p.at("assets"),c.at("mediaAssetId"),"MEDIA_NOT_FOUND",type=="RelinkMedia"?"Media asset not found.":"Media not found.",type=="RelinkMedia")];
        if(type=="RelinkMedia") for(const char* k:{"sourcePath","durationTicks","sampleRate","channels"}) a[k]=c.at(k);
        else { if(c.at("provenance").is_null()) reject("INVALID_PROVENANCE","Provenance required."); a["provenance"]=c.at("provenance"); }
        return p;
    }
    if(type=="CreateSequence") { p["sequences"].push_back({{"id",c.at("sequenceId")},{"name",c.at("name")},{"settings",c.at("settings")},{"durationTicks",c.at("durationTicks")},{"tracks",json::array()},{"clappers",json::array()},{"recipes",json::array()}}); return p; }
    const std::set<std::string> supported={"AddClapper","UpdateClapper","DeleteClapper","AddRecipe","UpdateRecipe","SetSequenceDuration","AddTrack","InsertClip","MoveClip","RippleReorderClip","TrimClip","SplitClip","DeleteClip","SetClipProperties","AddClipVolumePoint","UpdateClipVolumePoint","DeleteClipVolumePoint","SetTrackEnabled","ReorderTrack","AddCaption","UpdateCaption","DeleteCaption"};
    if(!supported.count(type)) reject("UNSUPPORTED_COMMAND","Unknown editing command.");
    auto& s=p["sequences"][find(p.at("sequences"),c.at("sequenceId"),"SEQUENCE_NOT_FOUND","Sequence not found.")];
    if(type=="SetSequenceDuration") s["durationTicks"]=c.at("durationTicks");
    else if(type=="AddTrack") s["tracks"].push_back({{"id",c.at("trackId")},{"name",c.at("name")},{"kind",c.at("kind")},{"enabled",true},{"clips",json::array()},{"captions",json::array()}});
    else if(type=="AddClapper"||type=="AddRecipe") s[type=="AddClapper"?"clappers":"recipes"].push_back(c.at(type=="AddClapper"?"clapper":"recipe"));
    else if(type=="UpdateClapper"||type=="DeleteClapper"||type=="UpdateRecipe") {
        bool clapper=type!="UpdateRecipe"; auto& values=s[clapper?"clappers":"recipes"];
        const json& value=field(c,clapper?"clapper":"recipe");
        if(type!="DeleteClapper"&&value.is_null()&&values.empty()) reject(clapper?"CLAPPER_NOT_FOUND":"RECIPE_NOT_FOUND",clapper?"Clapper not found.":"Recipe not found.");
        if(type!="DeleteClapper"&&value.is_null()) reject(clapper?"INVALID_CLAPPER":"INVALID_RECIPE",clapper?"Clapper required.":"Recipe required.");
        auto index=find(values,type=="DeleteClapper"?c.at("clapperId"):value.at("id"),clapper?"CLAPPER_NOT_FOUND":"RECIPE_NOT_FOUND",clapper?"Clapper not found.":"Recipe not found.",false);
        if(type=="DeleteClapper") values.erase(values.begin()+static_cast<json::difference_type>(index));
        else { if(!clapper&&integer(value.at("revision"))!=(integer(values[index].at("revision"))==INT32_MAX?INT32_MIN:integer(values[index].at("revision"))+1)) reject("RECIPE_REVISION_CONFLICT","Recipe revision must advance once."); values[index]=value; }
    }
    else if(type=="InsertClip"||type=="SetTrackEnabled"||type=="ReorderTrack"||type=="AddCaption") {
        auto index=find(s.at("tracks"),c.at("trackId"),"TRACK_NOT_FOUND","Track not found.");
        if(type=="ReorderTrack") {auto dest=integer(c.at("newIndex")); if(dest<0||uint64_t(dest)>=s.at("tracks").size()) reject("INVALID_TRACK_ORDER","Track index is outside the sequence.",c.at("trackId")); auto value=s["tracks"][index]; auto& tracks=s["tracks"]; tracks.erase(tracks.begin()+static_cast<json::difference_type>(index)); tracks.insert(tracks.begin()+static_cast<json::difference_type>(dest),value);}
        else if(type=="SetTrackEnabled") s["tracks"][index]["enabled"]=c.at("enabled");
        else s["tracks"][index][type=="InsertClip"?"clips":"captions"].push_back(c.at(type=="InsertClip"?"clip":"caption"));
    }
    else {
        bool caption=type=="UpdateCaption"||type=="DeleteCaption"; auto id=c.at(caption?"captionId":"clipId"); size_t ti=0,ci=0; bool found=false;
        for(;ti<s.at("tracks").size();++ti) {const auto& values=s.at("tracks")[ti].at(caption?"captions":"clips"); for(ci=0;ci<values.size();++ci) if(values[ci].at("id")==id) {found=true; break;} if(found) break;}
        if(!found) reject(caption?"CAPTION_NOT_FOUND":"CLIP_NOT_FOUND",caption?"Caption not found.":"Clip not found.",id);
        auto& values=s["tracks"][ti][caption?"captions":"clips"];
        if(type=="DeleteClip"||type=="DeleteCaption") values.erase(values.begin()+static_cast<json::difference_type>(ci));
        else if(type=="RippleReorderClip") {
            if(integer(s["tracks"][ti].at("kind"))==2)
                reject("TRACK_MEDIA_MISMATCH","Choose a video or audio track.",id);
            std::vector<size_t> order; order.reserve(values.size());
            for(size_t i=0;i<values.size();++i) order.push_back(i);
            std::sort(order.begin(),order.end(),[&](size_t a,size_t b) {
                return integer(values[a].at("startTicks"))<integer(values[b].at("startTicks"));
            });
            auto start=[&](size_t i) {return integer(values[order[i]].at("startTicks"));};
            auto end=[&](size_t i) {const auto& v=values[order[i]];return add(integer(v.at("startTicks")),integer(v.at("durationTicks")));};
            for(size_t i=1;i<order.size();++i) if(end(i-1)>start(i))
                reject("CLIP_OVERLAP","Ripple reordering requires a non-overlapping lane.",id);
            size_t origin=0; while(order[origin]!=ci) ++origin;
            size_t first=origin,last=origin;
            while(first>0&&end(first-1)==start(first)) --first;
            while(last+1<order.size()&&end(last)==start(last+1)) ++last;
            const auto& before=c.at("beforeClipId"); size_t destination=last+1;
            if(!before.is_null()) {
                destination=first;
                while(destination<=last&&values[order[destination]].at("id")!=before) ++destination;
                if(destination>last) reject("RIPPLE_GAP","Insertion must stay within the contiguous run.",id);
            }
            if(destination==origin||destination==origin+1)
                reject("REORDER_UNCHANGED","This insertion does not change clip order.",id);
            const auto anchor=start(first); const auto moved=order[origin];
            order.erase(order.begin()+static_cast<std::vector<size_t>::difference_type>(origin));
            if(destination>origin) --destination;
            order.insert(order.begin()+static_cast<std::vector<size_t>::difference_type>(destination),moved);
            auto cursor=anchor;
            for(size_t i=first;i<=last;++i) {
                auto& clip=values[order[i]]; clip["startTicks"]=cursor;
                cursor=add(cursor,integer(clip.at("durationTicks")));
            }
        }
        else if(type=="MoveClip") {json value=values[ci]; values.erase(values.begin()+static_cast<json::difference_type>(ci)); value["startTicks"]=c.at("startTicks"); auto target=find(s.at("tracks"),c.at("targetTrackId"),"TRACK_NOT_FOUND","Track not found."); s["tracks"][target]["clips"].push_back(value);}
        else if(type=="SplitClip") { auto& clip=values[ci]; auto split=integer(c.at("splitTicks")),start=integer(clip.at("startTicks")),duration=integer(clip.at("durationTicks")); if(split<=start||split>=add(start,duration)) reject("INVALID_SPLIT","Split must be strictly inside the clip.",id); auto left=split-start; json right=clip; clip["durationTicks"]=left; right["id"]=c.at("rightClipId"); right["startTicks"]=split; right["sourceInTicks"]=add(integer(clip.at("sourceInTicks")),left); right["durationTicks"]=duration-left; if(right["audio"].contains("volumePoints")) for(auto& point:right["audio"]["volumePoints"]) point["tick"]=add(integer(point.at("tick")),-left); values.push_back(right);}
        else if(type=="TrimClip") {
            const auto delta=add(integer(values[ci].at("sourceInTicks")),-integer(c.at("sourceInTicks")));
            if(values[ci]["audio"].contains("volumePoints")) for(auto& point:values[ci]["audio"]["volumePoints"]) point["tick"]=add(integer(point.at("tick")),delta);
            for(const char* k:{"startTicks","sourceInTicks","durationTicks"}) values[ci][k]=c.at(k);
        }
        else if(type=="SetClipProperties") {
            values[ci]["enabled"]=c.at("enabled");values[ci]["appearance"]=c.at("appearance");
            values[ci]["audio"]["gain"]=c.at("audio").at("gain");values[ci]["audio"]["muted"]=c.at("audio").at("muted");
        }
        else if(type=="AddClipVolumePoint"||type=="UpdateClipVolumePoint"||type=="DeleteClipVolumePoint") {
            if(integer(s["tracks"][ti].at("kind"))!=1) reject("VOLUME_AUDIO_REQUIRED","Volume points belong to audio-track clips.",id);
            auto& audio=values[ci]["audio"];if(!audio.contains("volumePoints")) audio["volumePoints"]=json::array();auto& points=audio["volumePoints"];
            if(type=="AddClipVolumePoint") points.push_back(c.at("point"));
            else {
                auto index=find(points,type=="DeleteClipVolumePoint"?c.at("pointId"):c.at("point").at("id"),"VOLUME_POINT_NOT_FOUND","Volume point not found.");
                if(type=="DeleteClipVolumePoint") points.erase(points.begin()+static_cast<json::difference_type>(index));
                else points[index]=c.at("point");
            }
            std::stable_sort(points.begin(),points.end(),[](const json& a,const json& b){return integer(a.at("tick"))<integer(b.at("tick"));});
        }
        else if(type=="UpdateCaption") for(const char* k:{"startTicks","durationTicks","text","enabled"}) values[ci][k]=c.at(k);
    }
    return p;
}
}
