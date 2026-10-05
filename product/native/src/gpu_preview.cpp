#include "kachinco_native.h"
#include <cmath>
#include <cstring>
#include <new>
#include <chrono>
#include <thread>
#include <cstddef>
#include <cstdio>
#include <utility>

namespace {
constexpr uint64_t maximum_budget = UINT64_C(256) * 1024 * 1024;
constexpr uint64_t constant_bytes = 80;
thread_local char diagnostic[512] = {};
void clear_error(int32_t* error) noexcept { if (error) *error = 0; diagnostic[0] = '\0'; }
void detail(const char* value, size_t count) noexcept {
    const size_t copied = count < sizeof(diagnostic) - 1 ? count : sizeof(diagnostic) - 1;
    if (copied) std::memcpy(diagnostic, value, copied);
    diagnostic[copied] = '\0';
}
bool valid_appearance(const kn_appearance* a) noexcept {
    return a && std::isfinite(a->x) && std::isfinite(a->y) && std::isfinite(a->scale_x) &&
        std::isfinite(a->scale_y) && a->scale_x > 0 && a->scale_y > 0 && std::isfinite(a->rotation) &&
        std::isfinite(a->opacity) && a->opacity >= 0 && a->opacity <= 1 && a->blend >= 0 && a->blend <= 1;
}
}
const char* KN_CALL kn_gpu_diagnostic() noexcept { return diagnostic; }

#if defined(_WIN32)
#define NOMINMAX
#include <d3d11.h>
#include <d3dcompiler.h>
#include <d3d11shader.h>
#include <wrl/client.h>
using Microsoft::WRL::ComPtr;

struct kn_gpu_preview {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11ComputeShader> shader;
    ComPtr<ID3DBlob> shader_diagnostics;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Query> completion;
    ComPtr<ID3D11Texture2D> upload, outputs[2], staging;
    ComPtr<ID3D11ShaderResourceView> source_view, output_views[2];
    ComPtr<ID3D11UnorderedAccessView> target_views[2];
    uint64_t budget = 0, allocated = constant_bytes;
    int32_t width = 0, height = 0;
    uint32_t size = 0;
    unsigned current = 0;
    bool submitted = false, poisoned = false, force_warp = false;
};
namespace {
/* Native computes trig once with the CPU compositor's formula. Shader double division
   and precise arithmetic preserve inverse nearest sampling at integer boundaries.
   UINT textures preserve byte values; explicit +0.5 truncation matches std::round.
   Only repository-authored shader source is compiled: no AI source execution here. */
constexpr char shader_source[] = R"hlsl(
Texture2D<uint4> Source : register(t0);
Texture2D<uint4> Backdrop : register(t1);
RWTexture2D<uint4> Target : register(u0);
cbuffer Parameters : register(b0) {
    double X; double Y;
    double ScaleX; double ScaleY;
    double Cosine; double Sine;
    double Opacity; uint Width; uint Height;
    uint Mode; uint Reserved; uint Padding0; uint Padding1;
};
uint quantize(double value) {
    precise double scaled = value * (double)255 + (double)0.5;
    if (scaled < 0) return 0;
    if (scaled > 255) return 255;
    return (uint)scaled;
}
uint blend_channel(uint backByte, uint frontByte, double a, double b, double alpha) {
    precise double back = (double)backByte / (double)255;
    precise double front = (double)frontByte / (double)255;
    precise double mixed = front;
    // Explicit uniform branching and scalar output avoid FXC's double ternary /
    // indexed-vector lowering, which corrupted R/B in the forced-WARP contracts.
    [branch] if (Mode == 1) mixed = (double)1 - ((double)1 - back) * ((double)1 - front);
    precise double value = (((double)1 - a) * b * back + a * ((double)1 - b) * front + a * b * mixed) / alpha;
    return quantize(value);
}
[numthreads(8,8,1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (id.x >= Width || id.y >= Height) return;
    uint4 backBytes = Backdrop.Load(int3(id.xy,0));
    Target[id.xy] = backBytes;
    precise double px = (double)id.x + (double)0.5 - X;
    precise double py = (double)id.y + (double)0.5 - Y;
    precise double sx = (Cosine * px + Sine * py) / ScaleX;
    precise double sy = (-Sine * px + Cosine * py) / ScaleY;
    if (!(sx >= 0 && sy >= 0 && sx < (double)Width && sy < (double)Height)) return;
    uint4 frontBytes = Source.Load(int3((uint)sx,(uint)sy,0));
    if (frontBytes.a == 0) return;
    precise double a = (double)frontBytes.a / (double)255 * Opacity;
    precise double b = (double)backBytes.a / (double)255;
    precise double alpha = a + b * ((double)1 - a);
    if (alpha == 0) { Target[id.xy] = uint4(0,0,0,0); return; }
    uint red = blend_channel(backBytes.r, frontBytes.r, a, b, alpha);
    uint green = blend_channel(backBytes.g, frontBytes.g, a, b, alpha);
    uint blue = blend_channel(backBytes.b, frontBytes.b, a, b, alpha);
    uint alphaByte = quantize(alpha);
    Target[id.xy] = uint4(red, green, blue, alphaByte);
}
)hlsl";
struct parameters {
    double x, y, scale_x, scale_y, cosine, sine, opacity;
    uint32_t width, height, mode, reserved, padding0, padding1;
};
static_assert(sizeof(parameters) == constant_bytes, "HLSL constant packing is ABI");

HRESULT validate_constants(ID3DBlob* compiled) noexcept {
    ComPtr<ID3D11ShaderReflection> reflection;
    HRESULT hr = D3DReflect(compiled->GetBufferPointer(), compiled->GetBufferSize(), __uuidof(ID3D11ShaderReflection), reinterpret_cast<void**>(reflection.GetAddressOf()));
    if (FAILED(hr)) return hr;
    auto buffer = reflection->GetConstantBufferByName("Parameters");
    D3D11_SHADER_BUFFER_DESC buffer_desc{};
    hr = buffer->GetDesc(&buffer_desc);
    if (FAILED(hr)) return hr;
    if (buffer_desc.Size != constant_bytes) {
        std::snprintf(diagnostic, sizeof(diagnostic), "Shader constant payload is %u bytes, expected %u.", buffer_desc.Size, static_cast<unsigned>(constant_bytes));
        return E_UNEXPECTED;
    }
    const char* names[] = {"X", "Y", "ScaleX", "ScaleY", "Cosine", "Sine", "Opacity", "Width", "Height", "Mode"};
    const UINT offsets[] = {0, 8, 16, 24, 32, 40, 48, 56, 60, 64};
    for (unsigned i=0;i<10;++i) {
        D3D11_SHADER_VARIABLE_DESC desc{};
        hr = buffer->GetVariableByName(names[i])->GetDesc(&desc);
        if (FAILED(hr)) return hr;
        if (desc.StartOffset != offsets[i]) {
            std::snprintf(diagnostic, sizeof(diagnostic), "Shader constant %s offset is %u, expected %u.", names[i], desc.StartOffset, offsets[i]);
            return E_UNEXPECTED;
        }
    }
    return S_OK;
}
HRESULT compile_shader(kn_gpu_preview* p, UINT flags) noexcept {
    ComPtr<ID3DBlob> compiled, errors, disassembled;
    HRESULT hr = D3DCompile(shader_source, sizeof(shader_source) - 1, "Kachinco.GpuPreview", nullptr, nullptr,
        "main", "cs_5_0", D3DCOMPILE_IEEE_STRICTNESS | flags, 0, &compiled, &errors);
    if (FAILED(hr) && errors) detail(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize());
    if (SUCCEEDED(hr)) hr = validate_constants(compiled.Get());
    if (FAILED(hr)) return hr;
    (void)D3DDisassemble(compiled->GetBufferPointer(), compiled->GetBufferSize(), 0, nullptr, &disassembled);
    // Diagnostics are compiler/device bookkeeping, bounded independently of texture
    // payload. Only one current disassembly is retained, including diagnostic mode.
    if (disassembled && disassembled->GetBufferSize() > 128 * 1024) disassembled.Reset();
    ComPtr<ID3D11ComputeShader> shader;
    hr = p->device->CreateComputeShader(compiled->GetBufferPointer(), compiled->GetBufferSize(), nullptr, &shader);
    if (SUCCEEDED(hr)) {
        p->context->CSSetShader(nullptr, nullptr, 0);
        p->shader = std::move(shader); p->shader_diagnostics = std::move(disassembled);
    }
    return hr;
}

int32_t failure(HRESULT hr, int32_t* error) noexcept {
    if (error) *error = static_cast<int32_t>(hr);
    return hr == E_OUTOFMEMORY ? KN_OUT_OF_MEMORY : KN_GPU_FAILURE;
}
int32_t health(kn_gpu_preview* p, int32_t* error) noexcept {
    if (p->poisoned) return KN_TIMEOUT;
    const auto hr = p->device->GetDeviceRemovedReason();
    return FAILED(hr) ? failure(hr, error) : KN_OK;
}
int32_t drain(kn_gpu_preview* p, int32_t* error) noexcept {
    if (!p->submitted) return health(p, error);
    // A cancelled frame may not have reached Read/Map. Drain its commands before
    // reallocating textures so old driver-retained payload cannot pile up on seeks.
    p->context->End(p->completion.Get()); p->context->Flush();
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    for (;;) {
        const HRESULT hr = p->context->GetData(p->completion.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (hr == S_OK) { p->submitted = false; return health(p, error); }
        if (FAILED(hr)) return failure(hr, error);
        int32_t status = health(p, error); if (status != KN_OK) return status;
        if (std::chrono::steady_clock::now() >= deadline) { p->poisoned = true; return KN_TIMEOUT; }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
}
void unbind(kn_gpu_preview* p) noexcept {
    ID3D11ShaderResourceView* empty_sources[2] = {};
    ID3D11UnorderedAccessView* empty_target = nullptr;
    p->context->CSSetShaderResources(0, 2, empty_sources);
    p->context->CSSetUnorderedAccessViews(0, 1, &empty_target, nullptr);
}
void reset(kn_gpu_preview* p) noexcept {
    unbind(p);
    p->source_view.Reset(); p->upload.Reset(); p->staging.Reset();
    for (unsigned i = 0; i < 2; ++i) {
        p->output_views[i].Reset(); p->target_views[i].Reset(); p->outputs[i].Reset();
    }
    p->width = p->height = 0; p->size = 0; p->current = 0; p->allocated = constant_bytes;
}
}

int32_t KN_CALL kn_gpu_create(uint64_t budget, int32_t force_warp, kn_gpu_preview** output, int32_t* error) noexcept {
    clear_error(error);
    if (!output) return KN_INVALID_ARGUMENT;
    *output = nullptr;
    if (budget < constant_bytes + 16 || budget > maximum_budget || (force_warp != 0 && force_warp != 1)) return KN_INVALID_ARGUMENT;
    kn_gpu_preview* p = new (std::nothrow) kn_gpu_preview;
    if (!p) return KN_OUT_OF_MEMORY;
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0};
    HRESULT hr = D3D11CreateDevice(nullptr, force_warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE,
        nullptr, 0, levels, 1, D3D11_SDK_VERSION, &p->device, nullptr, &p->context);
    if (FAILED(hr)) { detail("D3D11CreateDevice failed.", 25); delete p; return failure(hr, error); }
    D3D11_FEATURE_DATA_DOUBLES doubles{};
    D3D11_FEATURE_DATA_D3D11_OPTIONS options{};
    const bool supports_double = SUCCEEDED(p->device->CheckFeatureSupport(D3D11_FEATURE_DOUBLES, &doubles, static_cast<UINT>(sizeof(doubles)))) && doubles.DoublePrecisionFloatShaderOps;
    const bool supports_division = SUCCEEDED(p->device->CheckFeatureSupport(D3D11_FEATURE_D3D11_OPTIONS, &options, static_cast<UINT>(sizeof(options)))) && options.ExtendedDoublesShaderInstructions;
    UINT format = 0;
    hr = p->device->CheckFormatSupport(DXGI_FORMAT_R8G8B8A8_UINT, &format);
    if (!supports_double || !supports_division || FAILED(hr) ||
        (format & (D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT_SHADER_LOAD | D3D11_FORMAT_SUPPORT_TYPED_UNORDERED_ACCESS_VIEW)) !=
        (D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT_SHADER_LOAD | D3D11_FORMAT_SUPPORT_TYPED_UNORDERED_ACCESS_VIEW)) {
        delete p; return KN_GPU_UNSUPPORTED;
    }
    hr = compile_shader(p, D3DCOMPILE_OPTIMIZATION_LEVEL3);
    if (SUCCEEDED(hr)) {
        D3D11_BUFFER_DESC desc{}; desc.ByteWidth = static_cast<UINT>(constant_bytes);
        desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        hr = p->device->CreateBuffer(&desc, nullptr, &p->constants);
    }
    if (SUCCEEDED(hr)) {
        D3D11_QUERY_DESC desc{}; desc.Query = D3D11_QUERY_EVENT;
        hr = p->device->CreateQuery(&desc, &p->completion);
    }
    if (FAILED(hr)) { delete p; return failure(hr, error); }
    p->budget = budget; p->force_warp = force_warp != 0; *output = p; return KN_OK;
}
void KN_CALL kn_gpu_destroy(kn_gpu_preview* p) noexcept {
    if (p) { p->context->ClearState(); p->context->Flush(); delete p; }
}
int32_t KN_CALL kn_gpu_begin(kn_gpu_preview* p, int32_t width, int32_t height, int32_t* error) noexcept {
    clear_error(error);
    if (!p || width <= 0 || height <= 0 || width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION || height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION) return KN_INVALID_ARGUMENT;
    const uint64_t size = static_cast<uint64_t>(width) * static_cast<uint32_t>(height) * 4;
    if (size * 4 + constant_bytes > p->budget) return KN_GPU_BUDGET;
    int32_t status = health(p, error); if (status != KN_OK) return status;
    if (p->width != width || p->height != height) {
        status = drain(p, error); if (status != KN_OK) return status;
        reset(p); // Release the previous canvas before allocating a different one.
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(width); desc.Height = static_cast<UINT>(height);
        desc.MipLevels = desc.ArraySize = 1; desc.Format = DXGI_FORMAT_R8G8B8A8_UINT;
        desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr = p->device->CreateTexture2D(&desc, nullptr, &p->upload);
        if (SUCCEEDED(hr)) hr = p->device->CreateShaderResourceView(p->upload.Get(), nullptr, &p->source_view);
        desc.BindFlags |= D3D11_BIND_UNORDERED_ACCESS;
        for (unsigned i = 0; i < 2 && SUCCEEDED(hr); ++i) {
            hr = p->device->CreateTexture2D(&desc, nullptr, &p->outputs[i]);
            if (SUCCEEDED(hr)) hr = p->device->CreateShaderResourceView(p->outputs[i].Get(), nullptr, &p->output_views[i]);
            if (SUCCEEDED(hr)) hr = p->device->CreateUnorderedAccessView(p->outputs[i].Get(), nullptr, &p->target_views[i]);
        }
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (SUCCEEDED(hr)) hr = p->device->CreateTexture2D(&desc, nullptr, &p->staging);
        if (FAILED(hr)) { reset(p); return failure(hr, error); }
        p->width = width; p->height = height; p->size = static_cast<uint32_t>(size); p->allocated = size * 4 + constant_bytes;
    }
    unbind(p); p->current = 0;
    const UINT black[4] = {0, 0, 0, 255};
    p->context->ClearUnorderedAccessViewUint(p->target_views[0].Get(), black);
    p->submitted = true;
    return health(p, error);
}
int32_t KN_CALL kn_gpu_composite(kn_gpu_preview* p, const uint8_t* source, uint32_t size, const kn_appearance* a, int32_t* error) noexcept {
    clear_error(error);
    if (!p || !p->size || !source || size != p->size || !valid_appearance(a)) return KN_INVALID_ARGUMENT;
    const double radians = a->rotation * 3.14159265358979323846 / 180;
    if (!std::isfinite(radians)) return KN_INVALID_ARGUMENT;
    int32_t status = health(p, error); if (status != KN_OK) return status;
    parameters values{a->x, a->y, a->scale_x, a->scale_y, std::cos(radians), std::sin(radians), a->opacity,
        static_cast<uint32_t>(p->width), static_cast<uint32_t>(p->height), static_cast<uint32_t>(a->blend), 0, 0, 0};
    p->context->UpdateSubresource(p->upload.Get(), 0, nullptr, source, static_cast<UINT>(p->width) * 4, 0);
    p->context->UpdateSubresource(p->constants.Get(), 0, nullptr, &values, 0, 0);
    ID3D11ShaderResourceView* views[] = {p->source_view.Get(), p->output_views[p->current].Get()};
    const unsigned next = 1 - p->current;
    auto target = p->target_views[next].Get(); auto constants = p->constants.Get();
    p->context->CSSetShader(p->shader.Get(), nullptr, 0);
    p->context->CSSetConstantBuffers(0, 1, &constants);
    p->context->CSSetShaderResources(0, 2, views);
    p->context->CSSetUnorderedAccessViews(0, 1, &target, nullptr);
    p->context->Dispatch((static_cast<UINT>(p->width) + 7) / 8, (static_cast<UINT>(p->height) + 7) / 8, 1);
    unbind(p); p->current = next;
    p->submitted = true;
    return health(p, error);
}
int32_t KN_CALL kn_gpu_read(kn_gpu_preview* p, uint8_t* output, uint32_t size, int32_t* error) noexcept {
    clear_error(error);
    if (!p || !p->size || !output || size != p->size) return KN_INVALID_ARGUMENT;
    int32_t status = health(p, error); if (status != KN_OK) return status;
    p->context->CopyResource(p->staging.Get(), p->outputs[p->current].Get());
    p->submitted = true;
    status = drain(p, error); if (status != KN_OK) return status;
    D3D11_MAPPED_SUBRESOURCE mapped{};
    // The completion event includes CopyResource. A nonblocking map keeps a hung
    // driver from holding the frame lease after the explicit two-second deadline.
    const HRESULT hr = p->context->Map(p->staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
    if (FAILED(hr)) return failure(hr, error);
    for (int32_t row = 0; row < p->height; ++row)
        std::memcpy(output + static_cast<size_t>(row) * static_cast<size_t>(p->width) * 4,
            static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(row) * mapped.RowPitch, static_cast<size_t>(p->width) * 4);
    p->context->Unmap(p->staging.Get(), 0);
    p->submitted = false;
    return health(p, error);
}
int32_t KN_CALL kn_gpu_reset(kn_gpu_preview* p, int32_t* error) noexcept {
    clear_error(error); if (!p) return KN_INVALID_ARGUMENT;
    const auto status = drain(p, error); if (status != KN_OK) return status;
    reset(p); return health(p, error);
}
uint64_t KN_CALL kn_gpu_allocated_bytes(const kn_gpu_preview* p) noexcept { return p ? p->allocated : 0; }
const char* KN_CALL kn_gpu_shader_diagnostics(const kn_gpu_preview* p) noexcept {
    return p && p->shader_diagnostics ? static_cast<const char*>(p->shader_diagnostics->GetBufferPointer()) : "Shader diagnostic unavailable.";
}
int32_t KN_CALL kn_gpu_diagnostic_without_optimization(kn_gpu_preview* p, int32_t* error) noexcept {
    clear_error(error); if (!p || !p->force_warp) return KN_INVALID_ARGUMENT;
    const auto status = drain(p, error); if (status != KN_OK) return status;
    const auto hr = compile_shader(p, D3DCOMPILE_SKIP_OPTIMIZATION);
    return FAILED(hr) ? failure(hr, error) : KN_OK;
}
#else
struct kn_gpu_preview {};
int32_t KN_CALL kn_gpu_create(uint64_t budget, int32_t force_warp, kn_gpu_preview** output, int32_t* error) noexcept {
    clear_error(error); if (!output) return KN_INVALID_ARGUMENT; *output = nullptr;
    if (budget < constant_bytes + 16 || budget > maximum_budget || (force_warp != 0 && force_warp != 1)) return KN_INVALID_ARGUMENT;
    constexpr char unsupported[] = "D3D11 preview composition requires Windows.";
    detail(unsupported, sizeof(unsupported) - 1);
    return KN_GPU_UNSUPPORTED;
}
void KN_CALL kn_gpu_destroy(kn_gpu_preview*) noexcept {}
int32_t KN_CALL kn_gpu_begin(kn_gpu_preview*, int32_t, int32_t, int32_t* error) noexcept { clear_error(error); return KN_GPU_UNSUPPORTED; }
int32_t KN_CALL kn_gpu_composite(kn_gpu_preview*, const uint8_t*, uint32_t, const kn_appearance* appearance, int32_t* error) noexcept {
    clear_error(error); if (!valid_appearance(appearance)) return KN_INVALID_ARGUMENT; return KN_GPU_UNSUPPORTED;
}
int32_t KN_CALL kn_gpu_read(kn_gpu_preview*, uint8_t*, uint32_t, int32_t* error) noexcept { clear_error(error); return KN_GPU_UNSUPPORTED; }
int32_t KN_CALL kn_gpu_reset(kn_gpu_preview*, int32_t* error) noexcept { clear_error(error); return KN_GPU_UNSUPPORTED; }
uint64_t KN_CALL kn_gpu_allocated_bytes(const kn_gpu_preview*) noexcept { return 0; }
const char* KN_CALL kn_gpu_shader_diagnostics(const kn_gpu_preview*) noexcept { return "D3D11 is unavailable on this host."; }
int32_t KN_CALL kn_gpu_diagnostic_without_optimization(kn_gpu_preview*, int32_t* error) noexcept { clear_error(error); return KN_GPU_UNSUPPORTED; }
#endif
