"""Compiler -> actual native evaluator contracts, without the Windows shell."""
import ctypes
import json
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = None
if '--native-library' in sys.argv:
    index = sys.argv.index('--native-library')
    LIBRARY = sys.argv[index + 1]
    del sys.argv[index:index + 2]


def compile_source(source, success=True):
    result = subprocess.run([sys.executable, '-I', '-S', str(ROOT / 'product/Kachinco.Infrastructure/recipe-worker.py')],
        input=json.dumps({'mode': 'kinetic', 'source': source}), text=True, capture_output=True, timeout=6)
    value = json.loads(result.stdout)
    if success:
        if result.returncode:
            raise AssertionError(value)
    elif not result.returncode:
        raise AssertionError('accepted forbidden source: ' + source)
    return value


class Motion(ctypes.Structure):
    _fields_ = [(key, ctypes.c_double) for key in ('x', 'y', 'scale', 'rotation', 'opacity')]


class CompilerTests(unittest.TestCase):
    def test_legacy_and_structured_compilation(self):
        self.assertEqual(1, compile_source('flight(x=-20)')['version'])
        script = '''def wave(v):
    if v < 0:
        return -v
    return v

def animate(t, duration, i, n, seed):
    value = 0
    for k in range(1, 4):
        value += sin(t * k - i) / k
    if t < duration - 0.3:
        return {'y': wave(value)}
    return {'opacity': clamp((duration - t) / 0.3, 0, 1)}
'''
        ir = compile_source(script)
        self.assertEqual(2, ir['version'])
        self.assertIn('select', json.dumps(ir))
        self.assertEqual(ir, compile_source(script))

    def test_language_and_expansion_boundaries(self):
        forbidden = ['import os', "__import__('os')", 'open("x")', 'shake(amplitude=1+2)',
            'def animate(t, duration, i, n, seed):\n    while True: pass',
            'def animate(t, duration, i, n, seed):\n    return {"x": t.real}',
            'def animate(t, duration, i, n, seed):\n    return {"x": [a for a in range(2)]}',
            'def animate(t, duration, i, n, seed):\n    return animate(t, duration, i, n, seed)',
            'def animate(t, duration, i, n, seed):\n    x=0\n    for k in range(65):\n        x+=k\n    return {"x":x}',
            'def animate(t, duration, i, n, seed):\n    x=t\n    for k in range(12):\n        x=x+x\n    return {"x":x}',
            'def animate(t, duration, i, n, seed):\n    return {"opacity": 1, "opacity": 0}',
            'def animate(t, duration, i, n, seed):\n    return {"x": noise(**{})}',
            'def animate(t, duration, i, n, seed):\n    if t: return {"x": 1}',
            'def animate(t, duration, i, n, seed):\n    return {"__class__": 1}',
            '@print\ndef animate(t, duration, i, n, seed):\n    return {}']
        for source in forbidden:
            with self.subTest(source=source):
                self.assertIn('error', compile_source(source, False))


@unittest.skipUnless(LIBRARY, 'native runtime not supplied')
class NativeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lib = ctypes.CDLL(LIBRARY)
        cls.lib.kn_text_evaluate_unit.argtypes = [ctypes.c_char_p, ctypes.c_uint32, ctypes.c_int64,
            ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(Motion)]
        cls.lib.kn_text_create.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
        cls.lib.kn_text_destroy.argtypes = [ctypes.c_void_p]
        cls.lib.kn_text_request.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]
        cls.lib.kn_buffer_size.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
        cls.lib.kn_buffer_copy.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32]
        cls.lib.kn_buffer_destroy.argtypes = [ctypes.c_void_p]

    def document(self, program):
        return {'version': 1, 'id': '11111111-1111-4111-8111-111111111111', 'width': 1280, 'height': 720,
            'fpsNumerator': 30, 'fpsDenominator': 1, 'durationTicks': str(5 * 35280000), 'clips': [{
                'id': '22222222-2222-4222-8222-222222222222', 'text': '愛乃 Á 🐱', 'startTicks': '0',
                'durationTicks': str(5 * 35280000), 'fontFamily': 'Yu Gothic', 'fontSize': 48,
                'color': [1, 1, 1, 1], 'strokeColor': [0, 0, 0, 1], 'strokeWidth': 1,
                'shadowColor': [0, 0, 0, .5], 'shadowX': 2, 'shadowY': 3, 'x': 100, 'y': 200,
                'seed': 4294967295, 'source': 'author source', 'provenance': 'test', 'program': program}]}

    def evaluate(self, ir, seconds, i=0, n=4, status=0):
        data = json.dumps(self.document(ir)).encode()
        output = Motion()
        result = self.lib.kn_text_evaluate_unit(data, len(data), round(seconds * 35280000), i, n, ctypes.byref(output))
        self.assertEqual(status, result)
        return output

    def test_exit_and_seek_determinism(self):
        ir = compile_source((ROOT / 'docs/examples/kinetai-exit-scatter.py').read_text())
        first = self.evaluate(ir, 0, 1)
        self.assertEqual((0, 0, 0, 1), (first.x, first.y, first.rotation, first.opacity))
        a = self.evaluate(ir, 4.825, 1)
        self.evaluate(ir, 2, 3)
        b = self.evaluate(ir, 4.825, 1)
        self.assertEqual(bytes(a), bytes(b))
        self.assertAlmostEqual(.5, a.opacity)
        self.assertNotEqual(a.x, self.evaluate(ir, 4.825, 2).x)
        self.assertEqual(0, self.evaluate(ir, 5, 1).opacity)

    def test_context_helpers_loops_and_lazy_domains(self):
        source = '''def f(a):
    return a * a

def animate(t, duration, i, n, seed):
    x = 0
    for k in range(1, 4):
        x += k
    return {'x': x + i / n, 'y': f(a=2), 'scale': sqrt(t-1) if t >= 1 else 1,
        'opacity': (t and 1 / t) if i else 0}
'''
        ir = compile_source(source)
        m = self.evaluate(ir, 0, 1)
        self.assertEqual((6.25, 4, 1, 0), (m.x, m.y, m.scale, m.opacity))
        m = self.evaluate(ir, 2, 1)
        self.assertEqual((1, .5), (m.scale, m.opacity))
        self.evaluate(ir, 0, 4, 4, status=1)
        self.evaluate(compile_source('def animate(t, duration, i, n, seed):\n    return {"x": 1 / t}'), 0, status=1)
        for expr, value in [('(-5)%3', 1), ('(-5)//3', -2), ('min(2,3)+max(2,3)', 5),
                            ('ceil(1.2)+floor(1.9)', 3), ('clamp(2,0,1)', 1),
                            ('lerp(2,4,0.5)', 3), ('smoothstep(0,1,0.5)', .5),
                            ('abs(-2)+cos(0)+sin(0)+tan(0)', 3),
                            ('1 if (t==0 or t!=0) and not t<0 else 2', 1)]:
            with self.subTest(expr=expr):
                ir = compile_source('def animate(t, duration, i, n, seed):\n    return {"x": ' + expr + '}')
                self.assertAlmostEqual(value, self.evaluate(ir, 0).x)

    def request(self, session, value):
        data = json.dumps(value).encode()
        buffer = ctypes.c_void_p()
        self.assertEqual(0, self.lib.kn_text_request(session, data, len(data), ctypes.byref(buffer)))
        try:
            size = ctypes.c_uint32()
            self.assertEqual(0, self.lib.kn_buffer_size(buffer, ctypes.byref(size)))
            out = ctypes.create_string_buffer(size.value)
            self.assertEqual(0, self.lib.kn_buffer_copy(buffer, out, size))
            return json.loads(out.raw)
        finally:
            self.lib.kn_buffer_destroy(buffer)

    def test_native_validation_and_history_are_authority(self):
        ir = compile_source((ROOT / 'docs/examples/kinetai-wave.py').read_text())
        doc = self.document(ir)
        session = ctypes.c_void_p()
        self.assertEqual(0, self.lib.kn_text_create(ctypes.byref(session)))
        try:
            original = self.request(session, {'action': 'get'})['document']
            result = self.request(session, {'action': 'edit', 'expectedRevision': 0, 'document': doc})
            self.assertTrue(result['success'])
            self.assertEqual(doc, result['document'])
            self.assertEqual(original, self.request(session, {'action': 'undo', 'expectedRevision': 1})['document'])
            self.assertEqual(doc, self.request(session, {'action': 'redo', 'expectedRevision': 2})['document'])
            for bad in [{'version': 2, 'outputs': {'x': ['evil', 0]}},
                        {'version': 2, 'outputs': {'x': ['var', 'wall_time']}},
                        {'version': 2, 'outputs': {'x': ['add', ['const', 1]]}},
                        {'version': 2, 'outputs': {'bad': ['const', 1]}},
                        {'version': 2, 'outputs': {}, 'extra': 1},
                        {'version': 2.5, 'outputs': {}}]:
                doc['clips'][0]['program'] = bad
                self.assertFalse(self.request(session, {'action': 'edit', 'expectedRevision': 3, 'document': doc})['success'])
            self.assertEqual(3, self.request(session, {'action': 'get'})['revision'])
        finally:
            self.lib.kn_text_destroy(session)


if __name__ == '__main__':
    unittest.main()
