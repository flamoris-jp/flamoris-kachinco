#!/usr/bin/env python3
"""Prepare a repository-owned, digested Updater catalog. Never publishes it."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil

ROOT = pathlib.Path(__file__).resolve().parents[2]


def package(image, image_id, revision, output):
    release = json.loads((ROOT / 'release/updater.json').read_text())
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', image_id) or not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Immutable image ID and full source revision are required')
    if output.exists() or image.is_symlink() or not image.is_file():
        raise ValueError('Use a regular image archive and a new output directory')
    with image.open('rb') as stream:
        digest = 'sha256:' + hashlib.file_digest(stream, 'sha256').hexdigest()
    version = release['version']
    asset = 'linux-amd64--image.tar'
    url = f'https://github.com/flamoris-jp/flamoris-kachinco/releases/download/cli-v{version}/{asset}'
    settings = [
        dict(key='PORT', label='Local health port', default='8772', kind='port'),
        dict(key='HUB_ENDPOINT', label='Dedicated Hub WSS endpoint', default='', required=True),
        dict(key='HUB_TOKEN', label='Hub connector credential', default='', secret=True, required=True, kind='url_password'),
        dict(key='PERMISSION', label='MCP permission (edit/readOnly)', default='edit'),
        dict(key='ENCODER', label='Video encoder (vaapi/auto/cpu)', default='vaapi'),
        dict(key='DRM_RENDER_NODE', label='GPU render device (empty for CPU)', default='/dev/dri/renderD128'),
        dict(key='MEDIA_ROOT', label='Existing media directory (read-only)', default='', required=True),
    ]
    runtime = ('KACHINCO_PORT=${PORT}\nKACHINCO_HUB_ENDPOINT=${HUB_ENDPOINT}\n'
               'KACHINCO_HUB_TOKEN=${HUB_TOKEN}\nKACHINCO_PERMISSION=${PERMISSION}\n'
               'KACHINCO_ENCODER=${ENCODER}\nKACHINCO_DRM_RENDER_NODE=${DRM_RENDER_NODE}\n')
    recipe = dict(application_id=release['application_id'], release=version, platform=release['platform'],
                  compatible_from=release['compatible_from'], dependencies={}, settings=settings,
                  downloads={'image.tar': dict(url=url, digest=digest)}, generated={'runtime.env': runtime},
                  application=dict(application_id=release['application_id'], release=version, kind='docker',
                      directories=[dict(path='${root}/config'),
                                   *[dict(path='${root}/' + name, uid=10001, gid=10001, mode=488) for name in ['projects', 'exports']]],
                      files=[dict(source=dict(path='${package}/runtime.env', digest='generated', private=True),
                                  destination='${root}/config/runtime.env')],
                      health_url='http://127.0.0.1:${PORT}/healthz',
                      image_archive=dict(path='${package}/image.tar', digest=digest, private=False), image_id=image_id,
                      container_name=release['application_id'], environment_file='${root}/config/runtime.env', network='host',
                      drm_render_node='${DRM_RENDER_NODE}',
                      mounts=[dict(source='${root}/projects', target='/data/projects', read_only=False),
                              dict(source='${root}/exports', target='/data/exports', read_only=False),
                              dict(source='${MEDIA_ROOT}', target='/media', read_only=True)]))
    output.mkdir(parents=True)
    shutil.copyfile(image, output / asset)
    (output / 'catalog.json').write_text(json.dumps(dict(catalog_version=1, recipes=[recipe], updater_releases=[]), indent=2) + '\n')
    (output / 'candidate.json').write_text(json.dumps(dict(application_id=release['application_id'], release=version,
        platform=release['platform'], source_revision=revision, image_id=image_id, archive_digest=digest), indent=2) + '\n')
    return recipe


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', type=pathlib.Path, required=True)
    parser.add_argument('--image-id', required=True)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    package(args.image, args.image_id, args.revision, args.output)
