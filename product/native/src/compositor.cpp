#include "kachinco_native.h"
#include <algorithm>
#include <cmath>
#include <cstring>
namespace {
bool unit(double x) noexcept { return std::isfinite(x) && x >= 0 && x <= 1; }
bool valid(kn_rgba c) noexcept { return unit(c.r) && unit(c.g) && unit(c.b) && unit(c.a); }
kn_rgba blend(kn_rgba cb, kn_rgba cs, int32_t mode, double opacity) noexcept {
    const double a = cs.a * opacity, b = cb.a, alpha = a + b * (1 - a);
    if (alpha == 0) return {};
    const auto channel = [=](double back, double front) {
        const double mixed = mode == 1 ? 1 - (1 - back) * (1 - front) : front;
        return ((1 - a) * b * back + a * (1 - b) * front + a * b * mixed) / alpha;
    };
    return {channel(cb.r, cs.r), channel(cb.g, cs.g), channel(cb.b, cs.b), alpha};
}
uint8_t byte(double x) noexcept { return static_cast<uint8_t>(std::clamp(std::round(x * 255), 0.0, 255.0)); }
void pixel(uint8_t* dst, const uint8_t* src, int32_t mode, double opacity) noexcept {
    const auto c = blend({dst[0]/255.0,dst[1]/255.0,dst[2]/255.0,dst[3]/255.0},
        {src[0]/255.0,src[1]/255.0,src[2]/255.0,src[3]/255.0}, mode, opacity);
    dst[0]=byte(c.r); dst[1]=byte(c.g); dst[2]=byte(c.b); dst[3]=byte(c.a);
}
}
int32_t KN_CALL kn_blend(kn_rgba backdrop, kn_rgba source, int32_t mode, double opacity, kn_rgba* output) noexcept {
    if (!output) return KN_INVALID_ARGUMENT;
    *output = {};
    if (!valid(backdrop) || !valid(source) || !unit(opacity) || mode < 0 || mode > 1) return KN_INVALID_ARGUMENT;
    *output = blend(backdrop, source, mode, opacity); return KN_OK;
}
int32_t KN_CALL kn_composite_rows(uint8_t* output, const uint8_t* source, uint32_t size, int32_t width, int32_t height,
    const kn_appearance* t, int32_t first_row, int32_t row_count) noexcept {
    if (!output || !source || !t || width <= 0 || height <= 0 ||
        static_cast<uint64_t>(width) * static_cast<uint32_t>(height) * 4 != size || size > 256*1024*1024 ||
        first_row < 0 || first_row > height || row_count < 0 || row_count > height - first_row ||
        !std::isfinite(t->x) || !std::isfinite(t->y) || !std::isfinite(t->scale_x) || !std::isfinite(t->scale_y) ||
        t->scale_x <= 0 || t->scale_y <= 0 || !std::isfinite(t->rotation) || !unit(t->opacity) || t->blend < 0 || t->blend > 1)
        return KN_INVALID_ARGUMENT;
    const bool identity = t->x == 0 && t->y == 0 && t->scale_x == 1 && t->scale_y == 1 && t->rotation == 0 && t->opacity == 1 && t->blend == 0;
    const double radians = t->rotation * 3.14159265358979323846 / 180, cosine = std::cos(radians), sine = std::sin(radians);
    for (int32_t y = first_row; y < first_row + row_count; ++y) {
        const auto row = static_cast<uint32_t>(y) * static_cast<uint32_t>(width) * 4;
        if (identity) {
            bool opaque = true;
            for (int32_t x = 0; x < width; ++x) if (source[row + x*4 + 3] != 255) { opaque = false; break; }
            if (opaque) { std::memcpy(output + row, source + row, static_cast<size_t>(width)*4); continue; }
            for (int32_t x = 0; x < width; ++x) {
                const auto at = row + static_cast<uint32_t>(x)*4;
                if (source[at+3] == 0) continue;
                if (source[at+3] == 255) std::memcpy(output+at, source+at, 4);
                else pixel(output+at, source+at, 0, 1);
            }
            continue;
        }
        for (int32_t x = 0; x < width; ++x) {
            const double px = x + 0.5 - t->x, py = y + 0.5 - t->y;
            const double sx = (cosine*px + sine*py)/t->scale_x, sy = (-sine*px + cosine*py)/t->scale_y;
            if (!(sx >= 0 && sy >= 0 && sx < width && sy < height)) continue;
            const auto src = (static_cast<uint32_t>(sy)*static_cast<uint32_t>(width)+static_cast<uint32_t>(sx))*4;
            if (source[src+3] != 0) pixel(output+row+static_cast<uint32_t>(x)*4, source+src, t->blend, t->opacity);
        }
    }
    return KN_OK;
}
int32_t KN_CALL kn_mix_add(double* mix, uint32_t mix_count, const float* source, uint32_t source_count, uint32_t offset, double gain) noexcept {
    if ((!mix && mix_count) || (!source && source_count) || offset > mix_count || source_count > mix_count-offset ||
        mix_count > 96000 || !std::isfinite(gain) || gain < 0) return KN_INVALID_ARGUMENT;
    for (uint32_t i=0; i<source_count; ++i) if (!std::isfinite(source[i])) return KN_INVALID_MEDIA;
    for (uint32_t i=0; i<source_count; ++i) mix[offset+i] += source[i]*gain;
    return KN_OK;
}
int32_t KN_CALL kn_mix_finish(const double* mix, float* output, uint32_t count) noexcept {
    if ((!mix && count) || (!output && count) || count > 96000) return KN_INVALID_ARGUMENT;
    for (uint32_t i=0; i<count; ++i) {
        if (!std::isfinite(mix[i])) return KN_INVALID_MEDIA;
        output[i]=static_cast<float>(std::clamp(mix[i], -1.0, 1.0));
    }
    return KN_OK;
}
