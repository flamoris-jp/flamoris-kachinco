# Native phase 2: physical Windows A/V acceptance

Use the portable ZIP attached to the exact reviewed #34 workflow run. Extract it
to a fresh directory, put external FFmpeg/ffprobe on PATH, and launch Kachinco.App.exe.
The ZIP contains the matching native DLL; do not mix files from another build.

Automated native/managed tests establish timing, pixels, bounded work, cancellation,
pause stale-result rejection and export structure. They cannot establish audible
continuity, perceived A/V sync or responsiveness on a physical Windows output device.
Issue #34 requires this remaining evidence before its merge; #28 stays open until
the reported physical clip-boundary pause behavior is confirmed.

## Focused check

1. Open a copy of the project that reproduced #28, or place two visually distinct
   MOV/MP4 clips adjacent on V1 and a WAV with known sync cues on A1.
2. Play through the clip boundary, then pause just after it. Compare the paused
   picture with scrubbing to that same time. It must show the new clip and must not
   revert to an earlier picture after a delayed decode completes.
3. Repeat Play/Pause five times across the boundary, seek backward, then resume.
   Position and held picture must stay aligned; no old audio queue should replay.
4. Play through the sequence end. The cursor stays at the end, the last output frame
   remains visible, and Play starts from zero.
5. At Full, Half and Quarter, listen/watch known sync cues for at least 30 seconds.
   Record audible dropouts, visible stale frames, A/V drift and startup/pause latency.
   A long sequence must start near the playhead without rendering its entire duration.
6. Export H.264/AAC MP4 and compare the boundary, Normal/Screen, transforms and
   captions with Full preview. Codec compression differences are expected; timing,
   layering and clip identity differences are failures.

Record commit SHA, Windows version, CPU/RAM, GPU, DPI, audio device, media codec/FPS,
FFmpeg version, quality and pass/fail notes. Logs can diagnose a failure; a screenshot
alone does not demonstrate A/V synchronization.

Status: not performed in the Linux work environment. Do not treat the test-only
simulated device clock or a CI `native_playback_unavailable` record as physical signoff.
Issues #29 (Inspector) and #30 (automation UI/domain/persistence) are outside this work.
