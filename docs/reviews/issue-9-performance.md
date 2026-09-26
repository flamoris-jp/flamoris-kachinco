# Issue #9 Windows performance evidence

Measured on the [Windows CI run](https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/34860671577)
for `99a1dab127137c4fa6108d90bae5eb982d954626`, 2026-09-14. Windows NT 10.0.26100,
2 logical processors, FFmpeg 9.0.1 essentials. These are individual shared-runner
observations, not percentile estimates or a universal real-time promise. The final
sequence-end cursor fix does not change this measured decode path.

## Scrub and forward throughput

Three-second 1920×1080/30 H.264 MOV plus 48 kHz WAV; real source pixels/PCM, shared
compositor/mixer. The 219.6-second sequence contains the fixture at 110 seconds.
The 60-frame loop requests two seconds of visible content and 20 mixed audio blocks.
It is an unpaced throughput measurement, not actual device playback. Cold/cached scrub
numbers time the engine frame request; WPF paint/vsync and pointer-to-display latency
are excluded.

| Quality | Cold scrub ms | Cached scrub ms | 60 frames + PCM seconds | Host CPU seconds | Sampled host + FFmpeg peak MiB | Frame cache MiB / entries |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Full | 291.0 | 0.929 | 2.861 | 1.938 | 600.7 | 94.92 / 12 |
| Half | 280.8 | 0.135 | 1.720 | 0.969 | 472.5 | 94.92 / 48 |
| Quarter | 325.5 | 0.130 | 1.312 | 0.578 | 456.1 | 29.66 / 60 |

Each loop used one forward video process and one forward PCM process. Cache limits
held. Host CPU excludes FFmpeg CPU; combined working set is sampled, not a hard RSS
budget, and includes runtime/previous allocation effects. Full missed 30fps throughput
on this runner. Half/Quarter completed the two-second work below two seconds; this
alone does not establish smooth display, sound, sustained playback or A/V sync.

## Current-position startup versus total duration

Fresh forward decoders for every case, same real MOV/WAV placed at four seconds;
request begins at four seconds, with no earlier timeline evaluation. One evaluated
frame and two 4,800-sample stereo PCM blocks (200 ms) are prepared concurrently.

| Quality | Sequence seconds | Frame ready ms | PCM ready ms | Frame / PCM cache entries |
| --- | ---: | ---: | ---: | ---: |
| Full | 8 | 352.5 | 416.4 | 1 / 2 |
| Full | 219.6 | 414.4 | 486.0 | 1 / 2 |
| Half | 8 | 420.4 | 505.2 | 1 / 2 |
| Half | 219.6 | 398.9 | 457.4 | 1 / 2 |
| Quarter | 8 | 292.6 | 375.9 | 1 / 2 |
| Quarter | 219.6 | 174.2 | 243.8 | 1 / 2 |

The measured work stays fixed when duration grows by 27.45×. These are codec-ready
latencies; Play → first visible frame and Play → audible sound require a working
Windows output device and physical observation.

## Restart outlier investigation

Initial measurements included a 5,072 ms PCM preparation outlier. That run reused
forward decoder state across duration cases, so startup cases were made independent
and an explicit backward PCM restart was instrumented. A following run reproduced
4,035 and 5,592 ms restarts, while cumulative process creation was ~11–13 ms and
close was ~22 ms. The delay was outside process creation/termination.

Long-lived redirected stderr reads were competing with frame/PCM tasks on the
ThreadPool. Dedicated readers, one per bounded process, remove that competition.
After this change the six measured restarts were 120–387 ms, and Full's 60-frame
loop changed from 20.389 s to 2.861 s. This is evidence consistent with scheduling
starvation and the implemented remedy; it is not a guarantee that all long-tail
latency on physical machines is eliminated. No global ThreadPool tuning or weaker
Recipe process isolation was introduced.

Retained raw evidence, including the unfavorable observations:

- [Initial measurement](../../staging/evidence/issue9-initial-windows.json)
- [Fresh startup / restart audit](../../staging/evidence/issue9-restart-audit-windows.json)
- [After dedicated readers](../../staging/evidence/issue9-windows.json)

## Unavailable and remaining acceptance

All six native Play attempts returned Windows audio error 2: no usable output device
on the runner. Native Play → first picture/audio, real WAV sound, device-paced
sustained playback, actual A/V drift and perceptual overload behavior remain
**unverified**. They are not replaced with a fake timer/sink measurement.

Automated contracts cover consumed-sample timing, underrun freeze/refill, bounded
video skips and stale/edit handling. Windows verifies real viewer scrub pixels,
thumbnail strip, layout geometry, build/startup and worker/raster behavior. Follow
[the physical checklist](../../staging/windows-issue9.md) for the remaining acceptance.
