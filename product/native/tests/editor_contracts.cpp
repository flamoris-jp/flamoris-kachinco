#include "kachinco_native.h"
#include "../src/editor_domain.hpp"
#include <iostream>
#include <stdexcept>
#include <cstdio>
#if defined(__SANITIZE_ADDRESS__)
extern "C" size_t __sanitizer_get_current_allocated_bytes();
#elif defined(__GLIBC__)
#include <malloc.h>
#endif
using kn_editor::json;
#define REQUIRE(x) do { if(!(x)) {std::cerr<<"Failed "<<__LINE__<<": "<<#x<<'\n';return 1;} } while(false)
static json request(kn_editor_session* session,const std::string& input) {
    kn_buffer* b=nullptr;auto status=kn_editor_request(session,reinterpret_cast<const uint8_t*>(input.data()),static_cast<uint32_t>(input.size()),&b);
    if(status!=KN_OK||!b) throw std::runtime_error("request"); uint32_t size=0; kn_buffer_size(b,&size);std::vector<uint8_t> data(size);kn_buffer_copy(b,data.data(),size);kn_buffer_destroy(b);return json::parse(data);
}
static json commit(kn_editor_session* s,const json& req) {
    auto prepared=request(s,req.dump());
    if(prepared.at("success")!=true) throw std::runtime_error(prepared.dump());
    auto response=request(s,json({{"action","commit"},{"transactionId",prepared.at("transactionId")}}).dump());
    if(response.at("success")!=true) throw std::runtime_error(response.dump());
    return response;
}
static std::string id(int n) {
    char value[37];std::snprintf(value,sizeof(value),"00000000-0000-0000-0000-%012d",n);return value;
}
static json command(const char* type,const json& value) {return {{"type",type},{"value",value}};}
static json edit(const json& commands) {return {{"action","execute"},{"commands",commands}};}
static json snapshot(kn_editor_session* s) {return request(s,R"({"action":"get"})");}
static size_t allocated() {
#if defined(__SANITIZE_ADDRESS__)
    return __sanitizer_get_current_allocated_bytes();
#elif defined(__GLIBC__)
#if __GLIBC_PREREQ(2,33)
    return mallinfo2().uordblks;
#else
    return 0;
#endif
#else
    return 0; // Retained-allocation acceptance is measured on Linux/ASan.
#endif
}
static int history_contracts() {
    kn_editor_session* s=nullptr;REQUIRE(kn_editor_create(100,&s)==KN_OK);
    json captions=json::array();
    for(int n=0;n<1000;++n) captions.push_back({{"id",id(1000+n)},{"startTicks",int64_t(n)*35280000},{"durationTicks",35280000},{"text",std::string(2050,'x')},{"enabled",true}});
    json project={{"id",id(100)},{"name","history"},{"assets",json::array()},{"sequences",json::array({{
        {"id",id(101)},{"name","sequence"},{"settings",{{"width",1920},{"height",1080},{"frameRate",{{"numerator",30},{"denominator",1}}}}},
        {"durationTicks",int64_t(1000)*35280000},{"clappers",json::array()},{"recipes",json::array()},
        {"tracks",json::array({{{"id",id(102)},{"name","subtitles"},{"kind",2},{"enabled",true},{"clips",json::array()},{"captions",std::move(captions)}}})}
    }})}};
    commit(s,{{"action","replace"},{"project",project}});
    const auto baseline=allocated();
    for(int n=1;n<=100;++n) commit(s,edit(json::array({command("SetTrackEnabled",{{"sequenceId",id(101)},{"trackId",id(102)},{"enabled",n%2==0}})})));
    const auto retained=allocated();
    // A small edit must not retain the unchanged 2 MB subtitle text 100 times.
    // Allow allocator bookkeeping; the old implementation grows by ~250 MB.
    REQUIRE(retained<=baseline+16*1024*1024);
    for(int n=99;n>=0;--n) {
        commit(s,{{"action","undo"}});
        if(n%10==0) {auto p=snapshot(s).at("project");REQUIRE(p["sequences"][0]["tracks"][0]["enabled"]==(n%2==0));REQUIRE(p["sequences"][0]["tracks"][0]["captions"]==project["sequences"][0]["tracks"][0]["captions"]);}
    }
    REQUIRE(snapshot(s).at("project")==project);
    REQUIRE(request(s,R"({"action":"undo"})").at("success")==false);
    for(int n=1;n<=100;++n) commit(s,{{"action","redo"}});
    REQUIRE(snapshot(s).at("project")==project);
    REQUIRE(request(s,R"({"action":"redo"})").at("success")==false);
    // Prepare/abort, dry-run, failed batch and stale revision preserve both stacks.
    commit(s,{{"action","undo"}});auto before=snapshot(s);
    request(s,R"({"action":"undo"})");request(s,R"({"action":"abort"})");REQUIRE(snapshot(s)==before);
    auto toggle=command("SetTrackEnabled",{{"sequenceId",id(101)},{"trackId",id(102)},{"enabled",true}});
    auto dry=edit(json::array({toggle}));dry["dryRun"]=true;REQUIRE(request(s,dry.dump()).at("success")==true);REQUIRE(snapshot(s)==before);
    auto bad=edit(json::array({toggle,command("DeleteCaption",{{"sequenceId",id(101)},{"captionId",id(999)}})}));
    REQUIRE(request(s,bad.dump()).at("success")==false);REQUIRE(snapshot(s)==before);
    auto stale=edit(json::array({toggle}));stale["expectedRevision"]=-1;REQUIRE(request(s,stale.dump()).at("success")==false);REQUIRE(snapshot(s)==before);
    commit(s,edit(json::array({toggle})));REQUIRE(snapshot(s).at("canRedo")==false);
    // Overflow evicts exactly the oldest entry, retaining the configured 100.
    for(int n=0;n<2;++n) commit(s,edit(json::array({toggle})));
    for(int n=0;n<100;++n) commit(s,{{"action","undo"}});
    REQUIRE(request(s,R"({"action":"undo"})").at("success")==false);
    commit(s,{{"action","replace"},{"project",nullptr}});REQUIRE(snapshot(s).at("canUndo")==false);REQUIRE(snapshot(s).at("canRedo")==false);
    // Null <-> Project travel must revoke document identity in both directions.
    auto generation=snapshot(s).at("documentGeneration").get<int64_t>();
    commit(s,edit(json::array({command("CreateProject",{{"projectId",id(200)},{"name","new"}})})));
    REQUIRE(snapshot(s).at("documentGeneration")==generation+1);
    commit(s,{{"action","undo"}});REQUIRE(snapshot(s).at("project").is_null());REQUIRE(snapshot(s).at("documentGeneration")==generation+2);
    commit(s,{{"action","redo"}});REQUIRE(snapshot(s).at("project").at("id")==id(200));REQUIRE(snapshot(s).at("documentGeneration")==generation+3);
    // A compound edit deleting an array element and reordering tracks is one entry.
    project["sequences"][0]["tracks"][0]["captions"].erase(project["sequences"][0]["tracks"][0]["captions"].begin()+1);
    project["sequences"][0]["tracks"].push_back({{"id",id(103)},{"name","empty"},{"kind",2},{"enabled",true},{"clips",json::array()},{"captions",json::array()}});
    commit(s,{{"action","replace"},{"project",project}});
    commit(s,edit(json::array({command("DeleteCaption",{{"sequenceId",id(101)},{"captionId",id(1000)}}),command("ReorderTrack",{{"sequenceId",id(101)},{"trackId",id(103)},{"newIndex",0}})})));
    auto after=snapshot(s).at("project");commit(s,{{"action","undo"}});REQUIRE(snapshot(s).at("project")==project);commit(s,{{"action","redo"}});REQUIRE(snapshot(s).at("project")==after);
    kn_editor_destroy(s);return 0;
}
int main() {
    REQUIRE(history_contracts()==0);
    kn_editor_session* s=nullptr; REQUIRE(kn_editor_create(0,&s)==KN_INVALID_ARGUMENT&&!s); REQUIRE(kn_editor_create(2,&s)==KN_OK);
    auto command=R"({"action":"execute","commands":[{"type":"CreateProject","value":{"projectId":"00000000-0000-0000-0000-000000000001","name":"日本語😀"}}]})";
    for(int i=0;i<200;++i) {
        auto prepared=request(s,command);REQUIRE(prepared.at("success")==true);REQUIRE(request(s,R"({"action":"get"})").at("project").is_null());
        if(i%2==0) {request(s,R"({"action":"abort"})");REQUIRE(request(s,json({{"action","commit"},{"transactionId",prepared.at("transactionId")}}).dump()).at("success")==false);}
        else {request(s,json({{"action","commit"},{"transactionId",prepared.at("transactionId")}}).dump());auto p=request(s,R"({"action":"get"})").at("project");auto encoded=request(nullptr,json({{"action","serialize"},{"project",p}}).dump());REQUIRE(encoded.at("diagnostics").empty());auto decoded=request(nullptr,json({{"action","deserialize"},{"text",encoded.at("value")}}).dump());REQUIRE(decoded.at("diagnostics").empty());auto replace=request(s,R"({"action":"replace","project":null})");request(s,json({{"action","commit"},{"transactionId",replace.at("transactionId")}}).dump());}
    }
    kn_editor_destroy(s);
    kn_buffer* b=nullptr;const uint8_t malformed[]={0xff};REQUIRE(kn_editor_request(nullptr,malformed,1,&b)==KN_INVALID_ARGUMENT&&!b);
    REQUIRE(kn_editor_request(nullptr,nullptr,0,&b)==KN_INVALID_ARGUMENT&&!b);
    const auto unsupported_schema=request(nullptr,json({{"action","deserialize"},{"text",R"({"schemaVersion":99})"}}).dump());
    REQUIRE(unsupported_schema.at("diagnostics")[0].at("code")=="SCHEMA_UNSUPPORTED");
    return 0;
}

