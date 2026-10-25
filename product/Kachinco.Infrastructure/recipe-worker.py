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
    if any(isinstance(node, ast.FunctionDef) for node in module.body):
        return compile_kinetic_script(source)
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

# Symbolic Python authoring. AST nodes are translated, never exec/eval'ed.
class KineticScriptCompiler:
    outputs = {'x': 0, 'y': 0, 'scale': 1, 'rotation': 0, 'opacity': 1}
    contexts = ('t', 'duration', 'i', 'n', 'seed')
    functions = {'sin': 1, 'cos': 1, 'tan': 1, 'sqrt': 1, 'abs': 1,
                 'floor': 1, 'ceil': 1, 'min': 2, 'max': 2, 'pow': 2,
                 'clamp': 3, 'lerp': 3, 'smoothstep': 3, 'noise': 3}
    binary = {ast.Add: 'add', ast.Sub: 'sub', ast.Mult: 'mul', ast.Div: 'div',
              ast.FloorDiv: 'floordiv', ast.Mod: 'mod', ast.Pow: 'pow'}
    compare = {ast.Lt: 'lt', ast.LtE: 'le', ast.Gt: 'gt', ast.GtE: 'ge',
               ast.Eq: 'eq', ast.NotEq: 'ne'}

    def __init__(self, module):
        self.defs = {}
        self.globals = {'pi': ['const', math.pi], 'tau': ['const', math.tau]}
        self.steps = 0
        for stmt in module.body:
            if isinstance(stmt, ast.Expr) and isinstance(stmt.value, ast.Constant) and isinstance(stmt.value.value, str):
                continue
            if isinstance(stmt, ast.FunctionDef):
                self.name(stmt.name)
                a = stmt.args
                if (stmt.decorator_list or stmt.returns or a.vararg or a.kwarg or
                    a.kwonlyargs or a.posonlyargs or a.defaults or a.kw_defaults or
                    any(x.annotation for x in a.args) or len(a.args) > 8):
                    raise ValueError('use simple undecorated function parameters')
                if stmt.name in self.defs or stmt.name in self.functions or stmt.name in self.globals:
                    raise ValueError('duplicate/reserved function name')
                for arg in a.args:
                    self.name(arg.arg)
                if len({arg.arg for arg in a.args}) != len(a.args):
                    raise ValueError('duplicate parameter')
                self.defs[stmt.name] = stmt
            elif isinstance(stmt, ast.Assign) and len(stmt.targets) == 1 and isinstance(stmt.targets[0], ast.Name):
                name = stmt.targets[0].id
                self.name(name)
                if name in self.globals or name in self.functions or name in self.contexts or name in self.defs:
                    raise ValueError('reserved global name')
                value = self.expr(stmt.value, self.globals, ())
                if value[0] != 'const':
                    raise ValueError('global values must be numeric literals')
                self.globals[name] = value
            else:
                raise ValueError('script requires animate(t, duration, i, n, seed); only functions and numeric constants at top level')
        if not 1 <= len(self.defs) <= 16 or 'animate' not in self.defs:
            raise ValueError('define animate(t, duration, i, n, seed); maximum 16 functions')
        if tuple(x.arg for x in self.defs['animate'].args.args) != self.contexts:
            raise ValueError('animate parameters must be t, duration, i, n, seed')

    @staticmethod
    def name(value):
        if not value.isidentifier() or value.startswith('_') or len(value) > 64:
            raise ValueError('invalid script name')

    def bounded(self, value):
        count = 0
        def visit(node, depth):
            nonlocal count
            count += 1
            if count > 1024 or depth > 32:
                raise ValueError('compiled expression exceeds 1024 nodes / depth 32')
            for item in node[1:]:
                if isinstance(item, list):
                    visit(item, depth + 1)
        for expr in (value.values() if isinstance(value, dict) else [value]):
            visit(expr, 1)
        return value

    def op(self, name, *args):
        if any(not isinstance(arg, list) for arg in args):
            raise ValueError('numeric expression required')
        return self.bounded([name, *args])

    def expr(self, node, env, stack):
        if isinstance(node, ast.Constant) and type(node.value) in (int, float, bool):
            v = float(node.value)
            if not math.isfinite(v) or abs(v) > 1e9:
                raise ValueError('finite numeric constants within 1e9 required')
            return ['const', v]
        if isinstance(node, ast.Name):
            self.name(node.id)
            if node.id not in env:
                raise ValueError('unknown variable: ' + node.id)
            return env[node.id]
        if isinstance(node, ast.BinOp) and type(node.op) in self.binary:
            return self.op(self.binary[type(node.op)], self.expr(node.left, env, stack), self.expr(node.right, env, stack))
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, (ast.USub, ast.UAdd, ast.Not)):
            value = self.expr(node.operand, env, stack)
            if isinstance(node.op, ast.UAdd):
                return value
            if value[0] == 'const':
                return ['const', -value[1] if isinstance(node.op, ast.USub) else float(not value[1])]
            return self.op('neg' if isinstance(node.op, ast.USub) else 'not', value)
        if isinstance(node, ast.Compare):
            previous = self.expr(node.left, env, stack)
            checks = []
            for operation, right in zip(node.ops, node.comparators):
                if type(operation) not in self.compare:
                    raise ValueError('unsupported comparison')
                value = self.expr(right, env, stack)
                checks.append(self.op(self.compare[type(operation)], previous, value))
                previous = value
            result = checks[0]
            for check in checks[1:]:
                result = self.op('and', result, check)
            return result
        if isinstance(node, ast.BoolOp):
            result = self.expr(node.values[0], env, stack)
            for value in node.values[1:]:
                result = self.op('and' if isinstance(node.op, ast.And) else 'or', result, self.expr(value, env, stack))
            return result
        if isinstance(node, ast.IfExp):
            return self.merge(self.expr(node.test, env, stack), self.expr(node.body, env, stack), self.expr(node.orelse, env, stack))
        if isinstance(node, ast.Dict):
            result = {}
            for key, value in zip(node.keys, node.values):
                if not isinstance(key, ast.Constant) or key.value not in self.outputs or key.value in result:
                    raise ValueError('return only unique x/y/scale/rotation/opacity keys')
                result[key.value] = self.expr(value, env, stack)
            return self.bounded(result)
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name):
            name = node.func.id
            args = [self.expr(x, env, stack) for x in node.args]
            if name in self.functions:
                if node.keywords or len(args) != self.functions[name]:
                    raise ValueError('wrong arguments for ' + name)
                return self.op(name, *args)
            if name not in self.defs or name in stack or len(stack) >= 8:
                raise ValueError('unknown/recursive function or call depth limit: ' + name)
            function = self.defs[name]
            parameters = [a.arg for a in function.args.args]
            if len(args) > len(parameters):
                raise ValueError('too many helper arguments')
            bindings = dict(zip(parameters, args))
            for keyword in node.keywords:
                if keyword.arg not in parameters or keyword.arg in bindings:
                    raise ValueError('unknown/duplicate helper keyword')
                bindings[keyword.arg] = self.expr(keyword.value, env, stack)
            if len(bindings) != len(parameters):
                raise ValueError('missing helper argument')
            return self.block(function.body, {**self.globals, **bindings}, stack + (name,))
        raise ValueError('unsupported expression at line ' + str(getattr(node, 'lineno', '?')))

    def merge(self, condition, yes, no):
        if isinstance(yes, dict) and isinstance(no, dict):
            return self.bounded({key: self.op('select', condition,
                yes.get(key, ['const', default]), no.get(key, ['const', default]))
                for key, default in self.outputs.items() if key in yes or key in no})
        if isinstance(yes, dict) or isinstance(no, dict):
            raise ValueError('branches must return the same value type')
        return self.op('select', condition, yes, no)

    def block(self, statements, env, stack):
        for index, stmt in enumerate(statements):
            self.steps += 1
            if self.steps > 512:
                raise ValueError('expanded statement budget exceeded (512)')
            tail = statements[index + 1:]
            if isinstance(stmt, ast.Return):
                return self.expr(stmt.value, env, stack)
            if isinstance(stmt, ast.Expr) and isinstance(stmt.value, ast.Constant) and isinstance(stmt.value.value, str):
                continue
            if isinstance(stmt, (ast.Assign, ast.AugAssign)):
                targets = stmt.targets if isinstance(stmt, ast.Assign) else [stmt.target]
                if len(targets) != 1 or not isinstance(targets[0], ast.Name):
                    raise ValueError('assign a single local variable')
                name = targets[0].id
                self.name(name)
                if name in self.globals or name in self.functions or name in self.defs:
                    raise ValueError('reserved assignment name')
                value = self.expr(stmt.value, env, stack)
                if isinstance(stmt, ast.AugAssign):
                    if name not in env or type(stmt.op) not in self.binary:
                        raise ValueError('invalid augmented assignment')
                    value = self.op(self.binary[type(stmt.op)], env[name], value)
                env[name] = value
                if len(env) > 64:
                    raise ValueError('local variable limit')
            elif isinstance(stmt, ast.If):
                condition = self.expr(stmt.test, env, stack)
                return self.merge(condition, self.block(stmt.body + tail, dict(env), stack),
                                  self.block(stmt.orelse + tail, dict(env), stack))
            elif isinstance(stmt, ast.For):
                call = stmt.iter
                if (not isinstance(stmt.target, ast.Name) or not isinstance(call, ast.Call) or
                    not isinstance(call.func, ast.Name) or call.func.id != 'range' or
                    call.keywords or not 1 <= len(call.args) <= 3 or stmt.orelse):
                    raise ValueError('for requires a literal range of at most 64 iterations')
                values = []
                for arg in call.args:
                    sign = 1
                    if isinstance(arg, ast.UnaryOp) and isinstance(arg.op, ast.USub):
                        sign, arg = -1, arg.operand
                    if not isinstance(arg, ast.Constant) or type(arg.value) is not int or abs(arg.value) > 10000:
                        raise ValueError('range arguments must be literal integers within 10000')
                    values.append(sign * arg.value)
                try:
                    sequence = range(*values)
                except ValueError as error:
                    raise ValueError('invalid range') from error
                if len(sequence) > 64 or len(sequence) * (len(stmt.body) + 1) > 512:
                    raise ValueError('loop expansion limit')
                expanded = []
                for value in sequence:
                    expanded += [ast.Assign(targets=[stmt.target], value=ast.Constant(value=value))] + stmt.body
                return self.block(expanded + tail, env, stack)
            else:
                raise ValueError('unsupported statement at line ' + str(getattr(stmt, 'lineno', '?')))
        raise ValueError('every function path must return a value')

    def compile(self):
        result = self.block(self.defs['animate'].body,
            {**self.globals, **{name: ['var', name] for name in self.contexts}}, ('animate',))
        if not isinstance(result, dict):
            raise ValueError('animate must return a property dictionary')
        self.bounded(result)
        program = {'version': 2, 'outputs': result}
        if len(json.dumps(program)) > 65536:
            raise ValueError('compiled program size limit')
        return program


def compile_kinetic_script(source):
    module = ast.parse(source, mode='exec')
    if sum(1 for _ in ast.walk(module)) > 4096:
        raise ValueError('script AST limit (4096 nodes)')
    return KineticScriptCompiler(module).compile()


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
