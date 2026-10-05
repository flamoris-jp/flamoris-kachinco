#include "kachinco_native.h"
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <limits>
#include <random>
#include <vector>
#define REQUIRE(x) do { if (!(x)) { std::fprintf(stderr,"Failed line %d: %s\n",__LINE__,#x); std::abort(); } } while (false)

#if defined(_WIN32)
static void assert_parity(kn_gpu_preview* gpu, const std::vector<uint8_t>& actual, const std::vector<uint8_t>& expected,
    const std::vector<uint8_t>& backdrop, const std::vector<uint8_t>& source,
    int32_t width, int32_t height, const kn_appearance& a, unsigned iteration, unsigned layer) {
    for (size_t at=0;at<actual.size();++at) {
        if (std::abs(static_cast<int>(actual[at])-static_cast<int>(expected[at]))<=1) continue;
        const size_t pixel=at/4, offset=pixel*4;
        const auto x=static_cast<int32_t>(pixel%static_cast<size_t>(width)), y=static_cast<int32_t>(pixel/static_cast<size_t>(width));
        const double radians=a.rotation*3.14159265358979323846/180, cosine=std::cos(radians), sine=std::sin(radians);
        const double px=x+0.5-a.x,py=y+0.5-a.y;
        const double sx=(cosine*px+sine*py)/a.scale_x,sy=(-sine*px+cosine*py)/a.scale_y;
        std::fprintf(stderr,"GPU parity mismatch case=%u layer=%u canvas=%dx%d pixel=(%d,%d) channel=%u\n",
            iteration,layer,width,height,x,y,static_cast<unsigned>(at%4));
        std::fprintf(stderr,"appearance x=%.17g y=%.17g sx=%.17g sy=%.17g rotation=%.17g opacity=%.17g blend=%d cos=%.17g sin=%.17g\n",
            a.x,a.y,a.scale_x,a.scale_y,a.rotation,a.opacity,a.blend,cosine,sine);
        std::fprintf(stderr,"expected=(%u,%u,%u,%u) actual=(%u,%u,%u,%u) backdrop=(%u,%u,%u,%u) sourceCoord=(%.17g,%.17g)\n",
            expected[offset],expected[offset+1],expected[offset+2],expected[offset+3],actual[offset],actual[offset+1],actual[offset+2],actual[offset+3],
            backdrop[offset],backdrop[offset+1],backdrop[offset+2],backdrop[offset+3],sx,sy);
        if (sx>=0 && sy>=0 && sx<width && sy<height) {
            const auto from=(static_cast<size_t>(sy)*static_cast<size_t>(width)+static_cast<size_t>(sx))*4;
            std::fprintf(stderr,"expected source=(%u,%u,%u,%u) index=(%u,%u)\n",source[from],source[from+1],source[from+2],source[from+3],static_cast<unsigned>(sx),static_cast<unsigned>(sy));
        }
        std::fprintf(stderr,"Compiled shader diagnostic:\n%s\n",kn_gpu_shader_diagnostics(gpu));
        std::fflush(stderr); REQUIRE(false);
    }
}
#endif

int main() {
    kn_gpu_preview* gpu = nullptr; int32_t error = 99;
    REQUIRE(kn_gpu_create(0, 0, &gpu, &error) == KN_INVALID_ARGUMENT && !gpu && error == 0);
    REQUIRE(kn_gpu_create(UINT64_C(268435457), 0, &gpu, &error) == KN_INVALID_ARGUMENT && !gpu);
    REQUIRE(kn_gpu_create(4096, 2, &gpu, &error) == KN_INVALID_ARGUMENT && !gpu);
    REQUIRE(kn_gpu_create(4096, 0, nullptr, &error) == KN_INVALID_ARGUMENT);
#if defined(_WIN32)
    // CI must compile and execute the real shader on WARP. Hardware absence never skips parity.
    const int32_t created = kn_gpu_create(UINT64_C(268435456), 1, &gpu, &error);
    if (created != KN_OK) std::fprintf(stderr,"WARP create failed: status=%d HRESULT=0x%08x %s\n",created,static_cast<unsigned>(error),kn_gpu_diagnostic());
    REQUIRE(created == KN_OK && gpu);
    REQUIRE(kn_gpu_allocated_bytes(gpu) == 80);
    REQUIRE(kn_gpu_begin(gpu, 0, 1, &error) == KN_INVALID_ARGUMENT);
    REQUIRE(kn_gpu_begin(gpu, 16385, 1, &error) == KN_INVALID_ARGUMENT);
    REQUIRE(kn_gpu_begin(gpu, 8192, 8192, &error) == KN_GPU_BUDGET);
    {
        REQUIRE(kn_gpu_begin(gpu,6,5,&error)==KN_OK);
        std::vector<uint8_t> source(6*5*4),backdrop(source.size(),0),expected,actual(source.size());
        for(size_t at=0;at<source.size();at+=4) {
            source[at]=static_cast<uint8_t>((at/4)%6*40);source[at+1]=static_cast<uint8_t>((at/4)/6*50);
            source[at+2]=static_cast<uint8_t>(at/4+1);source[at+3]=255;backdrop[at+3]=255;
        }
        expected=source;kn_appearance identity{0,0,1,1,0,1,0,0};
        REQUIRE(kn_gpu_composite(gpu,source.data(),static_cast<uint32_t>(source.size()),&identity,&error)==KN_OK);
        REQUIRE(kn_gpu_read(gpu,actual.data(),static_cast<uint32_t>(actual.size()),&error)==KN_OK);
        assert_parity(gpu,actual,expected,backdrop,source,6,5,identity,999,0);
    }
    std::mt19937 random(67001);
    for (unsigned iteration = 0; iteration < 80; ++iteration) {
        const int32_t width = 9 + static_cast<int32_t>(random() % 29), height = 5 + static_cast<int32_t>(random() % 23);
        const uint32_t size = static_cast<uint32_t>(width * height * 4);
        REQUIRE(kn_gpu_begin(gpu, width, height, &error) == KN_OK);
        REQUIRE(kn_gpu_allocated_bytes(gpu) == static_cast<uint64_t>(size) * 4 + 80);
        std::vector<uint8_t> expected(size, 0), actual(size, 0), source(size);
        for (uint32_t at = 3; at < size; at += 4) expected[at] = 255;
        // Begin is opaque black, including odd sizes at the edge of a dispatch group.
        REQUIRE(kn_gpu_read(gpu, actual.data(), size, &error) == KN_OK && actual == expected);
        for (unsigned layer = 0; layer < 4; ++layer) {
            for (auto& value : source) value = static_cast<uint8_t>(random() & 255);
            if (layer == 0) for (uint32_t at = 3; at < size; at += 4) source[at] = 0;
            kn_appearance a{0,0,1,1,0,1,0,0};
            if (layer == 1) a = {0.5, -0.5, 1, 1, 0, 0.5, 1, 0}; // Exact integer sampling boundaries.
            if (layer == 2) a = {static_cast<double>(width), 0, 1, 1, 90, 0.75, 0, 0};
            if (layer == 3) a = {-2.125, 1.75, 1.25, 0.75, 13.5, 0.63, 1, 0};
            const auto backdrop=expected;
            REQUIRE(kn_composite_rows(expected.data(), source.data(), size, width, height, &a, 0, height) == KN_OK);
            REQUIRE(kn_gpu_composite(gpu, source.data(), size, &a, &error) == KN_OK);
            REQUIRE(kn_gpu_read(gpu, actual.data(), size, &error) == KN_OK);
            // CPU and shader division are correctly rounded to <=0.5 ULP; permit 1 byte
            // at final SDR quantization, never changed nearest source selection.
            assert_parity(gpu,actual,expected,backdrop,source,width,height,a,iteration,layer);
        }
        kn_appearance invalid{0,0,0,1,0,1,0,0};
        REQUIRE(kn_gpu_composite(gpu, source.data(), size, &invalid, &error) == KN_INVALID_ARGUMENT);
        invalid.scale_x = 1; invalid.opacity = std::numeric_limits<double>::quiet_NaN();
        REQUIRE(kn_gpu_composite(gpu, source.data(), size, &invalid, &error) == KN_INVALID_ARGUMENT);
        REQUIRE(kn_gpu_read(gpu, actual.data(), size - 1, &error) == KN_INVALID_ARGUMENT);
        REQUIRE(kn_gpu_reset(gpu, &error) == KN_OK && kn_gpu_allocated_bytes(gpu) == 80);
        REQUIRE(kn_gpu_read(gpu, actual.data(), size, &error) == KN_INVALID_ARGUMENT);
    }
    kn_gpu_destroy(gpu); gpu = nullptr;
    REQUIRE(kn_gpu_create(96, 1, &gpu, &error) == KN_OK);
    REQUIRE(kn_gpu_begin(gpu, 1, 1, &error) == KN_OK && kn_gpu_allocated_bytes(gpu) == 96);
    REQUIRE(kn_gpu_begin(gpu, 2, 1, &error) == KN_GPU_BUDGET && kn_gpu_allocated_bytes(gpu) == 96);
    REQUIRE(kn_gpu_reset(gpu, &error) == KN_OK);
    kn_gpu_destroy(gpu);
    REQUIRE(kn_gpu_create(4096, 1, &gpu, &error) == KN_OK);
    for (int32_t width=1;width<=8;++width) {
        REQUIRE(kn_gpu_begin(gpu,width,1,&error)==KN_OK);
        std::vector<uint8_t> source(static_cast<size_t>(width)*4,255);
        kn_appearance a{0,0,1,1,0,1,0,0};
        REQUIRE(kn_gpu_composite(gpu,source.data(),static_cast<uint32_t>(source.size()),&a,&error)==KN_OK);
        // Cancellation omits Read. Reset must finish submitted commands before
        // releasing this payload and accepting a differently sized next canvas.
        REQUIRE(kn_gpu_reset(gpu,&error)==KN_OK && kn_gpu_allocated_bytes(gpu)==80);
    }
    kn_gpu_destroy(gpu);
    std::puts("D3D11 WARP shader parity, resource budget and reset contracts passed.");
#else
    REQUIRE(kn_gpu_create(4096, 0, &gpu, &error) == KN_GPU_UNSUPPORTED && !gpu && error == 0);
    REQUIRE(kn_gpu_create(4096, 1, &gpu, &error) == KN_GPU_UNSUPPORTED && !gpu);
    REQUIRE(kn_gpu_allocated_bytes(nullptr) == 0);
    std::puts("Optional GPU adapter rejects unsupported Linux host deterministically.");
#endif
    kn_gpu_destroy(nullptr);
}
