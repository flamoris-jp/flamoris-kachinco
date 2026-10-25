#include "kinetic_program.hpp"
#include "kachinco_native.h"
#include <algorithm>
#include <cmath>
#include <map>
#include <set>
#include <stdexcept>

namespace kn_text {
namespace {
const std::map<std::string,size_t> arities={
    {"neg",1},{"not",1},{"sin",1},{"cos",1},{"tan",1},{"sqrt",1},
    {"abs",1},{"floor",1},{"ceil",1},{"add",2},{"sub",2},{"mul",2},
    {"div",2},{"floordiv",2},{"mod",2},{"pow",2},{"min",2},{"max",2},
    {"lt",2},{"le",2},{"gt",2},{"ge",2},{"eq",2},{"ne",2},
    {"and",2},{"or",2},{"select",3},{"clamp",3},{"lerp",3},
    {"smoothstep",3},{"noise",3}};
const std::set<std::string> variables={"t","duration","i","n","seed"};
const std::set<std::string> outputs={"x","y","scale","rotation","opacity"};
double finite(double value) {
    if(!std::isfinite(value)||std::abs(value)>1e9)
        throw std::invalid_argument("Kinetic expression nonfinite/range result");
    return value;
}
void validate_expression(const json& expression,size_t& nodes,size_t depth) {
    if(++nodes>1024||depth>32)throw std::invalid_argument("Kinetic expression budget");
    if(!expression.is_array()||expression.empty()||!expression[0].is_string())
        throw std::invalid_argument("Kinetic expression array required");
    const auto op=expression[0].get<std::string>();
    if(op=="const") {
        if(expression.size()!=2||!expression[1].is_number())throw std::invalid_argument("Kinetic constant");
        finite(expression[1].get<double>());return;
    }
    if(op=="var") {
        if(expression.size()!=2||!expression[1].is_string()||!variables.count(expression[1].get<std::string>()))
            throw std::invalid_argument("Kinetic context variable");
        return;
    }
    auto found=arities.find(op);
    if(found==arities.end()||expression.size()!=found->second+1)throw std::invalid_argument("Kinetic expression opcode/arity");
    for(size_t i=1;i<expression.size();++i)validate_expression(expression[i],nodes,depth+1);
}
uint32_t hash(uint32_t n) {n^=n>>16;n*=0x7feb352du;n^=n>>15;n*=0x846ca68bu;return n^(n>>16);}
// Signed integer channels are normalized with explicit modulo before narrowing.
uint32_t channel(double v) {
    double n=std::fmod(std::floor(v),4294967296.0);if(n<0)n+=4294967296.0;
    return static_cast<uint32_t>(n);
}
struct context {double t,duration,i,n,seed;};
double expression(const json& e,const context& c,size_t& budget) {
    if(budget==0)throw std::invalid_argument("Kinetic frame expression budget exceeded");
    --budget;
    const auto op=e[0].get<std::string>();
    if(op=="const")return e[1].get<double>();
    if(op=="var") {
        const auto v=e[1].get<std::string>();
        if(v=="t")return c.t;
        if(v=="duration")return c.duration;
        if(v=="i")return c.i;
        if(v=="n")return c.n;
        return c.seed;
    }
    const double a=expression(e[1],c,budget);
    if(op=="select")return expression(e[a!=0?2:3],c,budget);
    if(op=="and")return a!=0?expression(e[2],c,budget):a;
    if(op=="or")return a!=0?a:expression(e[2],c,budget);
    if(op=="neg")return -a;
    if(op=="not")return a==0?1:0;
    if(op=="sin")return finite(std::sin(a));
    if(op=="cos")return finite(std::cos(a));
    if(op=="tan")return finite(std::tan(a));
    if(op=="sqrt")return finite(std::sqrt(a));
    if(op=="abs")return std::abs(a);
    if(op=="floor")return std::floor(a);
    if(op=="ceil")return std::ceil(a);
    const double b=expression(e[2],c,budget);
    if(op=="add")return finite(a+b);
    if(op=="sub")return finite(a-b);
    if(op=="mul")return finite(a*b);
    if(op=="div"||op=="floordiv"||op=="mod") {
        if(b==0)throw std::invalid_argument("Kinetic expression division by zero");
        const auto quotient=finite(a/b);
        if(op=="div")return quotient;
        if(op=="floordiv")return std::floor(quotient);
        return finite(a-std::floor(quotient)*b);
    }
    if(op=="pow")return finite(std::pow(a,b));
    if(op=="min")return std::min(a,b);
    if(op=="max")return std::max(a,b);
    if(op=="lt")return a<b?1:0;
    if(op=="le")return a<=b?1:0;
    if(op=="gt")return a>b?1:0;
    if(op=="ge")return a>=b?1:0;
    if(op=="eq")return a==b?1:0;
    if(op=="ne")return a!=b?1:0;
    const double d=expression(e[3],c,budget);
    if(op=="clamp") {
        if(b>d)throw std::invalid_argument("Kinetic clamp bounds");
        return std::clamp(a,b,d);
    }
    if(op=="lerp")return finite(a+(b-a)*d);
    if(op=="smoothstep") {
        if(a>=b)throw std::invalid_argument("Kinetic smoothstep bounds");
        const double t=std::clamp((d-a)/(b-a),0.0,1.0);return t*t*(3-2*t);
    }
    if(op=="noise")return static_cast<double>(hash(channel(a)^hash(channel(b))^hash(channel(d)+101)))/4294967295.0*2-1;
    throw std::invalid_argument("Kinetic unknown expression");
}
}
void validate_program_v2(const json& p) {
    if(!p.is_object()||p.size()!=2||!p.contains("version")||p.at("version")!=2||
       !p.contains("outputs")||!p.at("outputs").is_object()||p.dump().size()>65536)
        throw std::invalid_argument("Kinetic v2 program fields/size");
    size_t nodes=0;
    for(auto it=p.at("outputs").begin();it!=p.at("outputs").end();++it) {
        if(!outputs.count(it.key()))throw std::invalid_argument("Kinetic output property");
        validate_expression(it.value(),nodes,1);
    }
}
motion evaluate_program_v2(const json& clip,int64_t tick,uint32_t unit,uint32_t count,size_t* budget) {
    size_t local_budget=1024;
    size_t& remaining=budget?*budget:local_budget;
    const auto start=std::stoll(clip.at("startTicks").get<std::string>());
    const auto duration=std::stoll(clip.at("durationTicks").get<std::string>());
    motion m;
    if(tick<start||tick-start>=duration){m.opacity=0;return m;}
    if(count<1||unit>=count)throw std::invalid_argument("Kinetic shaping unit/count");
    context c{static_cast<double>(tick-start)/KN_TICKS_PER_SECOND,
        static_cast<double>(duration)/KN_TICKS_PER_SECOND,static_cast<double>(unit),
        static_cast<double>(count),clip.at("seed").get<double>()};
    const auto& out=clip.at("program").at("outputs");
    auto get=[&](const char* name,double fallback){return out.contains(name)?finite(expression(out.at(name),c,remaining)):fallback;};
    m.x=get("x",0);m.y=get("y",0);m.rotation=get("rotation",0);m.scale=get("scale",1);m.opacity=std::clamp(get("opacity",1),0.0,1.0);
    if(std::abs(m.x)>16384||std::abs(m.y)>16384||std::abs(m.rotation)>36000||m.scale<0||m.scale>1000)
        throw std::invalid_argument("Kinetic motion output outside bounds");
    return m;
}
}
