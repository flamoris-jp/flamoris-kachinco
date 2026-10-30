#include "kinetic_vectors.hpp"
#include <cmath>
#include <map>
#include <set>
#include <string>
namespace kn_text {
namespace {
void fields(const json& value,std::initializer_list<const char*> keys) {
    if(!value.is_object()||value.size()!=keys.size())throw std::invalid_argument("Glyph fields");
    for(auto k:keys)if(!value.contains(k))throw std::invalid_argument("Missing Glyph field");
}
double number(const json& value,double lo,double hi) {
    if(!value.is_number())throw std::invalid_argument("Glyph numeric coordinate required");
    auto n=value.get<double>();if(!std::isfinite(n)||n<lo||n>hi)throw std::invalid_argument("Glyph coordinate/metric limit");return n;
}
void label(const json& value,size_t max) {
    if(!value.is_string()||value.get_ref<const std::string&>().empty()||value.get_ref<const std::string&>().size()>max||value.get_ref<const std::string&>().find('\0')!=std::string::npos)throw std::invalid_argument("Glyph label limit");
}
// Decode exactly, including overlong/surrogate/range rejection. Columns count
// Unicode codepoints, matching the generator, rather than UTF-8 bytes/UTF-16.
std::vector<uint32_t> codepoints(const std::string& s) {
    std::vector<uint32_t> out;
    for(size_t i=0;i<s.size();) {
        auto a=static_cast<unsigned char>(s[i++]);uint32_t v=a;int n=0;
        if(a>=0xC2&&a<=0xDF){v=a&31;n=1;}else if(a>=0xE0&&a<=0xEF){v=a&15;n=2;}else if(a>=0xF0&&a<=0xF4){v=a&7;n=3;}else if(a>=128)throw std::invalid_argument("Glyph UTF-8");
        for(int k=0;k<n;++k){if(i==s.size())throw std::invalid_argument("Glyph UTF-8");auto b=static_cast<unsigned char>(s[i++]);if((b&0xC0)!=0x80)throw std::invalid_argument("Glyph UTF-8");v=(v<<6)|(b&63);}
        if((n==1&&v<128)||(n==2&&v<2048)||(n==3&&v<65536)||v>0x10FFFF||(v>=0xD800&&v<=0xDFFF))throw std::invalid_argument("Glyph UTF-8 scalar");
        out.push_back(v);
    }return out;
}
bool space(uint32_t c){return c==9||c==10||c==13||c==32||c==0x85||c==0xA0||c==0x1680||(c>=0x2000&&c<=0x200A)||c==0x2028||c==0x2029||c==0x202F||c==0x205F||c==0x3000||(c>=0x1C&&c<=0x1F);}
}
void validate_vectors(const json& clip,size_t& budget) {
    const auto& g=clip.at("glyphs");fields(g,{"version","source","units"});
    if(!g.at("version").is_number_integer()||g.at("version")!=1)throw std::invalid_argument("Unsupported Glyph geometry version");
    auto& source=g.at("source");fields(source,{"song","style","sha256","license"});
    label(source.at("song"),1024);label(source.at("style"),400);label(source.at("license"),8192);label(source.at("sha256"),64);
    auto hash=source.at("sha256").get<std::string>();if(hash.size()!=64||hash.find_first_not_of("0123456789abcdef")!=std::string::npos)throw std::invalid_argument("Glyph source hash");
    const auto& units=g.at("units");if(!units.is_array()||units.empty()||units.size()>4096)throw std::invalid_argument("Glyph unit count");
    std::vector<std::tuple<int,int,uint32_t>> visible;int line=1,column=1;
    for(auto c:codepoints(clip.at("text").get<std::string>())){if(!space(c))visible.emplace_back(line,column,c);if(c==10){++line;column=1;}else ++column;}
    if(visible.size()!=units.size())throw std::invalid_argument("Glyph text changed; regenerate or detach Glyph geometry");
    std::set<std::string> ids;size_t index=0;
    for(const auto& u:units) {
        fields(u,{"occurrenceId","character","line","column","advance","paths"});label(u.at("occurrenceId"),128);label(u.at("character"),4);
        if(!ids.insert(u.at("occurrenceId").get<std::string>()).second)throw std::invalid_argument("Duplicate Glyph occurrence");
        auto chars=codepoints(u.at("character").get<std::string>());auto expected=visible[index++];
        if(chars.size()!=1||chars[0]!=std::get<2>(expected)||kn_editor::integer(u.at("line"))!=std::get<0>(expected)||kn_editor::integer(u.at("column"))!=std::get<1>(expected))throw std::invalid_argument("Glyph occurrence/text mismatch");
        number(u.at("advance"),1,4000);auto& paths=u.at("paths");if(!paths.is_array()||paths.empty()||paths.size()>64)throw std::invalid_argument("Glyph stroke count");
        std::set<std::string> strokes;
        for(const auto& p:paths) {
            fields(p,{"id","fill","commands"});label(p.at("id"),128);if(!strokes.insert(p.at("id").get<std::string>()).second)throw std::invalid_argument("Duplicate Glyph stroke");
            const auto& fill=p.at("fill");if(!fill.is_array()||fill.size()!=4)throw std::invalid_argument("Glyph RGBA required");for(auto& c:fill)number(c,0,1);
            const auto& commands=p.at("commands");if(!commands.is_array()||commands.empty()||commands.size()>100000)throw std::invalid_argument("Glyph path limit");
            bool open=false;
            for(const auto& c:commands){if(!budget--)throw std::invalid_argument("Glyph geometry budget");if(!c.is_array()||c.empty()||!c[0].is_string())throw std::invalid_argument("Glyph path command");auto op=c[0].get<std::string>();
                size_t count=op=="M"||op=="L"?3:op=="C"?7:op=="Z"?1:0;if(!count||c.size()!=count)throw std::invalid_argument("Glyph supports absolute M/L/C/Z only");
                if(op=="M"){if(open)throw std::invalid_argument("Glyph contour must close");open=true;}else if(!open)throw std::invalid_argument("Glyph contour must start with M");
                if(op=="Z")open=false;
                for(size_t j=1;j<count;++j)number(c[j],-2000,3000);
            }if(open)throw std::invalid_argument("Glyph contour must close");
        }
    }
}
std::vector<vector_unit> vector_layout(const json& clip) {
    const double size=clip.at("fontSize").get<double>(),scale=size/1000;double x=0;size_t at=0;int line=1;
    const auto& units=clip.at("glyphs").at("units");std::vector<vector_unit> result;
    for(auto c:codepoints(clip.at("text").get<std::string>())) {
        if(c==10){x=0;++line;continue;}if(space(c)){x+=size*(c==9?2:c==0x3000?1:0.5);continue;}
        const auto& u=units.at(at);vector_unit value;value.x=x+size/2;value.y=(line-1)*size*1.25+size/2;value.unit=static_cast<uint32_t>(at++);
        for(const auto& p:u.at("paths")){vector_path path;for(size_t k=0;k<4;++k)path.fill[k]=p.at("fill")[k].get<double>();
            for(const auto& cmd:p.at("commands")){vector_command item;item.op=cmd[0].get<std::string>()[0];for(size_t k=1;k<cmd.size();++k)item.values[k-1]=(cmd[k].get<double>()-500)*scale;path.commands.push_back(item);}value.paths.push_back(std::move(path));}
        result.push_back(std::move(value));x+=u.at("advance").get<double>()*scale;
    }return result;
}
}
