#ifndef _WIN32
#include "kinetic_vectors.hpp"
#include "kachinco_native.h"
#include "buffer.hpp"
#include <pango/pangocairo.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <set>

namespace {
using path_ptr=std::shared_ptr<cairo_path_t>;
struct part {path_ptr path;std::array<double,4> fill{1,1,1,1};};
struct shape {std::vector<part> parts;double x=0,y=0;uint32_t source=0,unit=0;};
struct layout {std::vector<shape> shapes;uint32_t count=0;std::set<std::string> faces,warnings;size_t bytes=0;};
path_ptr capture(cairo_t* cr) {
    auto path=path_ptr(cairo_copy_path(cr),cairo_path_destroy);
    if(!path||path->status!=CAIRO_STATUS_SUCCESS)throw std::runtime_error("Cairo path allocation failed");
    return path;
}
void append(cairo_t* cr,const kn_text::vector_path& path) {
    cairo_new_path(cr);
    for(const auto& c:path.commands){const auto& v=c.values;
        if(c.op=='M')cairo_move_to(cr,v[0],v[1]);else if(c.op=='L')cairo_line_to(cr,v[0],v[1]);
        else if(c.op=='C')cairo_curve_to(cr,v[0],v[1],v[2],v[3],v[4],v[5]);else cairo_close_path(cr);}
}
layout make_layout(cairo_t* cr,const kn_text::json& clip,int width) {
    layout result;
    if(clip.contains("glyphs")) {
        for(const auto& u:kn_text::vector_layout(clip)) {
            shape s;s.x=u.x;s.y=u.y;s.unit=u.unit;
            for(const auto& p:u.paths){append(cr,p);s.parts.push_back({capture(cr),p.fill});}
            result.shapes.push_back(std::move(s));
        }result.count=static_cast<uint32_t>(result.shapes.size());
    }else {
        auto text=clip.at("text").get<std::string>(),requested=clip.at("fontFamily").get<std::string>();
        auto l=std::unique_ptr<PangoLayout,void(*)(PangoLayout*)>(pango_cairo_create_layout(cr),[](PangoLayout* p){g_object_unref(p);});
        auto font=std::unique_ptr<PangoFontDescription,decltype(&pango_font_description_free)>(pango_font_description_new(),pango_font_description_free);
        pango_font_description_set_family(font.get(),requested=="Yu Gothic"?"Noto Sans CJK JP":requested.c_str());
        pango_font_description_set_absolute_size(font.get(),clip.at("fontSize").get<double>()*PANGO_SCALE);
        pango_layout_set_font_description(l.get(),font.get());pango_layout_set_text(l.get(),text.c_str(),static_cast<int>(text.size()));
        pango_layout_set_width(l.get(),width*PANGO_SCALE);pango_layout_set_wrap(l.get(),PANGO_WRAP_WORD_CHAR);
        if(pango_layout_get_unknown_glyphs_count(l.get()))throw std::invalid_argument("KINETAI_GLYPH_MISSING: install fonts covering this text, or import outlined Glyph assets");
        auto iter=std::unique_ptr<PangoLayoutIter,decltype(&pango_layout_iter_free)>(pango_layout_get_iter(l.get()),pango_layout_iter_free);
        std::set<uint32_t> starts;
        do {
            auto run=pango_layout_iter_get_run_readonly(iter.get());if(!run)continue;
            PangoRectangle logical{};pango_layout_iter_get_run_extents(iter.get(),nullptr,&logical);
            double x=static_cast<double>(logical.x)/PANGO_SCALE,y=static_cast<double>(pango_layout_iter_get_baseline(iter.get()))/PANGO_SCALE;
            auto actual=std::unique_ptr<PangoFontDescription,decltype(&pango_font_description_free)>(pango_font_describe(run->item->analysis.font),pango_font_description_free);
            auto family=pango_font_description_get_family(actual.get());if(family){result.faces.insert(family);if(requested!=family)result.warnings.insert("Requested "+requested+"; resolved "+family);}
            for(int begin=0;begin<run->glyphs->num_glyphs;) {
                int end=begin+1;while(end<run->glyphs->num_glyphs&&run->glyphs->log_clusters[end]==run->glyphs->log_clusters[begin])++end;
                auto glyphs=std::unique_ptr<PangoGlyphString,decltype(&pango_glyph_string_free)>(pango_glyph_string_new(),pango_glyph_string_free);
                pango_glyph_string_set_size(glyphs.get(),end-begin);
                for(int i=begin;i<end;++i){glyphs->glyphs[i-begin]=run->glyphs->glyphs[i];glyphs->log_clusters[i-begin]=0;}
                cairo_new_path(cr);cairo_move_to(cr,0,0);pango_cairo_glyph_string_path(cr,run->item->analysis.font,glyphs.get());
                uint32_t source=static_cast<uint32_t>(run->item->offset+run->glyphs->log_clusters[begin]);starts.insert(source);
                result.shapes.push_back({{{capture(cr),{1,1,1,1}}},x,y,source,0});
                for(int i=begin;i<end;++i)x+=static_cast<double>(run->glyphs->glyphs[i].geometry.width)/PANGO_SCALE;
                begin=end;
            }
        }while(pango_layout_iter_next_run(iter.get()));
        result.count=static_cast<uint32_t>(starts.size());
        for(auto& s:result.shapes)s.unit=static_cast<uint32_t>(std::distance(starts.begin(),starts.find(s.source)));
    }
    for(const auto& s:result.shapes)for(const auto& p:s.parts)result.bytes+=static_cast<size_t>(p.path->num_data)*sizeof(cairo_path_data_t);
    return result;
}
}
struct kn_text_renderer {
    std::mutex gate;std::string cached_source,last_error;kn_text::json document;
    std::map<std::string,layout> layouts;size_t cache_bytes=0;int width=0,height=0;
    std::set<std::string> faces,warnings;
    layout& get_layout(cairo_t* cr,const kn_text::json& clip,int w) {
        auto key=kn_text::json::array({clip.at("text"),clip.at("fontFamily"),clip.at("fontSize"),w,clip.contains("glyphs")?clip.at("glyphs"):kn_text::json(nullptr)}).dump();
        auto found=layouts.find(key);if(found!=layouts.end())return found->second;
        auto next=make_layout(cr,clip,w);next.bytes+=key.size();
        constexpr size_t maximum=32*1024*1024;
        if(next.bytes>maximum)throw std::invalid_argument("Kinetai layout exceeds 32 MiB");
        if(layouts.size()>=128||cache_bytes+next.bytes>maximum){layouts.clear();cache_bytes=0;}
        cache_bytes+=next.bytes;return layouts.emplace(std::move(key),std::move(next)).first->second;
    }
};
int32_t KN_CALL kn_text_renderer_create(int32_t warp,kn_text_renderer** out,int32_t* error) noexcept {
    if(!out||!error||(warp!=0&&warp!=1))return KN_INVALID_ARGUMENT;
    *out=nullptr;*error=0;if(warp)return KN_GPU_UNSUPPORTED;
    try{*out=new kn_text_renderer();return KN_OK;}catch(...){return KN_OUT_OF_MEMORY;}
}
void KN_CALL kn_text_renderer_destroy(kn_text_renderer* r) noexcept {delete r;}
int32_t KN_CALL kn_text_render(kn_text_renderer* r,const uint8_t* data,uint32_t size,int64_t tick,int32_t width,int32_t height,uint8_t* bgra,uint32_t capacity,int32_t* error) noexcept {
    if(!r||!data||!size||size>16*1024*1024||!bgra||!error||tick<0||width<1||height<1||width>4096||height>4096||capacity!=static_cast<uint32_t>(width*height*4))return KN_INVALID_ARGUMENT;
    *error=0;
    try {
        std::lock_guard<std::mutex> lock(r->gate);r->last_error.clear();std::string source(reinterpret_cast<const char*>(data),size);
        if(source!=r->cached_source){auto d=kn_editor::parse(source);kn_text::validate(d);r->document=std::move(d);r->cached_source=std::move(source);}
        std::fill(bgra,bgra+capacity,0);r->width=width;r->height=height;r->faces.clear();r->warnings.clear();
        auto surface=std::unique_ptr<cairo_surface_t,decltype(&cairo_surface_destroy)>(cairo_image_surface_create_for_data(bgra,CAIRO_FORMAT_ARGB32,width,height,width*4),cairo_surface_destroy);
        auto cr=std::unique_ptr<cairo_t,decltype(&cairo_destroy)>(cairo_create(surface.get()),cairo_destroy);
        if(cairo_surface_status(surface.get())||cairo_status(cr.get()))throw std::runtime_error("Cairo surface allocation failed");
        const auto& d=r->document;double sx=static_cast<double>(width)/d.at("width").get<double>(),sy=static_cast<double>(height)/d.at("height").get<double>();size_t budget=2000000;
        for(const auto& clip:d.at("clips")) {
            auto start=std::stoll(clip.at("startTicks").get<std::string>()),duration=std::stoll(clip.at("durationTicks").get<std::string>());if(tick<start||tick-start>=duration)continue;
            auto& layout=r->get_layout(cr.get(),clip,d.at("width").get<int>());r->faces.insert(layout.faces.begin(),layout.faces.end());r->warnings.insert(layout.warnings.begin(),layout.warnings.end());
            for(const auto& glyph:layout.shapes) {
                auto m=kn_text::evaluate(clip,tick,glyph.unit,layout.count,&budget);if(m.opacity<=0||m.scale==0)continue;
                cairo_save(cr.get());cairo_identity_matrix(cr.get());cairo_push_group(cr.get());
                const auto matrix=[&](double dx,double dy){cairo_identity_matrix(cr.get());cairo_scale(cr.get(),sx,sy);cairo_translate(cr.get(),glyph.x+clip.at("x").get<double>()+m.x+dx,glyph.y+clip.at("y").get<double>()+m.y+dy);cairo_rotate(cr.get(),m.rotation*3.14159265358979323846/180);cairo_scale(cr.get(),m.scale,m.scale);};
                const auto color=[&](const kn_text::json& c){cairo_set_source_rgba(cr.get(),c[0].get<double>(),c[1].get<double>(),c[2].get<double>(),c[3].get<double>());};
                if(clip.at("shadowColor")[3].get<double>()>0){matrix(clip.at("shadowX").get<double>(),clip.at("shadowY").get<double>());color(clip.at("shadowColor"));for(const auto& p:glyph.parts){cairo_new_path(cr.get());cairo_append_path(cr.get(),p.path.get());cairo_fill(cr.get());}}
                matrix(0,0);
                for(const auto& p:glyph.parts){cairo_new_path(cr.get());cairo_append_path(cr.get(),p.path.get());double stroke=clip.at("strokeWidth").get<double>();
                    if(stroke>0){color(clip.at("strokeColor"));cairo_set_line_width(cr.get(),stroke*2);cairo_stroke_preserve(cr.get());}
                    const auto& tint=clip.at("color");cairo_set_source_rgba(cr.get(),p.fill[0]*tint[0].get<double>(),p.fill[1]*tint[1].get<double>(),p.fill[2]*tint[2].get<double>(),p.fill[3]*tint[3].get<double>());cairo_fill(cr.get());}
                cairo_pop_group_to_source(cr.get());cairo_paint_with_alpha(cr.get(),m.opacity);cairo_restore(cr.get());
            }
        }
        if(cairo_status(cr.get()))throw std::runtime_error("Cairo native text drawing failed");
        cairo_surface_flush(surface.get());return KN_OK;
    }catch(const std::bad_alloc&){return KN_OUT_OF_MEMORY;}catch(const std::exception& e){try{std::lock_guard<std::mutex> lock(r->gate);r->last_error=e.what();}catch(...){}return KN_INVALID_ARGUMENT;}catch(...){return KN_INTERNAL_ERROR;}
}
int32_t KN_CALL kn_text_renderer_info(kn_text_renderer* r,kn_buffer** out) noexcept {
    if(!r||!out)return KN_INVALID_ARGUMENT;
    *out=nullptr;
    try{std::lock_guard<std::mutex> lock(r->gate);auto text=kn_text::json({{"scriptError",r->last_error},{"backend","Pango-Cairo-CPU"},{"adapter","CPU"},{"resolvedFonts",r->faces},{"fontWarnings",r->warnings},{"layoutCacheCount",r->layouts.size()},{"layoutCacheBytes",r->cache_bytes},{"surfaceBytes",static_cast<int64_t>(r->width)*r->height*4}}).dump();auto b=std::make_unique<kn_buffer>();b->data=std::make_shared<const std::vector<uint8_t>>(text.begin(),text.end());*out=b.release();return KN_OK;}catch(...){return KN_OUT_OF_MEMORY;}
}
#endif
