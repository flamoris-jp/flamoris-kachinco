#!/usr/bin/env python3
"""Decopon-only repair for the reviewed Linux CLI 0.1.2 Hub schemas.

Run with sudo; omit --apply to inspect the plan. Only the existing Linux
descriptor's input schemas change. Credentials, annotations and other upstreams
are retained. Applying restarts the Hub and reconnects its upstreams.
"""
import argparse
import copy
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import tempfile
import time

DIRECTORY = pathlib.Path('/srv/flamoris/apps/flamoris-mcp-hub/mcps')
CONTAINER = 'flamoris-mcp-hub'
CATALOG_URL = 'https://github.com/flamoris-jp/flamoris-kachinco/releases/download/cli-v0.1.2/cli-tools.json'
CATALOG_SHA256 = '37491c1796145319df1c44fc068a6d4c745a68f90e27c102afa157f22ae6e220'


def refresh_descriptor(original, catalog):
    if original.get('id') != 'kachinco-lime' or original.get('namespace') != 'kachinco_lime':
        raise ValueError('Refusing a different upstream')
    indexed = {tool['name']: tool for tool in catalog}
    names = [tool['name'] for tool in original['tools']]
    if len(indexed) != len(catalog) or len(set(names)) != len(names) or set(names) != set(indexed) or len(names) != 17:
        raise ValueError('Expected the same reviewed 17 Linux tools; no tools may be added or removed')
    result = copy.deepcopy(original)
    for tool in result['tools']:
        tool['input_schema'] = copy.deepcopy(indexed[tool['name']]['input_schema'])
    return result


def docker_python(code, payload=None):
    return subprocess.run(['docker', 'exec', '-i', CONTAINER, 'python', '-c', code],
                          input=payload, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          check=True, timeout=60).stdout


def replace_file(path, data, metadata):
    fd, name = tempfile.mkstemp(prefix='.kachinco-schema-', suffix='.tmp', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            os.fchmod(stream.fileno(), stat.S_IMODE(metadata.st_mode))
            os.fchown(stream.fileno(), metadata.st_uid, metadata.st_gid)
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if os.geteuid() != 0 or not DIRECTORY.is_dir():
        raise SystemExit('Run with sudo on Decopon, where the managed Hub catalog is mounted')
    downloaded = subprocess.run(['curl', '--fail', '--silent', '--show-error', '--location',
                                 '--max-time', '60', CATALOG_URL], stdout=subprocess.PIPE,
                                check=True, timeout=65).stdout
    if len(downloaded) > 2 * 1024 * 1024 or hashlib.sha256(downloaded).hexdigest() != CATALOG_SHA256:
        raise ValueError('Published CLI catalog digest mismatch')
    # Use the Hub container's existing YAML dependency, without installing packages on the host.
    captured = json.loads(docker_python('''
import hashlib, json, pathlib, yaml
matches = []
for path in pathlib.Path('/app/config/mcps').glob('*.yaml'):
    if path.name.startswith('_'): continue
    raw = path.read_bytes()
    value = yaml.safe_load(raw)
    if isinstance(value, dict) and value.get('id') == 'kachinco-lime':
        matches.append(dict(filename=path.name, digest=hashlib.sha256(raw).hexdigest(), descriptor=value))
if len(matches) != 1: raise ValueError('Expected exactly one Linux descriptor')
print(json.dumps(matches[0]))
'''))
    name = captured['filename']
    if pathlib.Path(name).name != name:
        raise ValueError('Invalid descriptor filename')
    path = DIRECTORY / name
    if path.is_symlink():
        raise ValueError('Refusing to replace a symlinked descriptor')
    original = path.read_bytes()
    if hashlib.sha256(original).hexdigest() != captured['digest']:
        raise ValueError('Mounted descriptor differs from the host file')
    updated = refresh_descriptor(captured['descriptor'], json.loads(downloaded))
    encoded = (json.dumps(updated, ensure_ascii=False, indent=2) + '\n').encode()
    docker_python('import sys; from flamoris_mcp_hub.config import UpstreamConfig; UpstreamConfig.model_validate_json(sys.stdin.read())', encoded)
    print('Validated: existing 17 Linux tool schemas; connection settings and annotations retained.')
    if not args.apply:
        print('Dry run. Add --apply to back up, replace the Linux descriptor and restart the Hub.')
        return
    if path.read_bytes() != original:
        raise ValueError('Descriptor changed during preparation; run again')
    metadata = path.stat()
    backup = path.with_name(path.name + '.before-cli-0.1.2-' + str(time.time_ns()) + '.bak')
    with backup.open('xb') as stream:
        os.fchmod(stream.fileno(), stat.S_IMODE(metadata.st_mode))
        os.fchown(stream.fileno(), metadata.st_uid, metadata.st_gid)
        stream.write(original)
        stream.flush()
        os.fsync(stream.fileno())
    replace_file(path, encoded, metadata)
    try:
        subprocess.run(['docker', 'restart', CONTAINER], check=True, timeout=60)
    except Exception:
        replace_file(path, original, metadata)
        subprocess.run(['docker', 'restart', CONTAINER], check=False, timeout=60)
        raise
    print('Linux descriptor updated. Backup: ' + str(backup))
    print('Allow the Linux client to reconnect, then verify kachinco_lime.mcp_context/get_project.')


if __name__ == '__main__':
    main()
