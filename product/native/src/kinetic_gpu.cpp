#include "kinetic_text.hpp"
#include "kachinco_native.h"
#include "buffer.hpp"
#include <memory>
#include <mutex>
#include <vector>
#include <string>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <d2d1_1helper.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <map>
#include <set>
#include <algorithm>
using Microsoft::WRL::ComPtr;
namespace {
void check(HRESULT hr) {if(FAILED(hr)) throw hr;}
std::wstring wide(const std::string& s) {int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),nullptr,0);if(n<=0)throw std::invalid_argument("UTF-8 text");std::wstring w(static_cast<size_t>(n),L'\0');MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),static_cast<int>(s.size()),w.data(),n);return w;}
std::string utf8(const wchar_t* w) {int n=WideCharToMultiByte(CP_UTF8,0,w,-1,nullptr,0,nullptr,nullptr);if(n<=1)return "";std::string s(static_cast<size_t>(n),0);WideCharToMultiByte(CP_UTF8,0,w,-1,s.data(),n,nullptr,nullptr);s.pop_back();return s;}
struct shape {ComPtr<ID2D1PathGeometry> geometry;float x,y;uint32_t unit;};
struct layout {std::vector<shape> shapes;std::vector<uint32_t> starts;std::set<std::string> faces;std::string warning;};
class collector final:public IDWriteTextRenderer {
    ULONG refs=1;ID2D1Factory1* factory;IDWriteFactory* write;layout& out;
public:
    collector(ID2D1Factory1* f,IDWriteFactory* w,layout& o):factory(f),write(w),out(o){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** p) override {if(!p)return E_POINTER;*p=nullptr;if(id==__uuidof(IUnknown)||id==__uuidof(IDWritePixelSnapping)||id==__uuidof(IDWriteTextRenderer)){*p=static_cast<IDWriteTextRenderer*>(this);AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {auto n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*,BOOL* disabled) override {if(!disabled)return E_POINTER;*disabled=TRUE;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*,DWRITE_MATRIX* m) override {if(!m)return E_POINTER;*m={1,0,0,1,0,0};return S_OK;}
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*,FLOAT* p) override {if(!p)return E_POINTER;*p=1;return S_OK;}
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*,FLOAT x,FLOAT y,DWRITE_MEASURING_MODE,const DWRITE_GLYPH_RUN* run,const DWRITE_GLYPH_RUN_DESCRIPTION* desc,IUnknown*) override {
        try {
            ComPtr<IDWriteFontCollection> collection;ComPtr<IDWriteFont> font;ComPtr<IDWriteFontFamily> family;ComPtr<IDWriteLocalizedStrings> names;
            if(SUCCEEDED(write->GetSystemFontCollection(&collection))&&SUCCEEDED(collection->GetFontFromFontFace(run->fontFace,&font))&&SUCCEEDED(font->GetFontFamily(&family))&&SUCCEEDED(family->GetFamilyNames(&names))){UINT32 length=0;check(names->GetStringLength(0,&length));std::vector<wchar_t> name(static_cast<size_t>(length)+1);check(names->GetString(0,name.data(),length+1));out.faces.insert(utf8(name.data()));}
            std::map<UINT32,UINT32> groups;
            if(desc&&desc->clusterMap)for(UINT32 i=0;i<desc->stringLength;++i){auto g=desc->clusterMap[i];auto source=desc->textPosition+i;auto found=groups.find(g);if(found==groups.end()||source<found->second)groups[g]=source;}
            if(groups.empty())groups[0]=desc?desc->textPosition:0;
            float advance=0;const bool rtl=(run->bidiLevel&1)!=0;
            for(auto it=groups.begin();it!=groups.end();++it) {
                UINT32 first=it->first;auto next=std::next(it);UINT32 count=(next==groups.end()?run->glyphCount:next->first)-first;
                if(!count||first>=run->glyphCount||count>run->glyphCount-first)return E_INVALIDARG;
                ComPtr<ID2D1PathGeometry> geometry;ComPtr<ID2D1GeometrySink> sink;check(factory->CreatePathGeometry(&geometry));check(geometry->Open(&sink));
                check(run->fontFace->GetGlyphRunOutline(run->fontEmSize,run->glyphIndices+first,run->glyphAdvances?run->glyphAdvances+first:nullptr,run->glyphOffsets?run->glyphOffsets+first:nullptr,count,run->isSideways,rtl,sink.Get()));check(sink->Close());
                auto unit=std::upper_bound(out.starts.begin(),out.starts.end(),it->second);uint32_t index=unit==out.starts.begin()?0:static_cast<uint32_t>(std::distance(out.starts.begin(),unit)-1);
                out.shapes.push_back({geometry,x+(rtl?-advance:advance),y,index});
                for(UINT32 i=first;i<first+count;++i)if(run->glyphAdvances)advance+=run->glyphAdvances[i];
            }
            return S_OK;
        }catch(HRESULT hr){return hr;}catch(...){return E_FAIL;}
    }
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*,FLOAT,FLOAT,const DWRITE_UNDERLINE*,IUnknown*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*,FLOAT,FLOAT,const DWRITE_STRIKETHROUGH*,IUnknown*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void*,FLOAT,FLOAT,IDWriteInlineObject*,BOOL,BOOL,IUnknown*) override {return E_NOTIMPL;}
};
}
struct kn_text_renderer {
    std::mutex gate;bool warp=false;std::string adapter;int width=0,height=0;
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;ComPtr<ID2D1Factory1> factory;ComPtr<ID2D1Device> d2d;ComPtr<ID2D1DeviceContext> draw;ComPtr<IDWriteFactory> write;
    ComPtr<ID3D11Texture2D> target,readback;ComPtr<ID2D1Bitmap1> bitmap;ComPtr<ID2D1SolidColorBrush> brush;
    std::map<std::string,layout> layouts;std::set<std::string> faces,warnings;size_t cached_shapes=0;std::string cached_source;kn_text::json document;
    explicit kn_text_renderer(bool use_warp):warp(use_warp) {
        D3D_FEATURE_LEVEL level;check(D3D11CreateDevice(nullptr,warp?D3D_DRIVER_TYPE_WARP:D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context));
        ComPtr<IDXGIDevice> dxgi;check(device.As(&dxgi));ComPtr<IDXGIAdapter> ad;check(dxgi->GetAdapter(&ad));DXGI_ADAPTER_DESC description{};check(ad->GetDesc(&description));adapter=utf8(description.Description);
        ComPtr<IDXGIAdapter1> actual_adapter;check(ad.As(&actual_adapter));DXGI_ADAPTER_DESC1 actual_description{};check(actual_adapter->GetDesc1(&actual_description));if(!warp&&(actual_description.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)!=0)throw static_cast<HRESULT>(DXGI_ERROR_UNSUPPORTED);
        D2D1_FACTORY_OPTIONS options{};check(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED,__uuidof(ID2D1Factory1),&options,reinterpret_cast<void**>(factory.GetAddressOf())));check(factory->CreateDevice(dxgi.Get(),&d2d));check(d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&draw));
        check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(write.GetAddressOf())));check(draw->CreateSolidColorBrush(D2D1::ColorF(1,1,1,1),&brush));draw->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    }
    void resize(int w,int h) {
        if(w==width&&h==height)return;
        draw->SetTarget(nullptr);bitmap.Reset();target.Reset();readback.Reset();width=height=0;
        D3D11_TEXTURE2D_DESC desc{};desc.Width=static_cast<UINT>(w);desc.Height=static_cast<UINT>(h);desc.MipLevels=1;desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        check(device->CreateTexture2D(&desc,nullptr,&target));ComPtr<IDXGISurface> surface;check(target.As(&surface));auto properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,D2D1::PixelFormat(desc.Format,D2D1_ALPHA_MODE_PREMULTIPLIED));check(draw->CreateBitmapFromDxgiSurface(surface.Get(),&properties,&bitmap));draw->SetTarget(bitmap.Get());
        desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;check(device->CreateTexture2D(&desc,nullptr,&readback));width=w;height=h;
    }
    layout& get_layout(const kn_text::json& clip,int w,int h) {
        auto key=kn_text::json::array({clip.at("text"),clip.at("fontFamily"),clip.at("fontSize"),w,h}).dump();auto it=layouts.find(key);if(it!=layouts.end())return it->second;
        if(layouts.size()>=128||cached_shapes>=16384){layouts.clear();cached_shapes=0;}
        ComPtr<IDWriteTextFormat> format;auto font=wide(clip.at("fontFamily").get<std::string>()),text=wide(clip.at("text").get<std::string>());check(write->CreateTextFormat(font.c_str(),nullptr,DWRITE_FONT_WEIGHT_NORMAL,DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,clip.at("fontSize").get<float>(),L"ja-JP",&format));
        ComPtr<IDWriteTextLayout> text_layout;check(write->CreateTextLayout(text.c_str(),static_cast<UINT32>(text.size()),format.Get(),static_cast<float>(w),static_cast<float>(h),&text_layout));
        layout result;ComPtr<IDWriteFontCollection> fonts;check(write->GetSystemFontCollection(&fonts));UINT32 font_index=0;BOOL exists=FALSE;check(fonts->FindFamilyName(font.c_str(),&font_index,&exists));if(!exists)result.warning="Requested font missing: "+clip.at("fontFamily").get<std::string>();
        UINT32 count=0;text_layout->GetClusterMetrics(nullptr,0,&count);std::vector<DWRITE_CLUSTER_METRICS> metrics(count);check(text_layout->GetClusterMetrics(metrics.data(),count,&count));uint32_t source=0;for(auto metric:metrics){result.starts.push_back(source);source+=metric.length;}
        ComPtr<collector> collect;collect.Attach(new collector(factory.Get(),write.Get(),result));check(text_layout->Draw(nullptr,collect.Get(),0,0));cached_shapes+=result.shapes.size();auto inserted=layouts.emplace(key,std::move(result));return inserted.first->second;
    }
};
int32_t KN_CALL kn_text_renderer_create(int32_t warp,kn_text_renderer** out,int32_t* error) noexcept {
    if(!out||!error||(warp!=0&&warp!=1))return KN_INVALID_ARGUMENT;
    *out=nullptr;*error=0;try{*out=new kn_text_renderer(warp!=0);return KN_OK;}catch(HRESULT hr){*error=static_cast<int32_t>(hr);return KN_GPU_FAILURE;}catch(...){return KN_OUT_OF_MEMORY;}
}
void KN_CALL kn_text_renderer_destroy(kn_text_renderer* r) noexcept {delete r;}
int32_t KN_CALL kn_text_render(kn_text_renderer* r,const uint8_t* data,uint32_t size,int64_t tick,int32_t width,int32_t height,uint8_t* bgra,uint32_t capacity,int32_t* error) noexcept {
    if(!r||!data||!size||size>16*1024*1024||!bgra||!error||tick<0||width<1||height<1||width>4096||height>4096||capacity!=static_cast<uint32_t>(width*height*4))return KN_INVALID_ARGUMENT;
    *error=0;try {
        std::lock_guard<std::mutex> lock(r->gate);std::string source(reinterpret_cast<const char*>(data),size);if(source!=r->cached_source){auto next=kn_editor::parse(source);kn_text::validate(next);r->document=std::move(next);r->cached_source=std::move(source);}const auto& d=r->document;r->resize(width,height);r->faces.clear();r->warnings.clear();r->draw->BeginDraw();r->draw->SetTransform(D2D1::Matrix3x2F::Identity());r->draw->Clear(D2D1::ColorF(0,0,0,0));
        try {
            const float sx=static_cast<float>(width)/d.at("width").get<float>(),sy=static_cast<float>(height)/d.at("height").get<float>();
            for(const auto& clip:d.at("clips")) {
                auto base=kn_text::evaluate(clip,tick,0);if(base.opacity<=0)continue;
                auto& layout=r->get_layout(clip,d.at("width").get<int>(),d.at("height").get<int>());r->faces.insert(layout.faces.begin(),layout.faces.end());if(!layout.warning.empty())r->warnings.insert(layout.warning);
                for(const auto& glyph:layout.shapes) {
                    auto m=kn_text::evaluate(clip,tick,glyph.unit);if(m.opacity<=0)continue;
                    r->draw->SetTransform(D2D1::Matrix3x2F::Identity());
                    const bool group_opacity=m.opacity<1;
                    if(group_opacity){auto parameters=D2D1::LayerParameters1();parameters.opacity=static_cast<float>(m.opacity);r->draw->PushLayer(parameters,nullptr);}
                    auto matrix=D2D1::Matrix3x2F::Scale(static_cast<float>(m.scale),static_cast<float>(m.scale))*D2D1::Matrix3x2F::Rotation(static_cast<float>(m.rotation))*D2D1::Matrix3x2F::Translation(glyph.x+clip.at("x").get<float>()+static_cast<float>(m.x),glyph.y+clip.at("y").get<float>()+static_cast<float>(m.y));
                    const auto color=[&](const kn_text::json& c){return D2D1::ColorF(c[0].get<float>(),c[1].get<float>(),c[2].get<float>(),c[3].get<float>());};
                    auto shadow=clip.at("shadowColor");if(shadow[3].get<double>()>0){auto sm=matrix*D2D1::Matrix3x2F::Translation(clip.at("shadowX").get<float>(),clip.at("shadowY").get<float>())*D2D1::Matrix3x2F::Scale(sx,sy);r->draw->SetTransform(sm);r->brush->SetColor(color(shadow));r->draw->FillGeometry(glyph.geometry.Get(),r->brush.Get());}
                    r->draw->SetTransform(matrix*D2D1::Matrix3x2F::Scale(sx,sy));float stroke=clip.at("strokeWidth").get<float>();if(stroke>0){r->brush->SetColor(color(clip.at("strokeColor")));r->draw->DrawGeometry(glyph.geometry.Get(),r->brush.Get(),stroke*2);}
                    r->brush->SetColor(color(clip.at("color")));r->draw->FillGeometry(glyph.geometry.Get(),r->brush.Get());
                    if(group_opacity)r->draw->PopLayer();
                }
            }
        }catch(...){r->draw->EndDraw();throw;}
        check(r->draw->EndDraw());r->context->CopyResource(r->readback.Get(),r->target.Get());D3D11_MAPPED_SUBRESOURCE mapped{};check(r->context->Map(r->readback.Get(),0,D3D11_MAP_READ,0,&mapped));for(int y=0;y<height;++y)memcpy(bgra+static_cast<size_t>(y)*width*4,static_cast<const uint8_t*>(mapped.pData)+static_cast<size_t>(y)*mapped.RowPitch,static_cast<size_t>(width)*4);r->context->Unmap(r->readback.Get(),0);return KN_OK;
    }catch(HRESULT hr){*error=static_cast<int32_t>(hr);return KN_GPU_FAILURE;}catch(const std::bad_alloc&){return KN_OUT_OF_MEMORY;}catch(...){return KN_INVALID_ARGUMENT;}
}
int32_t KN_CALL kn_text_renderer_info(kn_text_renderer* r,kn_buffer** output) noexcept {
    if(!r||!output)return KN_INVALID_ARGUMENT;
    *output=nullptr;try{std::lock_guard<std::mutex> lock(r->gate);auto s=kn_text::json({{"backend",r->warp?"D3D11-WARP":"D3D11-Hardware"},{"adapter",r->adapter},{"resolvedFonts",r->faces},{"fontWarnings",r->warnings},{"layoutCacheCount",r->layouts.size()},{"surfaceBytes",static_cast<int64_t>(r->width)*r->height*8}}).dump();auto b=std::make_unique<kn_buffer>();b->data=std::make_shared<const std::vector<uint8_t>>(s.begin(),s.end());*output=b.release();return KN_OK;}catch(...){return KN_OUT_OF_MEMORY;}
}
#else
struct kn_text_renderer {};
int32_t KN_CALL kn_text_renderer_create(int32_t warp,kn_text_renderer** output,int32_t* error) noexcept {if(!output||!error||(warp!=0&&warp!=1))return KN_INVALID_ARGUMENT;*output=nullptr;*error=0;return KN_GPU_UNSUPPORTED;}
void KN_CALL kn_text_renderer_destroy(kn_text_renderer* r) noexcept {delete r;}
int32_t KN_CALL kn_text_render(kn_text_renderer*,const uint8_t*,uint32_t,int64_t,int32_t,int32_t,uint8_t*,uint32_t,int32_t*) noexcept {return KN_GPU_UNSUPPORTED;}
int32_t KN_CALL kn_text_renderer_info(kn_text_renderer*,kn_buffer**) noexcept {return KN_GPU_UNSUPPORTED;}
#endif
