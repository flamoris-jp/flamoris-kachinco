#pragma once
#include "kinetic_text.hpp"
namespace kn_text {
void validate_program_v2(const json& program);
motion evaluate_program_v2(const json& clip, int64_t tick, uint32_t unit, uint32_t count,size_t* budget=nullptr);
}
