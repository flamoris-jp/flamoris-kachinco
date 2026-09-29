# Issue #34 reproducible runtime evidence

These are codec preparation/throughput measurements, not physical device-paced
playback or perceptual acceptance. There is no universal real-time guarantee.

## Same-host Linux comparison

Driver: `test/Kachinco.RuntimeBench/Program.cs`. Run the identical driver against
the reviewed #33 tree (`f797b4a`, same tree as merge `b60f9a9`) and the #34 runtime
(`510bd048`). Copy the benchmark project into the baseline checkout; its public
Product APIs are unchanged. Build Release, then run sequentially with one shared
fixture directory, output JSON path and revision label as its three arguments.

Machine: Ubuntu 24.04.3, .NET 10.0.12, AMD EPYC 9V74 virtual host, 8-vCPU quota,
approximately 9.7 GiB visible memory. Generated 1920x1080/30 testsrc2 MOV (3 seconds,
H.264 ultrafast/GOP 30) and 48 kHz sine WAV. The sequence has 110 adjacent two-second
clips on each video/audio track, 220 seconds total. Preview is Quarter (480x270);
export is 1920x1080 H.264/AAC, 30 frames and 48,000 stereo sample frames.

Three sequential before/after pairs, wall-time medians in milliseconds:

| Workload | Before | After |
| --- | ---: | ---: |
| Cold seek at 111 seconds | 115.03 | 98.40 |
| Cached same-frame seek | 1.58 | 1.69 |
| First frame plus 9,600 PCM sample frames | 111.01 | 105.54 |
| 12 frames spanning a clip boundary | 1,257.82 | 1,106.93 |
| 180 sequential frames plus periodic PCM | 3,949.35 | 3,296.78 |
| One-second full-resolution export | 7,895.25 | 9,413.40 |

Raw repetitions, environment and exact fixture description are in
`issue-34-linux-repeated.json`. The first before result is also preserved separately.
Export is **not demonstrated faster**: baseline ranged 4,604.55–13,775.50 ms, native
7,031.80–12,338.59 ms. This shared-host variability and the higher native median must
not be omitted or converted into a speedup claim. All six exports completed with
the same byte length (1,142,474); correctness rests on independent real-export and
pixel/PCM conformance tests, not byte length or timing. Cache charge/entry counts
are identical for the sequential workload. No benchmark timing is a flaky CI gate.

## Windows and physical acceptance

The pre-cutover Windows measurement is `issue-34-windows-before.json`, from run
36591434724 at reviewed #33. The unchanged Windows smoke records Full/Half/Quarter
cold/cached seeks, 60 sequential frames, bounded startup for 8/219.6-second sequences,
decoder process counts, CPU and sampled memory. Native Windows pipe buffers now
request 64 KiB to reduce polling stalls while keeping fixed per-child pipe bounds.

Windows CI reports `native_playback_unavailable` when no audio output device exists;
those entries are not passes for physical A/V. The final reviewed run's metrics and
portable ZIP are workflow artifacts. Follow `staging/windows-native-phase2.md` on a
physical Windows machine before closing #34/#28. #29 and #30 remain out of scope.
