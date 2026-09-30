#include "editor_domain.hpp"
#include "buffer.hpp"
#include "kachinco_native.h"
#include <mutex>
#include <memory>
#include <vector>
using kn_editor::json;
struct history_entry {
    json backward,forward;
};
using history_ptr=std::shared_ptr<const history_entry>;
struct editor_state {
    std::shared_ptr<const json> project=std::make_shared<const json>(nullptr);
    std::vector<history_ptr> undo,redo;
    int64_t revision=0,document_generation=0;
};
struct kn_editor_session {
    std::mutex gate;
    editor_state state;
    std::unique_ptr<editor_state> pending;
    int32_t history_limit;
    int64_t transaction=0;
    explicit kn_editor_session(int32_t limit):history_limit(limit){}
};
namespace {
json fail(int64_t revision,const std::string& code,const std::string& message) {return {{"success",false},{"revision",revision},{"diagnostics",json::array({kn_editor::error(code,message)})}};}
json result(int64_t revision,const json& diagnostics) {return {{"success",diagnostics.empty()},{"revision",revision},{"diagnostics",diagnostics}};}
void push(std::vector<history_ptr>& values,const history_ptr& value,int32_t limit) {values.push_back(value);if(values.size()>static_cast<size_t>(limit)) values.erase(values.begin());}
json identity(const json& p) {return p.is_null()?json(nullptr):p.at("id");}
void buffer(const json& response,kn_buffer** output) {
    auto text=response.dump(); if(text.size()>128*1024*1024) throw std::length_error("response");
    auto value=std::make_unique<kn_buffer>(); value->data=std::make_shared<const std::vector<uint8_t>>(text.begin(),text.end()); *output=value.release();
}
json prepare(kn_editor_session& session,const json& request) {
    using namespace kn_editor; auto& state=session.state; const auto revision=state.revision;
    session.pending.reset();
    const auto op=request.at("action").get<std::string>();
    auto expected=request.value("expectedRevision",json(nullptr));
    if(!expected.is_null()&&integer(expected)!=revision) return fail(revision,"REVISION_CONFLICT",op=="replace"?"Project changed while loading.":op=="execute"?"Query the latest project before editing.":"Query the latest project before changing history.");
    json candidate=*state.project;
    bool dry=request.value("dryRun",false);
    if(op=="execute") {
        const auto& commands=request.at("commands");
        if(!commands.is_array()||commands.empty()||commands.size()>10000) return fail(revision,"INVALID_BATCH","Provide 1–10000 commands.");
        for(const auto& command:commands) {
            if(command.is_null()) return fail(revision,"INVALID_COMMAND","Command cannot be null.");
            candidate=apply(std::move(candidate),command); auto errors=validate(candidate); if(!errors.empty()) return result(revision,errors);
        }
        if(dry) return result(revision,json::array());
    }
    else if(op=="replace") {candidate=request.at("project");auto errors=candidate.is_null()?json::array():validate(candidate);if(!errors.empty()) return result(revision,errors);}
    else if(op=="undo"||op=="redo") {auto& from=op=="undo"?state.undo:state.redo;if(from.empty()) return fail(revision,"HISTORY_EMPTY","No history entry available.");candidate=state.project->patch(op=="undo"?from.back()->backward:from.back()->forward);}
    else return fail(revision,"INVALID_BATCH","Batch is required.");
    if(revision==INT64_MAX) return fail(revision,"REVISION_OVERFLOW","Start a new session.");
    if(session.transaction==INT64_MAX) return fail(revision,"REVISION_OVERFLOW","Start a new session.");
    auto next=std::make_unique<editor_state>(state);
    bool changed=op=="replace"||identity(candidate)!=identity(*state.project);
    if(changed) { if(state.document_generation==INT64_MAX) return fail(revision,"REVISION_OVERFLOW","Start a new session."); ++next->document_generation; }
    if(op=="replace") {next->undo.clear();next->redo.clear();}
    // Keep only reversible deltas in history. Unchanged captions, recipes and media
    // are not retained once per revision. Preparation still owns a full candidate;
    // neither the candidate nor history becomes visible until the commit swap.
    else if(op=="execute") {auto entry=std::make_shared<const history_entry>(history_entry{json::diff(candidate,*state.project),json::diff(*state.project,candidate)});push(next->undo,entry,session.history_limit);next->redo.clear();}
    else {auto& from=op=="undo"?next->undo:next->redo;auto& to=op=="undo"?next->redo:next->undo;push(to,from.back(),session.history_limit);from.pop_back();}
    next->project=std::make_shared<const json>(std::move(candidate));next->revision=revision+1;
    auto response=result(revision,json::array());response["transactionId"]=++session.transaction;response["documentChanged"]=changed;session.pending=std::move(next);return response;
}
}
int32_t KN_CALL kn_editor_create(int32_t history_limit,kn_editor_session** output) noexcept {
    if(!output) return KN_INVALID_ARGUMENT;
    *output=nullptr;if(history_limit<1) return KN_INVALID_ARGUMENT;
    try {*output=new kn_editor_session(history_limit);return KN_OK;}catch(const std::bad_alloc&){return KN_OUT_OF_MEMORY;}catch(...){return KN_INTERNAL_ERROR;}
}
void KN_CALL kn_editor_destroy(kn_editor_session* session) noexcept {delete session;}
int32_t KN_CALL kn_editor_request(kn_editor_session* session,const uint8_t* data,uint32_t size,kn_buffer** output) noexcept {
    if(!output) return KN_INVALID_ARGUMENT;
    *output=nullptr;
    if(!data||size==0||size>64*1024*1024) return KN_INVALID_ARGUMENT;
    try {
        const auto request=kn_editor::parse(std::string(reinterpret_cast<const char*>(data),size));
        auto action=request.at("action").get<std::string>();
        if(action=="project") {
            json candidate=request.at("project"),errors=json::array();
            try {for(const auto& command:request.at("commands")) {candidate=kn_editor::apply(std::move(candidate),command);errors=kn_editor::validate(candidate);if(!errors.empty())break;}}
            catch(const kn_editor::rejected& e){errors.push_back(e.diagnostic);}catch(const std::overflow_error&){errors.push_back(kn_editor::error("TIME_OVERFLOW","Time arithmetic exceeds the supported integer range."));}
            buffer({{"value",errors.empty()?candidate:json(nullptr)},{"diagnostics",errors}},output);return KN_OK;
        }
        if(action=="validate") {buffer({{"diagnostics",kn_editor::validate(request.at("project"))}},output);return KN_OK;}
        if(action=="serialize"||action=="deserialize") {
            json value=nullptr,errors=json::array();
            try {
                if(action=="serialize") {errors=kn_editor::validate(request.at("project"));if(errors.empty()) {auto text=kn_editor::encode_file(request.at("project")).dump(2);if(text.size()>16*1024*1024) errors.push_back(kn_editor::error("PROJECT_TOO_LARGE","Project exceeds the 16 MiB foundation file limit."));else value=std::move(text);}}
                else {const auto& input=request.at("text");if(input.is_null()) errors.push_back(kn_editor::error("INVALID_PROJECT_FILE","JSON is required."));else {auto text=input.get<std::string>();if(text.size()>16*1024*1024) errors.push_back(kn_editor::error("PROJECT_TOO_LARGE","Project exceeds the 16 MiB foundation file limit."));else {auto p=kn_editor::decode_file(text);errors=kn_editor::validate(p);if(errors.empty())value=std::move(p);}}}
            } catch(const kn_editor::rejected& e) {errors.push_back(e.diagnostic);} catch(const std::bad_alloc&) {throw;} catch(...) {errors.push_back(kn_editor::error("INVALID_PROJECT_FILE","Malformed, missing, duplicate or unsupported v1 fields."));}
            buffer({{"value",value},{"diagnostics",errors}},output);return KN_OK;
        }
        if(!session) return KN_INVALID_ARGUMENT;
        std::lock_guard<std::mutex> lock(session->gate);auto& state=session->state;
        if(action=="get") {buffer({{"revision",state.revision},{"project",*state.project},{"canUndo",!state.undo.empty()},{"canRedo",!state.redo.empty()},{"documentGeneration",state.document_generation}},output);return KN_OK;}
        if(action=="abort") {session->pending.reset();buffer(result(state.revision,json::array()),output);return KN_OK;}
        if(action=="commit") {
            if(!session->pending||kn_editor::integer(request.at("transactionId"))!=session->transaction) {buffer(fail(state.revision,"REVISION_CONFLICT","Prepared transaction is no longer current."),output);return KN_OK;}
            // Allocate the entire response before the allocation-free state swap.
            buffer(result(session->pending->revision,json::array()),output);
            std::swap(state,*session->pending);session->pending.reset();return KN_OK;
        }
        json response;
        try {response=prepare(*session,request);} catch(const kn_editor::rejected& e){response=result(state.revision,json::array({e.diagnostic}));}catch(const std::overflow_error&){response=fail(state.revision,"TIME_OVERFLOW","Time arithmetic exceeds the supported integer range.");}
        buffer(response,output);return KN_OK;
    }catch(const std::bad_alloc&){return KN_OUT_OF_MEMORY;}catch(...){return KN_INVALID_ARGUMENT;}
}
