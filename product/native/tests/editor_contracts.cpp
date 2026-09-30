#include "kachinco_native.h"
#include "../src/editor_domain.hpp"
#include <iostream>
#include <stdexcept>
using kn_editor::json;
#define REQUIRE(x) do { if(!(x)) {std::cerr<<"Failed "<<__LINE__<<": "<<#x<<'\n';return 1;} } while(false)
static json request(kn_editor_session* session,const std::string& input) {
    kn_buffer* b=nullptr;auto status=kn_editor_request(session,reinterpret_cast<const uint8_t*>(input.data()),static_cast<uint32_t>(input.size()),&b);
    if(status!=KN_OK||!b) throw std::runtime_error("request"); uint32_t size=0; kn_buffer_size(b,&size);std::vector<uint8_t> data(size);kn_buffer_copy(b,data.data(),size);kn_buffer_destroy(b);return json::parse(data);
}
int main() {
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
    REQUIRE(request(nullptr,R"({"action":"deserialize","text":"{\"schemaVersion\":99}"})").at("diagnostics")[0].at("code")=="SCHEMA_UNSUPPORTED");
    return 0;
}
