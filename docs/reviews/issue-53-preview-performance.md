# Issue #53: preview performance and self-review

Baseline: reviewed main `e216577`, with metrics-only local commit `a688172`.
Implementation measured: local `dbc719f` (remote `a3dfa6a`), before documentation-only changes.
The identical `PreviewBench` driver was copied into the baseline checkout. Measurements
below are a consecutive baseline/after pair on the same Linux x64 host, .NET 10,
8 reported processors, using the same generated 1920x1080 30fps H.264 MOV. Each
quality prepares 60 sequential frames, then requests ten cached copies of the last.
Do not interpret this synthetic codec workload as physical Windows A/V acceptance.

## Results

| Quality | Frame preparation mean, before → after | Frame preparation p95, before → after | Pixel preparation mean, before → after |
| --- | --- | --- | --- |
| Full | 111.05 → 18.60 ms | 512.41 → 27.62 ms | 6.39 → 2.05 ms |
| Half | 106.76 → 6.06 ms | 130.28 → 8.99 ms | 1.43 → 0.66 ms |
| Quarter | 10.51 → 4.69 ms | 29.25 → 6.17 ms | 0.33 → 0.25 ms |

Preparation includes codec acquisition, composition, dependency keys and cache storage.
Cold startup is included and maxima remain larger than steady-state p95. Packet-poll
timers make baseline results variable; this is one comparable pair, not a universal
speedup claim. The first implementation without block reads still spent tens/hundreds
of milliseconds preparing large frames, which justified changing the pipe read path.

Before pixel preparation measures the actual previous `ToArray` plus managed channel
swap pattern. After includes native conversion on a worker plus Task/reflection overhead.
It excludes WPF WritePixels, which is measured separately by the Windows smoke/logger.
Per-presentation UI pixel-array allocations fall from 8,294,400 / 2,073,600 / 518,400
bytes at Full/Half/Quarter to zero. The owned BGRA allocation moves to preparation;
this is not a zero-copy renderer claim. RGBA caches and export remain unchanged.

In the deterministic producer-stall workload the consumed-sample clock advances to
tick 1,176,000 while frame two is deliberately blocked. Baseline still displays tick
zero; the new consumer displays the already prepared frame at 1,176,000 while the
producer remains blocked. Both report zero audio underruns in this test device.

Raw observations: [before](issue-53-linux-before.json), [after](issue-53-linux-after.json).
Reproduce on each checkout (copy `PreviewBench.cs` and the `--preview` dispatch into
the baseline RuntimeBench first):

```bash
dotnet run --project test/Kachinco.RuntimeBench/Kachinco.RuntimeBench.csproj -c Release -- --preview /tmp/kachinco-preview-fixtures /tmp/preview-results.json REVISION_LABEL
```

## Self-review and validation

- Native remains the generation, sample-clock and scheduling authority. Presentation
  does not reserve work; only one producer requests/decodes video, with ready plus
  in-flight frames bounded at three. It skips unrequested late frames and drops ready
  frames only when a newer ready frame is due.
- Cancel freezes the audio device and joins audio/video producers. Ignored source
  cancellation cannot publish into a later scrub/quality session. Both task failures
  are observed. Tests cover stalled production, backpressure, quality switching,
  failure clearing, boundaries, pause/resume/end, cache and immutable pixel ownership.
- Native worker reads retain one borrowed slice only in the call. An owner token and
  30-second deadline kill the owned writer to unblock reads. Native and managed tests
  cover packets larger than pipe capacity, short EOF, cancellation and process lifetime.
- BGRA preparation preserves straight alpha and does not mutate RGBA. New capability
  2048 makes startup reject an older DLL; ABI layouts and project formats are unchanged.
- Local full-suite attempt: 208/210 passed before the added block-read test; two MCP
  socket tests failed with `Permission denied` in this environment. They pass in Product
  CI. The final local run passes 209/209, excluding those two environment-blocked tests.
- Local ASan/UBSan contracts pass with `detect_leaks=0`; LeakSanitizer cannot inspect
  `/proc` under this execution environment. Product CI runs the full sanitizer gate.
- Windows Release build, native parity, worker/raster/viewer, portable/shared MCP and
  startup checks passed in Product run `36841333093` at code head `a3dfa6a`, alongside
  the full Linux contracts and sanitizer gate. Human A/V, representative private
  media and sustained working-set acceptance remain [pending](../../staging/windows-issue53.md).

Issue #53 remains open for that physical acceptance. Do not claim a verified 30fps
guarantee from this benchmark or a CI machine without an audio device.
