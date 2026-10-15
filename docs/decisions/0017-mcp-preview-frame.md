# ADR 0017: read-only MCP preview frame

Accepted for Issue #72. `preview_frame` is a background Query over the existing
EditorSession. Hub advertises `kachinco.preview_frame`. It uses the ordinary Core
`{input, guard?}` envelope. Input requires sequenceId UUID and timeTicks (canonical
nonnegative decimal int64 string); output optionally supplies format (jpeg default,
or png), width (64–1920, default 960) and JPEG-only quality (1–100, default 85).
Unknown/duplicate fields and PNG quality are rejected. No seconds alias in v1.

Time is in [0, duration). TimelineTime.TicksToFrame rounds to nearest (ties forward), bounded to the final valid frame;
FrameToTicks supplies its canonical start. No clamp at the exclusive sequence end.
A guarded UI-lane read captures immutable ProjectSnapshot, document token and saved
project path. Native TimelineEvaluator and SharedFrameRenderer render the **full
sequence canvas**, on a worker, with WindowsCaptionRasterizer and native CPU
composition. Output downsampling happens after composition, preserving full-viewer
transform/caption semantics. It neither invokes InteractivePreview nor shares its
mutable forward decoder, GPU, playback clock or latest-wins queue. CPU and export
use the same evaluator/compositor. No UI selection/overlays are captured.

One capture per window, no queue; slot remains owned until work/encoding exits.
Core's 15-second request deadline carries cancellation through decode, render and
encode. Source: max 4096 on either axis, 8,294,400 pixels, 64 evaluated video layers;
output: max 1920 on either axis, 2,073,600 pixels, no upscale, aspect-preserving
floor to integer pixels. Width is a maximum, further reduced by height/pixel caps.
WPF JPEG/PNG encoders write an in-memory bounded 1 MiB stream; no output files.
JPEG quality is an encoder quality value, not a size guarantee. Oversize is an
explicit response_too_large failure; retry smaller/JPEG. Synchronous native/WPF
calls observe cancellation before/after; timeout never frees a still-running slot.
Full-canvas memory is bounded but capture can contend with playback for CPU/I/O.

A final guarded UI read rejects changed revision/document/path, including Save As
while decoding relative media. Metadata includes runtimeId/documentToken, revision,
sequenceId, requestedTimeTicks, resolvedFrameIndex/resolvedTimeTicks, dimensions,
MIME, byte count and effective quality. Common Core 1.3 image results emit metadata
as structuredContent and text plus one actual MCP image block. Core's pipe/stdio
and outbound Hub paths share serialization. 1 MiB binary fits their 4 MiB envelopes.
No base64 payload is duplicated in structuredContent.

Errors: invalid_request for malformed input; sequence_not_found/time_out_of_range,
capture_limit, busy, renderer_unavailable, response_too_large and renderer diagnostic
codes; Core supplies authorization/session/revision/cancel/timeout errors. No path,
raw media, export or arbitrary file capability is added.

Automated proof includes exact rational time, option limits, pixels/automation,
immutable history, mid-render edits/replacement, cancellation/concurrency, local
endpoint/packaged bridge images and Hub result forwarding. Physical Windows
viewer parity and the deployed authenticated tunnel/Hub require the new desktop
bundle and matching Hub catalog; keep that acceptance explicitly pending until run.


Windows process-lifetime prerequisite (#74): repeated codec teardown CI exposed
that cancelling a job and waiting only for its launcher/root is insufficient when
that launcher owns codec descendants. Native process destruction now also waits
for the terminated job's ActiveProcesses to reach zero, with a five-second bound
and no new public ABI/state. The existing root wait and reader joins remain.
This applies to capture cancellation and the ordinary preview decoder. A controlled
launcher/descendant holding an exclusive temporary file protects the lifetime
contract; immediate codec-source deletion assertions remain unchanged.
