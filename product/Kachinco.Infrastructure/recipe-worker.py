"""Trusted compiler for a deliberately small Python Recipe subset; never exec/eval."""
import ast
import json
import math
import sys
try:
    import resource
    resource.setrlimit(resource.RLIMIT_AS, (128 * 1024 * 1024, 128 * 1024 * 1024))
    resource.setrlimit(resource.RLIMIT_CPU, (3, 3))
except ImportError:
    pass  # Windows parent assigns a memory-limited kill-on-close job before sending input.

def compile_recipe(source):
    if not isinstance(source, str) or len(source) > 65536:
        raise ValueError("source limit")
    module = ast.parse(source, mode="exec")
    if not 1 <= len(module.body) <= 128:
        raise ValueError("expected 1-128 primitive calls")
    result = []
    for stmt in module.body:
        if not isinstance(stmt, ast.Expr) or not isinstance(stmt.value, ast.Call):
            raise ValueError("only text(...) and particles(...) calls are supported")
        call = stmt.value
        if not isinstance(call.func, ast.Name) or call.func.id not in ("text", "particles") or call.args:
            raise ValueError("use literal keyword arguments to text or particles")
        values = {"kind": call.func.id, "text": "", "x": 0, "y": 0, "vx": 0, "vy": 0, "size": 48, "count": 1}
        seen = set()
        for keyword in call.keywords:
            name = keyword.arg
            if name not in ("text", "x", "y", "vx", "vy", "size", "count") or name in seen:
                raise ValueError("unknown or duplicate keyword")
            seen.add(name)
            node = keyword.value
            if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub) and isinstance(node.operand, ast.Constant) and type(node.operand.value) in (int, float):
                value = -node.operand.value
            elif isinstance(node, ast.Constant):
                value = node.value
            else:
                raise ValueError("arguments must be literal numbers or strings")
            if name == "text":
                if not isinstance(value, str) or len(value) > 4096:
                    raise ValueError("text limit")
            elif type(value) not in (int, float) or not math.isfinite(value) or abs(value) > 10000:
                raise ValueError("numeric limit")
            values[name] = value
        if values["kind"] == "text" and not values["text"].strip():
            raise ValueError("text required")
        if not 1 <= values["size"] <= 512 or type(values["count"]) is not int or not 1 <= values["count"] <= 1000:
            raise ValueError("size/count limit")
        result.append(values)
    if sum(x["count"] for x in result) > 2000:
        raise ValueError("total primitive limit")
    return result

def compile_kinetic(source):
    if not isinstance(source, str) or len(source) > 65536:
        raise ValueError("source limit")
    module = ast.parse(source, mode="exec")
    if not 1 <= len(module.body) <= 32:
        raise ValueError("expected 1-32 kinetic calls")
    signatures = {
        "translate": {"x": 0, "y": 0}, "scale": {"value": 1},
        "rotate": {"degrees": 0}, "opacity": {"value": 1},
        "shake": {"amplitude": 8, "frequency": 12},
        "flight": {"x": -500, "y": -200, "durationTicks": "17640000"},
        "stagger": {"delayTicks": "1234800"},
    }
    ops = []
    for stmt in module.body:
        if not isinstance(stmt, ast.Expr) or not isinstance(stmt.value, ast.Call):
            raise ValueError("only kinetic primitive calls are allowed")
        call = stmt.value
        if not isinstance(call.func, ast.Name) or call.func.id not in signatures or call.args:
            raise ValueError("use literal keywords to kinetic primitives")
        values = dict(signatures[call.func.id]); seen = set()
        for keyword in call.keywords:
            name = keyword.arg
            if name not in values or name in seen:
                raise ValueError("unknown or duplicate kinetic keyword")
            seen.add(name); node = keyword.value
            if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub) and isinstance(node.operand, ast.Constant) and type(node.operand.value) in (int, float):
                value = -node.operand.value
            elif isinstance(node, ast.Constant):
                value = node.value
            else:
                raise ValueError("kinetic arguments must be literal numbers or tick strings")
            if name.endswith("Ticks"):
                if not isinstance(value, str) or not value.isascii() or not value.isdecimal() or len(value) > 19:
                    raise ValueError("tick strings required")
            elif type(value) not in (int, float) or not math.isfinite(value) or abs(value) > 36000:
                raise ValueError("numeric limit")
            values[name] = value
        ops.append({"op": call.func.id, **values})
    return {"version": 1, "ops": ops}

try:
    raw = sys.stdin.buffer.readline(262145)
    if len(raw) > 262144:
        raise ValueError("input limit")
    request = json.loads(raw)
    mode = request.get("mode", "recipe")
    if mode not in ("recipe", "kinetic"):
        raise ValueError("unsupported compiler mode")
    value = compile_kinetic(request["source"]) if mode == "kinetic" else {"operations": compile_recipe(request["source"])}
    print(json.dumps(value, ensure_ascii=True))
except (ValueError, SyntaxError, MemoryError, RecursionError, KeyError, TypeError) as error:
    print(json.dumps({"error": str(error)[:512]}))
    sys.exit(1)
