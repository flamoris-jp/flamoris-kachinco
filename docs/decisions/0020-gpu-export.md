# ADR 0020: bounded GPU MP4 export

Status: proposed for Issue #82 / PR review. Supersedes ADR 0016's preview-only
restriction on composition; its pixel, resource and same-frame recovery contracts
remain unchanged. No project schema or timeline/effect/evaluation changes.

## Independent stages

Export owns a fresh `PreviewRenderBackend` through `ExportFrameRenderer`, never
shares the interactive device or selection, and consumes `SharedFrameRenderer`.
Auto preserves native CPU identity-copy optimization; explicit D3D11 permits a
controlled comparison. Captions, images, transforms, Screen/Normal, opacity and
speed still use the shared evaluator/renderer. Same-frame GPU recovery retains
at most 256 MiB of decoded inputs; compositor payload is independently capped
at 256 MiB. No production WARP fallback. Decode remains software; uploads and
readback remain necessary. These figures exclude NVENC/driver/decoder memory.

The encoder independently selects Auto / CPU / NVENC. Auto tries NVENC only on
Windows. NVENC explicitly requested on other hosts is useful for test/measurement;
it still falls back to CPU. The selected NVENC device is an explicit zero-based
FFmpeg `-gpu` index (default 0), independent of the D3D11 display adapter. A
bounded, actual encode of three black frames at the requested size/fps with the
same options tests initialization; an encoder-list entry is not evidence.
Probe timeout is eight seconds. Hardware video failure retries the entire video
from frame zero exactly once through libx264, removing the partial temporary MP4.
Rendered media sources must be replayable from zero for this retry. Rendering,
validation, cancellation and audio/mux failures do not trigger a hardware retry.
The compositor's sticky recovery state survives encoder retries. Output publishes
only after all video/audio/mux work succeeds, preserving existing files otherwise.

NVENC uses `p5`, `tune hq`, VBR-CQ 19, `b:v 0`, yuv420p, default GPU 0.
CPU retains `libx264 -preset fast -crf 18`. CQ 19 is a separate quality policy,
**not** a claim of equivalence to CRF 18. AMF/QSV can be additional encoder
selections behind this same rendered-RGBA boundary in a later measured change;
no new generalized provider framework is introduced here.

## Entry points and diagnostics

UI export has independent transient composition/encoding selections (Auto by
default). They never change project state/history or preview settings. Existing
MCP clip downloads use the same Auto compositor/encoder and retain their existing
size, time, cancellation and revocation limits. Current main has no full-sequence
MCP export tool; this change does not invent one. Download metadata reports the
actual encoding/composition diagnostics without changing tool inputs/catalog.

Results record chosen encoder, fallback reasons, attempted GPU/CPU frame counts,
peak charged compositor bytes and wall times. Video-pipeline elapsed includes
rendering, pipe backpressure and encoder drain; render elapsed is measured around
source frame acquisition, so their difference is not pure GPU encode time.
Hardware probe and failed attempts are included in total elapsed, and retries
restart progress at zero with a reason. Cancellation never retries hardware.

## Verification and acceptance

Use injected process scripts for deterministic missing/init/midstream hardware
failure, cancellation, replay, output protection and cleanup, plus real FFmpeg
CPU export/probe and native WARP pixel contracts on Windows. Test shared export
composition/caption/speed semantics through the actual export service. Keep CI's
Windows/package and Linux native/managed gates. See `staging/windows-issue82.md`
for four-combination comparison, quality/file-size and physical 6 GB acceptance.
No physical speedup or total-VRAM result is asserted before those measurements.

Primary references:
- https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/ffmpeg-with-nvidia-gpu/index.html
- https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html
- https://ffmpeg.org/ffmpeg.html
