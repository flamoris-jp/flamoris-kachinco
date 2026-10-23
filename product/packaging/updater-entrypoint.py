#!/usr/bin/env python3
"""Application-owned container bootstrap; Updater owns deployment and restart."""
import http.server
import json
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import threading
import time


def main():
    binary = '/app/kachinco'
    project = pathlib.Path('/data/projects/resident.fkproj')
    exports = pathlib.Path('/data/exports')
    port = int(os.environ.get('KACHINCO_PORT', '8772'))
    if not 1 <= port <= 65535:
        raise ValueError('Invalid health port')
    endpoint = os.environ['KACHINCO_HUB_ENDPOINT']
    permission = os.environ.get('KACHINCO_PERMISSION', 'edit')
    encoder = os.environ.get('KACHINCO_ENCODER', 'vaapi')
    device = os.environ.get('KACHINCO_DRM_RENDER_NODE', '/dev/dri/renderD128') or '/dev/dri/renderD128'
    credential = os.environ.pop('KACHINCO_HUB_TOKEN', '')
    if not credential or len(credential) > 4096 or any(ord(c) < 32 for c in credential):
        raise ValueError('Configure a nonempty private Hub connector credential')
    if encoder == 'vaapi':
        subprocess.run([binary, 'gpu', 'check', '--vaapi-device', device], check=True, timeout=20)
    project.parent.mkdir(parents=True, exist_ok=True)
    exports.mkdir(parents=True, exist_ok=True)
    if not project.exists():
        subprocess.run([binary, 'project', 'new', '--output', str(project), '--name', 'Resident editor'], check=True)
    ready, stopping = threading.Event(), threading.Event()
    child = None

    class Health(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path != '/healthz':
                self.send_error(404)
                return
            alive = ready.is_set() and child is not None and child.poll() is None and not stopping.is_set()
            body = json.dumps({'status': 'ready' if alive else 'starting',
                               'hubReadiness': 'verify separately through Hub'}).encode()
            self.send_response(200 if alive else 503)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_args):
            pass

    server = http.server.ThreadingHTTPServer(('127.0.0.1', port), Health)
    server.daemon_threads = True
    with tempfile.TemporaryDirectory(prefix='kachinco-private-') as directory:
        token = pathlib.Path(directory) / 'hub-token'
        fd = os.open(token, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as output:
            output.write(credential)
        credential = None
        child = subprocess.Popen([binary, 'serve', '--project', str(project), '--output-root', str(exports),
                                  '--hub-endpoint', endpoint, '--credential-file', str(token), '--permission', permission,
                                  '--encoder', encoder, '--vaapi-device', device],
                                 stdout=subprocess.PIPE, text=True)

        def forward_output():
            for line in child.stdout:
                print(line, end='', flush=True)
                try:
                    if json.loads(line).get('status') == 'started':
                        ready.set()
                except (ValueError, AttributeError):
                    pass

        def stop(_number, _frame):
            stopping.set()
            if child.poll() is None:
                child.send_signal(signal.SIGTERM)

        signal.signal(signal.SIGTERM, stop)
        signal.signal(signal.SIGINT, stop)
        threading.Thread(target=forward_output, daemon=True).start()
        threading.Thread(target=server.serve_forever, kwargs={'poll_interval': .2}, daemon=True).start()
        deadline = None
        try:
            while child.poll() is None:
                if stopping.is_set():
                    deadline = deadline or time.monotonic() + 30
                    if time.monotonic() > deadline:
                        child.kill()
                time.sleep(.1)
            return 0 if stopping.is_set() and child.returncode == 130 else child.returncode
        finally:
            ready.clear()
            server.shutdown()
            server.server_close()
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        print(json.dumps({'error': 'resident_start_failed', 'type': type(error).__name__}), file=sys.stderr)
        sys.exit(1)
