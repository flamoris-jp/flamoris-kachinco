# Issue #82: Windows GPU export acceptance

Automated tests and a successful request for NVENC/D3D11 do not demonstrate real
GPU acceleration. Record **actual** metrics/fallback, picture/audio and total
VRAM on Mango (6 GB); leave Issue #82's physical acceptance open until measured.

## Desktop and MCP

Build the complete current PR branch with the ordinary Windows packaging script,
with the running Kachinco closed. Keep FFmpeg/ffprobe available for the comparison
driver. Open an ordinary saved project; verify portrait and landscape separately.

File > Export acceleration has independent Video composition (Auto / CPU /
D3D11) and H.264 encoding (Auto / CPU / NVIDIA NVENC GPU 0) choices. Default is
Auto, choices last for this editor launch, and never change project Undo/history
or interactive preview. Output remains H.264 yuv420p plus 48 kHz stereo AAC
192 kbps. Explicit CPU restores the previous x264 fast / CRF 18 path. NVENC is
p5 / hq / VBR-CQ 19; CQ and CRF values do not imply equal quality/size.

Check that completion status and structured `render` log properties report
`acceleration`: actual encoder/device, reason for fallback, CPU/D3D11 frame counts,
peak charged compositor payload, total/render/video pipeline/audio/mux milliseconds.
Auto may use CPU for identity-copy frames. Payload excludes driver, NVENC and
other GPU allocations. Use Task Manager for total dedicated VRAM, not this field.

Existing MCP `clip_download_begin/status/read/cancel` needs no input/catalog
change. Request a transformed/automated MP4 clip and inspect ready metadata's
`acceleration`, then download and play it. WAV clips still produce PCM16 WAV;
MP4 clips remain silent, bounded to 30 seconds / 64 MiB. Verify cancellation,
permission revocation, document/revision changes, size bounds and expiry still
remove temporary outputs. There is no full-sequence export MCP tool in current
main. No Hub deployment is required for this additive result metadata.

## Reproducible four-way comparison

Use a saved project with one sequence and available sources. Prefer a 5–10 second
fixture containing video, PNG, transformed/Screen/opacity layers, caption,
visual and volume automation and a 2x/4x clip. Run both aspect ratios. Close the
editor/other GPU workloads. In a repository PowerShell with ffmpeg/ffprobe on PATH:

```powershell
dotnet run --project test/Kachinco.WindowsSmoke -c Release -- --export-bench C:\work\fixture.fkproj C:\work\export-measurements 0
```

Final argument is the explicit zero-based FFmpeg NVENC GPU index; compositor
selects its ordinary D3D11 display adapter independently. Each run creates a
unique output subdirectory and `measurements.json`, containing:

| Requested composition | Requested encoder | Meaning |
|---|---|---|
| CPU | CPU | Existing reference |
| CPU | NVENC | Encoding acceleration only |
| D3D11 | CPU | Composition acceleration only |
| D3D11 | NVENC | Combined |

Inspect actual GPU frames and encoder before classifying a row. A fallback row
is **not** a hardware measurement. Repeat three times and compare medians; note
FFmpeg version, driver, GPU names/index, OS and active background work. Source
decode remains software and can dominate the total. Rendering includes source
acquisition; pipeline includes rendering + transfer/backpressure + drain. Their
difference is not a pure device encode duration. Failed hardware attempt and probe
costs are included in total; GPU/CPU counts include replayed attempts.

During each named row record CPU percentage, Task Manager GPU 3D/Compute and Video
Encode usage and total dedicated GPU memory. If available, sample the selected
NVENC GPU from a second PowerShell:

```powershell
nvidia-smi --query-gpu=index,name,driver_version,utilization.gpu,utilization.encoder,memory.used --format=csv -l 1
```

Ctrl+C stops this monitor. For the benchmark, Ctrl+C cancels the current export;
existing completed rows/JSON remain. Check no `.kachinco-*` directory remains.
Record device/driver limitations if these counters are unavailable.

## Correctness, quality and recovery

- ffprobe each result: dimensions, rational FPS, exact video frame count, H.264,
  AAC / 48000 Hz / stereo, start time and duration. Inspect first, middle and final
  frame; listen at caption/cut/volume/2x boundaries and check A/V synchronization.
- Native Windows WARP tests enforce raw compositor byte parity before H.264.
  Driver comparisons should use the same transforms/alpha/caption corpus. H.264
  is lossy; compressed bytes or decoded pixels need not be identical.
- Compare the CPU/CPU baseline to CPU/NVENC (and GPU-composed equivalents) using
  decoded SSIM/PSNR and human inspection of text, gradients, motion and edges:

```powershell
ffmpeg -i Cpu-Cpu.mp4 -i Cpu-Nvenc.mp4 -lavfi "[0:v][1:v]ssim" -an -f null -
ffmpeg -i Cpu-Cpu.mp4 -i Cpu-Nvenc.mp4 -lavfi "[0:v][1:v]psnr" -an -f null -
```

  There is no universal SSIM threshold that proves visual parity. Record scores,
  actual file sizes and visible artifacts; choose CPU when NVENC quality/size is
  unacceptable. Never advertise NVENC CQ 19 as equivalent to x264 CRF 18.
- Force CPU, use an unavailable encoder/device and cancel during probe/video/
  CPU retry/audio/mux. Check existing destination sentinel remains on failure,
  temporary folders disappear, and retry occurs at most once from frame zero.
- Repeat exports and cancellation at least ten times; observe resources return
  to baseline after each completion/disposal and memory does not grow continually.
- GPU device-loss/recovery is covered by injected same-frame failures. On physical
  hardware test a safely reproducible unsupported device/format, rather than
  resetting the display driver during unrelated work.

Record measured results and remaining failures on #82; do not infer speedup from
passing WARP or from the presence of an encoder name.
