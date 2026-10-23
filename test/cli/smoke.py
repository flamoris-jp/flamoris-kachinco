#!/usr/bin/env python3
"""Real CLI process/codec acceptance, independent of implementation internals."""
import hashlib
import json
import pathlib
import subprocess
import shutil
import signal
import sys
import tempfile
import uuid

binary = str(pathlib.Path(sys.argv[1]).resolve())

def run(*args, ok=0):
    p = subprocess.run([binary, *map(str, args)], capture_output=True, text=True, timeout=90)
    assert p.returncode == ok, (args, p.returncode, p.stdout[-2000:], p.stderr[-3000:])
    return json.loads(p.stdout) if p.stdout.strip() else None

def media(*args):
    subprocess.run(['ffmpeg', '-v', 'error', '-threads', '1', *map(str, args)], check=True, timeout=30)

def uid():
    return str(uuid.uuid4())

with tempfile.TemporaryDirectory(prefix='kachinco-cli-') as directory:
    root = pathlib.Path(directory)
    project = root / '0.fkproj'
    run('project', 'new', '--output', project, '--duration', '0.2')
    original = hashlib.sha256(project.read_bytes()).hexdigest()
    media('-f', 'lavfi', '-i', 'color=c=red:s=32x32:r=30:d=0.2', '-c:v', 'qtrle', root / 'source.mov')
    media('-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=0.2', root / 'source.wav')
    media('-f', 'lavfi', '-i', 'color=c=blue:s=32x32', '-frames:v', '1', '-threads', '1', root / 'still.png')
    previous = project
    for i, source in enumerate(['source.mov', 'source.wav', 'still.png'], 1):
        destination = root / f'{i}.fkproj'
        run('media', 'import', previous, '--source', root / source, '--output', destination)
        previous = destination
    info = run('project', 'info', previous)
    # This is the native persisted project contract, not a custom CLI model.
    persisted = info['project']
    if 'project' in persisted:
        persisted = persisted['project']
    sequence = persisted['sequences'][0]['id']
    assets = persisted['assets']
    video = next(a['id'] for a in assets if a['sourcePath'].endswith('.mov'))
    audio = next(a['id'] for a in assets if a['sourcePath'].endswith('.wav'))
    video_track, audio_track, subtitles = uid(), uid(), uid()
    duration = str(35280000 // 5)
    def clip(asset):
        return dict(id=uid(), mediaAssetId=asset, startTicks='0', sourceInTicks='0', durationTicks=duration, enabled=True,
                    appearance=dict(transform=dict(x=0, y=0, scaleX=1, scaleY=1, rotationDegrees=0), opacity=1, blend='Normal'),
                    audio=dict(gain=1, muted=False))
    caption = uid()
    commands = [dict(type='AddTrack', sequenceId=sequence, trackId=track, name=name, kind=kind)
                for track, name, kind in [(video_track, 'V1', 'Video'), (audio_track, 'A1', 'Audio'), (subtitles, 'S1', 'Subtitle')]]
    commands += [dict(type='InsertClip', sequenceId=sequence, trackId=video_track, clip=clip(video)),
                 dict(type='InsertClip', sequenceId=sequence, trackId=audio_track, clip=clip(audio)),
                 dict(type='AddCaption', sequenceId=sequence, trackId=subtitles,
                      caption=dict(id=caption, startTicks='0', durationTicks=duration, text='愛乃と美祥\nLIME CLI', enabled=True)),
                 dict(type='SetCaptionStyle', sequenceId=sequence, captionId=caption,
                      style=dict(fontFamily='Noto Sans CJK JP', fontSize=60, color='#FFFF88', outlineColor='#000000',
                                 outlineWidth=3.75, x=.5, y=.85, maxWidth=.84, alignment='Center', verticalAnchor='Bottom',
                                 shadowEnabled=True, shadowColor='#80000000', shadowX=2, shadowY=2))]
    edit_file = root / 'edits.json'
    edit_file.write_text(json.dumps(dict(commands=commands)), encoding='utf-8')
    edited = root / 'edited.fkproj'
    run('edit', 'apply', previous, '--commands', edit_file, '--output', edited)
    assert run('project', 'validate', edited)['valid']
    run('render', 'frame', edited, '--time', '0', '--output', root / 'frame.png')
    assert (root / 'frame.png').read_bytes().startswith(b'\x89PNG\r\n\x1a\n')
    run('render', 'frame', edited, '--time', '0', '--output', root / 'frame.jpg')
    assert (root / 'frame.jpg').read_bytes().startswith(b'\xff\xd8')
    run('export', edited, '--output', root / 'movie.mp4', '--encoder', 'cpu')
    probe = subprocess.check_output(['ffprobe', '-v', 'error', '-count_frames', '-show_streams', '-of', 'json', str(root / 'movie.mp4')], timeout=30)
    streams = json.loads(probe)['streams']
    assert streams[0]['codec_name'] == 'h264' and int(streams[0]['nb_read_frames']) == 6
    assert streams[1]['codec_name'] == 'aac' and streams[1]['sample_rate'] == '48000' and streams[1]['channels'] == 2
    if len(sys.argv) > 2:
        evidence = pathlib.Path(sys.argv[2]); evidence.mkdir(parents=True, exist_ok=True)
        for name in ['frame.png', 'frame.jpg', 'movie.mp4']:
            shutil.copyfile(root / name, evidence / name)
    assert hashlib.sha256(project.read_bytes()).hexdigest() == original
    before = (root / 'movie.mp4').read_bytes()
    run('export', edited, '--output', root / 'movie.mp4', ok=1)
    assert (root / 'movie.mp4').read_bytes() == before
    run('project', 'info', edited, '--typo', 'yes', ok=2)
    edit_file.write_text(json.dumps(dict(commands=[dict(type='DeleteClip', sequenceId=sequence, clipId=uid())])))
    run('edit', 'apply', edited, '--commands', edit_file, '--output', root / 'invalid.fkproj', ok=1)
    assert not (root / 'invalid.fkproj').exists()
    (root / 'source.mov').unlink()
    assert not run('project', 'validate', edited, ok=1)['valid']
    run('render', 'frame', edited, '--time', '0', '--output', root / 'missing.png', ok=1)
    assert not (root / 'missing.png').exists()
    if sys.platform == 'linux':
        run('project', 'new', '--output', root / 'long.fkproj', '--duration', '30')
        interrupted = subprocess.Popen([binary, 'export', str(root / 'long.fkproj'), '--output', str(root / 'cancelled.mp4')],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        assert interrupted.stderr.readline(), 'Expected export progress before cancellation'
        interrupted.send_signal(signal.SIGTERM)
        stdout, stderr = interrupted.communicate(timeout=30)
        assert interrupted.returncode == 130, (stdout, stderr)
        assert not (root / 'cancelled.mp4').exists()
    assert not list(root.glob('.kachinco-*'))
print('CLI real-codec smoke passed: import/edit/save/reopen, Japanese captions, PNG/JPEG, 6-frame H.264/AAC, missing input and output preservation.')
