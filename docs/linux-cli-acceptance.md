# Linux CLI: physical smoke acceptance — 2026-10-24 (JST)

This is a bounded physical LIME/AMD smoke result for CLI 0.1.1, following
[PR #105](https://github.com/flamoris-jp/flamoris-kachinco/pull/105),
[release publication #106](https://github.com/flamoris-jp/flamoris-kachinco/pull/106)
and [workspace support #107](https://github.com/flamoris-jp/flamoris-kachinco/pull/107).
The release is [cli-v0.1.1](https://github.com/flamoris-jp/flamoris-kachinco/releases/tag/cli-v0.1.1).
It supplements CI evidence; it does not close all of [Issue #100](https://github.com/flamoris-jp/flamoris-kachinco/issues/100).

## Observed from the external MCP client

- Read the headless resident project and Edit permission through the separate Linux namespace.
- Dry-run and commit an ordinary guarded batch: create a separate three-second
  sequence, add a subtitle track, add multiline Japanese/English captions and patch their style.
- Capture a composed JPEG at one second and visually inspect the Japanese glyphs,
  centered layout and green caption color.
- Explicitly save revision 2; project query reports `dirty:false`.
- Start a sequence export, poll completion and observe durable workspace publication.
- Read the complete MP4 through `export_read`: 39,889 bytes, offset 0 through
  39,889, `eof:true`; byte length matches the completed job.
- Probe the downloaded file and decode/visually inspect a frame at one second.
  This verifies actual returned bytes rather than relying on job metadata alone.

## Measured result

| Item | Observed |
| --- | --- |
| Content | Styled captions on black; no imported media or audible source |
| Video | H.264, 1920 × 1080, 30/1 fps, 90 decoded frames |
| Duration | 3.000000 seconds |
| Audio stream | AAC, stereo, 48 kHz; no audible source was supplied |
| Actual encoder | `h264_vaapi`; no encoder fallback |
| Export job total | 1,915.2177 ms |
| Composition | 90 CPU frames, 0 GPU frames |
| Durable publication / download | Completed / available |

The time is one short caption-only job's reported total, not a representative
MV benchmark or a CPU/GPU speedup comparison. VAAPI accelerates compression;
composition, captions, decoding and audio remain CPU work. CQP 20 is not
equivalent to x264 CRF 18.

## Still unverified on the physical deployment

- Image/video/audio upload and an edit using representative production material.
- Shared Undo/Redo exercise, controlled service restart and saved-project reload.
- Reboot, NAS/network outage and recovery.
- Cancellation, publication-failure recovery and live resource/quality benchmarks.
- Audible A/V synchronization and Windows reopen/path/font comparison.
- Simultaneous Windows/Linux operation; preservation of Windows registration
  alone does not establish live coexistence.

No runtime reconfiguration, restart, media deletion or credential cleanup was
performed for this smoke. Internal deployment paths, credentials and topology
remain in the private operations repository.
