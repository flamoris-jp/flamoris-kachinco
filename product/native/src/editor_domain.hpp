#pragma once
#include "../third_party/nlohmann/json.hpp"
#include <cstdint>
#include <string>
namespace kn_editor {
using json = nlohmann::ordered_json;
struct rejected { json diagnostic; };
json error(const std::string& code, const std::string& message, const json& id = nullptr, const json& path = nullptr);
json default_caption_style();
bool valid_caption_style(const json& style);
json validate(const json& project);
json apply(json project, const json& command);
json encode_file(const json& project);
json decode_file(const std::string& text);
json parse(const std::string& text, bool reject_duplicates = true);
int64_t integer(const json& value);
int64_t add(int64_t a, int64_t b);
std::string guid(const json& value);
int rate(const json& clip);
int64_t source_duration(const json& clip);
int64_t retime(int64_t tick, int old_rate, int new_rate);
bool valid_range(int64_t start, int64_t duration, int64_t limit = INT64_MAX);
}
