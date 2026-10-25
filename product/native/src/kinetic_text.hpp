#pragma once
#include "editor_domain.hpp"
namespace kn_text {
using kn_editor::json;
void validate(const json& document);
json initial();
struct motion { double x=0,y=0,scale=1,rotation=0,opacity=1; };
motion evaluate(const json& clip,int64_t tick,uint32_t unit,uint32_t count=1,size_t* budget=nullptr);
}
