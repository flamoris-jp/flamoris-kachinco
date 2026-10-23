#!/usr/bin/env python3
"""Validate through the actual Updater; real installation is disposable-CI only."""
import argparse
import hashlib
import json
import os
import pathlib
import secrets
import shutil
import socket
import subprocess
import tempfile

from flamoris_updater_adapters.managed import Catalog, Manager, Start


def verify(catalog_path, install=False):
    catalog = Catalog.model_validate_json(catalog_path.read_bytes())
    if len(catalog.recipes) != 1 or catalog.recipes[0].application_id != 'flamoris-kachinco-cli':
        raise ValueError('Expected one CLI release candidate')
    if install and (os.geteuid() != 0 or os.environ.get('GITHUB_ACTIONS') != 'true'):
        raise ValueError('Actual installation requires a disposable root CI runner')
    recipe = catalog.recipes[0]
    with tempfile.TemporaryDirectory(prefix='kachinco-updater-') as directory:
        root = pathlib.Path(directory)
        media = root / 'media'
        media.mkdir(mode=0o755)
        manager = Manager(root / 'state', root / 'apps')
        manager.catalog = lambda refresh=False: catalog
        manager.accept_catalog(catalog)

        def download(url, limit, destination, expected):
            source = catalog_path.parent / url.rsplit('/', 1)[1]
            if source.stat().st_size > limit:
                raise ValueError('Archive exceeds Updater limit')
            with source.open('rb') as stream:
                actual = 'sha256:' + hashlib.file_digest(stream, 'sha256').hexdigest()
            if actual != expected:
                raise ValueError('Archive digest mismatch')
            shutil.copyfile(source, destination)

        manager._download = download
        port = 8772
        if install:
            with socket.socket() as available:
                available.bind(('127.0.0.1', 0))
                port = available.getsockname()[1]
        values = dict(PORT=str(port), HUB_ENDPOINT='wss://127.0.0.1:9/desktop/kachinco-ci',
                      HUB_TOKEN=secrets.token_urlsafe(32), PERMISSION='edit', MEDIA_ROOT=str(media), ENCODER='cpu', DRM_RENDER_NODE='')
        job = manager.start('install', Start(application_id=recipe.application_id, release=recipe.release,
                            request_key='cli-repository-acceptance', settings=values))
        try:
            if not install:
                profile = manager._profile(recipe, job['job_id'])
                assert profile.applications[0].application_id == recipe.application_id
            else:
                manager.run_job(job['job_id'])
                recorded = manager.journal.get('managed_job', job['job_id'])
                assert recorded['phase'] == 'awaiting_setup', recorded
                manager.start_setup(recipe.application_id)
                completed = manager.complete(recipe.application_id)
                assert completed['phase'] == 'succeeded', completed
                project = root / 'apps' / recipe.application_id / 'projects/resident.fkproj'
                assert project.is_file()
                container = recipe.application['container_name']
                subprocess.run(['docker', 'exec', container, '/app/kachinco', 'project', 'new',
                                '--duration', '0.1', '--output', '/data/projects/acceptance.fkproj'], check=True, timeout=30)
                subprocess.run(['docker', 'exec', container, '/app/kachinco', 'export',
                                '/data/projects/acceptance.fkproj', '--output', '/data/exports/acceptance.mp4'], check=True, timeout=60)
                assert (root / 'apps' / recipe.application_id / 'exports/acceptance.mp4').is_file()
        finally:
            if install:
                subprocess.run(['docker', 'rm', '--force', recipe.application['container_name']], check=False,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=60)
    print(json.dumps(dict(catalog_validated=True, installation_performed=install, hub_connection_verified=False)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('catalog', type=pathlib.Path)
    parser.add_argument('--install', action='store_true')
    options = parser.parse_args()
    verify(options.catalog, options.install)
