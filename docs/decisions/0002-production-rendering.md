# Production rendering and playback slice

Status: Issue #5 accepted; Issue #9 interactive playback amendment for PR review.

The canonical evaluator remains the only timeline mapping. FFmpeg decodes MOV
frames and WAV sample blocks, but never receives a timeline filter graph. Decoded
video is fitted with preserved aspect ratio into a transparent project-sized RGBA
canvas. The existing transform acts on that canvas, about its top-left origin;
nearest-neighbour inverse mapping and the encoded-sRGB straight-alpha blend
reference define the initial CPU compositor. Output is flattened onto opaque black.

Audio output is 48 kHz stereo float PCM. Each output sample uses the canonical
sample-to-tick utility; contributions follow the shared evaluator. Mixing sums
contributions before a single [-1,1] clamp. MOV embedded audio remains excluded.

Issue #9 replaces the initial render-ahead MP4 playback adapter. Ordinary Play uses
bounded forward decoded frames and mixed PCM, starting at the current playhead.
Windows waveOut's consumed sample frames are the playback clock; redraw notifications
never advance time. Scrub requests a single evaluated arbitrary-time frame, with a
one-slot latest-request mailbox. Full / half / quarter resolution changes output
sampling, not timeline/evaluator semantics. Device buffering is at most 500 ms;
source decode streams are capped at two seconds/64 video frames. No full-sequence
file is required. See [the interactive contract](../issue-9-interactive-preview.md)
for dependencies, invalidation, memory bounds, timestamp handling and overload.
Playback quality and caches are transient; persistent schemas remain unchanged.
Hardware decoding/proxies and universal real-time performance remain unclaimed.

The encoder accepts only rendered frames and mixed audio. It writes temporary
media beside the output and publishes the MP4 only after successful completion.
Cancellation terminates children and removes incomplete output. An existing output
must be preserved on failure. Source files must never be overwritten by export.

A typed caption rasterizer supplies transparent caption layers to the shared
compositor. The initial Windows implementation uses the same fixed font/layout in
preview and export; output reproducibility is scoped to the same font/runtime.
No screen capture is used. Styling persistence requires a separate schema decision;
the first caption renderer uses a documented fixed product style.

Acceptance requires headless image/audio fixtures, real FFmpeg output probing for
both presets, Windows build/startup, and separately recorded human playback/visual
inspection. Later Clapper/Recipe schema and worker capabilities need their own ADR.
