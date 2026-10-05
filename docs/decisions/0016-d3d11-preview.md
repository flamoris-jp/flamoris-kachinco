# ADR 0016: bounded D3D11 preview backend

Status: implementation and automated acceptance in progress (Issue #67).

## Authority and boundary

The checkpoint is `v0.1-pre-gpu`, commit
`b0d9bad8ac08c7b4fd2bb00432de29ac2c92ea5e`. Native TimelineEvaluator,
source-time mapping, consumed audio samples, latest-wins generations and late
frame policy remain authoritative. GPU acceleration consumes the same evaluated
layers as CPU preview/export. It adds no project schema, editing commands,
persistent effect state or separate renderer timeline.

Interactive preview can select Auto, CPU or D3D11. Export retains the native CPU
reference. D3D11 failure falls back to that CPU implementation; WARP is an explicit
test device and never an automatic production acceleration fallback.

## Delivered pipeline and transfers

The existing media adapter owns an FFmpeg executable process and receives raw
RGBA frames. The WPF presentation and bounded prepared-frame queue own immutable
RGBA/BGRA arrays. Preserve these contracts in this change:

1. Supported forward video requests can use FFmpeg D3D11VA, with an explicit
   hardware-frame download before the existing software scale/RGBA output.
2. Each evaluated RGBA layer is uploaded to one reusable native D3D11 texture.
3. Native HLSL composes the transform/opacity/blend primitives in two reusable
   ping-pong output textures, preserving layer order.
4. The completed frame is read back once into the ordinary immutable RGBA result.
   Existing BGRA preparation and WPF presentation then consume it.

This is a GPU compositor and hardware decode adapter, **not a zero-copy pipeline**.
Decoder and compositor use independent D3D11 devices; the current process boundary
cannot pass shared GPU texture ownership. GPU-resident decode-to-presentation
requires a future explicit libav/shared-surface ownership and WPF interop decision.
Do not claim that hardware decode necessarily improves performance: download and
upload can outweigh its benefit. Explicit CPU selection supports comparison.

## Pixel and effect contract

Output begins as opaque black. Sources are top-down tightly packed straight-alpha
RGBA8 in encoded SDR space, without an implicit sRGB/linear-light conversion.
Origin, inverse transform, nearest source selection, normal/screen blend and
per-layer byte quantization follow `kn_composite_rows`. Transform values and
trigonometry remain native CPU computations. Double-capable HLSL and the required
extended-double instructions preserve the evaluator's double input contract;
unsupported devices use CPU. WARP comparisons include transparent sources,
layer ordering, fractional/negative translations, nonuniform scale, rotation,
source edges and opacity. Byte tolerances never justify sampling a different
source pixel. Physical driver comparisons remain a separate acceptance step.

Transform, opacity and blend are the initial effect chain primitives. Existing
AI/MCP automation and restricted Recipes target the same evaluation path. This
change does not accept arbitrary shader source from project/AI input or add a
second effect catalog.

## Ownership, memory and recovery

One source texture, two output textures, one staging texture and a fixed constant
buffer are reused per compositor. Account for all retained texture payloads and
reject requests above the configured 256 MiB ceiling before allocation. This is
the **compositor allocation budget**, not total process or GPU memory usage.
FFmpeg decoder surfaces have codec/driver-specific sizes; hardware process count
is bounded separately. Driver allocation overhead is not reported as payload.
The target is a 6 GB VRAM machine, subject to physical acceptance.

Frame work is serialized across Begin/Composite/Read. Cancellation does not
publish incomplete output. Seek, quality/backend changes, project replacement
and shutdown join or serialize outstanding work before resetting resources.
Cached/presented frames own CPU arrays and retain no D3D textures. Allocation,
format, device creation and device-loss failures release GPU resources and rerun
the same evaluated request through CPU; cancellation propagates as cancellation.
Unsupported hardware decoding retries the same requested timestamp using the
existing software forward stream, rather than entering per-frame random seeks.

## Verification and outstanding physical acceptance

Automated gates cover Linux unsupported capability, injected backend failures,
CPU fallback, cancellation/lifetime, hardware argument/fallback semantics,
Windows WARP pixel parity, bounded allocation/reset and existing codec, clock,
cache, export, UI and package behavior.

Mango measurements must compare this backend with the checkpoint and forced CPU:
1080p H.264, layered transforms, seek, clip transitions and deliberate overload,
at Full/Half/Quarter. Record prepared/presented FPS, skipped frames, preparation
p95, CPU utilization, GPU utilization, total dedicated GPU memory and seek latency.
No Mango performance results exist at design time. Keep Issue #67's physical
performance acceptance open until those measurements exist.

Primary API references:

- https://www.ffmpeg.org/ffmpeg.html (hardware acceleration and transfer costs)
- https://learn.microsoft.com/en-us/windows/win32/direct3d11/how-to-use-direct3d-11
- https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nn-d3d11-id3d11device
