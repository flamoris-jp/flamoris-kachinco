#pragma once
#include "kinetic_text.hpp"
#include <array>
#include <vector>
namespace kn_text {
struct vector_command { char op; std::array<double,6> values{}; };
struct vector_path { std::vector<vector_command> commands; std::array<double,4> fill{}; };
struct vector_unit { double x=0,y=0; uint32_t unit=0; std::vector<vector_path> paths; };
void validate_vectors(const json& clip,size_t& command_budget);
std::vector<vector_unit> vector_layout(const json& clip);
}
