# Production rendering and playback slice

Status: implementation proposal for Issue #5; subject to PR review.

The canonical evaluator remains the only timeline mapping. FFmpeg decodes MOV
frames and WAV sample blocks, but never receives a timeline filter graph. Decoded
video is fitted with preserved aspect ratio into a transparent project-sized RGBA
canvas. The existing transform acts on that canvas, about its top-left origin;
nearest-neighbour inverse mapping and the encoded-sRGB straight-alpha blend
reference define the initial CPU compositor. Output is flattened onto opaque black.

Audio output is 48 kHz stereo float PCM. Each output sample uses the canonical
sample-to-tick utility; contributions follow the shared evaluator. Mixing sums
contributions before a single [-1,1] clamp. MOV embedded audio remains excluded.

The first Windows playback adapter uses a rendered preview file produced by the
same snapshot exporter as final output. Windows MediaPlayer supplies actual
playback and media position; the adapter converts that position to canonical ticks.
UI redraw notifications never advance time themselves. Editing invalidates the
preview by project revision; no stale preview is presented as the current project.
Preparing a preview is visible and cancellable. Issue #7 makes Play prepare a cold
preview automatically; the explicit prepare-without-playing action remains.
This is render-ahead playback,
not a claim of interactive real-time full-resolution compositing.

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
