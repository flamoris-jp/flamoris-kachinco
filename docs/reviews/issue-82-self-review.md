# Issue #82 implementation-author review

This is author self-review, not independent review or physical GPU acceptance.

- Boundary: no native editor/evaluator/time/project/schema changes; the existing
  native D3D11 compositor is reused through SharedFrameRenderer. Export owns its
  backend and disposal; preview selection/clock/history are untouched.
- Recovery: probe actually initializes the selected encoder at target size/fps;
  unsupported/timeout becomes CPU. Only encoder pipe/exit/stall/no-output failures
  replay once. Input/renderer/PCM validation and user cancellation do not replay.
  A retry begins at zero on the same immutable snapshot, preserves sticky compositor
  downgrade and reports the restart. CPU failure ends the job.
- Output/lifetime: partial media remains inside the existing adjacent temporary
  directory. Final move occurs only after video/audio/mux validation and cancellation
  checks. Join FFmpeg before deleting/reusing output, retain original destination on
  failure. Native process ownership handles the bounded probe and its cancellation.
- Memory: no sequence-sized raw spool or full-frame cache was added. Existing GPU
  payload and same-frame recovery limits apply; FFmpeg/NVENC driver memory is outside
  this accounting and must be measured. Each input frame remains bounded RGBA.
- Compatibility: same AAC/stereo/48 kHz and video timing/format, software decode,
  caption rasterizer, native pixel rules, image/automation/speed pipeline. MCP only
  adds diagnostic metadata to ready clip results; its descriptor stays identical.
- Tests: real-codec old paths and 37 focused cases pass locally; WARP is Windows
  only. Initial self-review corrected cancellation-before-retry guarding, first-error
  diagnostic ordering, encoder join before cleanup, and failed-attempt render timing.
  Full standard runner and actual WPF/package gates belong to CI. Native WARP parity
  is not evidence of NVIDIA performance or a physical quality/VRAM result.
- Choices: independent CPU controls preserve comparison and quality recovery.
  NVENC CQ 19 / p5 is documented as distinct from CPU CRF 18 / fast, not equivalent.
  AMF/QSV and zero-copy are deferred without a new speculative provider framework.

Automated result: Product CI #155 at `26763e604d22f22ccc6013f306b211d5ed67ec78`
passes Linux 321 tests/native sanitizers and Windows native WARP, 67 managed
ABI/codec/export cases, actual UI portrait export, package, external MCP and
startup. See https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/38891627059 .

Remaining: independent PR review, Mango four-way timings, total 6 GB
VRAM/repeated cancellation, quality/file-size and physical A/V assessment. Keep
#82 open for those measured acceptance items.
