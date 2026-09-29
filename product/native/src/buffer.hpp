#pragma once
#include <cstdint>
#include <memory>
#include <vector>
struct kn_buffer { std::shared_ptr<const std::vector<uint8_t>> data; };
