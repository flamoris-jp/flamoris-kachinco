# Issue #67: GPU preview physical acceptance

Baseline: `v0.1-pre-gpu` / `b0d9bad`. Target: Mango, 6 GB dedicated VRAM.
Automated WARP tests establish rendering/lifetime contracts; WARP is CPU execution
and cannot establish physical GPU performance.

## Reproducible preparation comparison

Build current main on Windows, with FFmpeg available on PATH. In PowerShell:

```powershell
dotnet build Kachinco.slnx -c Release
dotnet run --project test/Kachinco.RuntimeBench/Kachinco.RuntimeBench.csproj -c Release --no-build -- --gpu-preview artifacts/gpu-fixtures artifacts/gpu-preview-mango.json
```

This generates public synthetic 1080p H.264 media and compares CPU, D3D11 and Auto
for identity and layered transform/screen composition at Full/Half/Quarter. It
records preparation plus BGRA latency/FPS, backend selection/fallback, compositor
payload bytes, parent-process CPU and software seek latency. It deliberately does
not report physical presented FPS, frame dropping, total GPU memory or GPU usage.
Parent-process CPU excludes FFmpeg children. A failed hardware initialization
must be shown as software decoding, never interpreted as GPU acceleration.

For the pre-GPU baseline, use a separate checkout at the tag and its existing
same-host driver, preserving generated media and tool versions:

```powershell
dotnet run --project test/Kachinco.RuntimeBench/Kachinco.RuntimeBench.csproj -c Release -- --preview artifacts/gpu-fixtures artifacts/pre-gpu-preview-mango.json v0.1-pre-gpu
```

The older driver compares identity preparation only; CPU versus GPU layered
results in the new driver share the exact evaluated project and source media.
Do not compare dissimilar workloads as evidence of a speedup.

## Physical editor checks

Record Windows version, GPU/driver, monitor connection/VM GPU assignment, FFmpeg
version, source codec/pixel format/FPS/resolution and quality. Compare forced CPU
and D3D11 using the same project. The backend selector and status report the active
composition/decode path; unsupported/10-bit decoder surfaces use software.

| Scenario | Required evidence |
| --- | --- |
| Single 1080p H.264, 8-bit | Actual D3D11VA confirmation, preparation/presentation latency and FPS |
| Two visual clips with scale/translation/rotation/opacity/screen | Visual reference parity and measured preparation improvement |
| Scrub forward/backward and repeated seek | Correct source time, no old frame publication, released/reused compositor pool |
| Clip transitions and prolonged Play | Stable queue/cache/memory, A/V synchronization |
| Full/Half/Quarter | Correct dimensions/placement, lower pressure at reduced quality |
| Deliberate overload | Current skipped-frame policy follows consumed audio clock |
| CPU selection and GPU fault/unsupported path | Editor remains usable; useful fallback reason and zero retained GPU texture pool |
| Project close/reopen and repeated quality changes | No growing decoder process count or dedicated GPU memory |

Use Task Manager/Performance Monitor to record editor **and FFmpeg** CPU, the
relevant GPU decode/compute engine utilization and total dedicated GPU memory.
Compositor metrics are texture payloads, capped at 256 MiB plus fixed accounting;
decoder surfaces and driver overhead are separate. Record peak memory with other
ordinary desktop workloads active. Avoid inferring a 6 GB total-memory guarantee
from compositor allocation alone.

Attach the JSON and a small comparison table to the issue. Mark #67 complete only
after representative transformed 1080p preview shows measured improvement and
these physical checks pass. A zero-copy decode/composite/WPF surface path is a
future explicit boundary change, not delivered by this milestone.
