# Issue #9 repository-wide self-review

Reviewed against main `c0a3601321efd807d480a620ab2be13e16d5e4d0` (PR #8).
The reference/design note was committed before broad implementation. Official Adobe,
Blackmagic, FFmpeg and Windows references are in the [design note](../issue-9-interactive-preview.md).

## Authority review

- Core Domain and ProjectFormatV1/V2 are unchanged. No frame, quality, clock, thumbnail,
  second timeline or UI edit model was persisted. Canonical ticks remain 35,280,000/s.
- UI and MCP retain EditorSession/typed EditBatch/expected revision, shared history and
  evaluator queries. Auto-track placement uses ordinary AddTrack/ReorderTrack/InsertClip
  commands atomically, including duration extension. Explicit MCP-authored overlap keeps
  the existing deterministic schema/evaluator meaning and is visibly warned in the UI.
- TimelineEvaluator, SharedFrameRenderer, SharedAudioRenderer and Windows caption rasterizer
  are shared. FFmpeg only decodes sources or encodes/muxes final rendered output. Export
  presets, cancellation/overwrite protection, Recipe worker/IR and regeneration authority
  remain intact. Ordinary Play contains no SnapshotExportService or temporary MP4 path.
- The obsolete PreviewPlayback/WindowsPreviewPlayer and eight file-transport-specific tests
  were retired after the interactive transport tests covered their applicable risks. The
  exporter and independent render/export parity tests remain.

## Findings fixed during implementation/review

1. Old native position could overwrite a newly requested scrub while canceled work joined.
   Position reads now require the active playback session; generations reject late frames.
2. Audio failure could leave a disposed device in the controller field. Cleanup now clears
   ownership even if awaiting the producer fails, and reports the underlying failure.
3. A duration edit could retain the old endpoint. Duration conservatively restarts the local
   transport. Initial dependency fingerprints also protect already queued source content.
4. Pending Play intent could be lost when quality/context changed before playback started.
   The replaceable pending request now participates in intent preservation.
5. Independent raw PCM decode pads the final partial source block; the forward adapter now
   preserves this same tail rule instead of failing at the last incomplete output block.
6. Windows geometry smoke caught duplicate rows caused by scroll-change reentry during
   Rebuild. A rebuild guard fixes the defect; assertions still compare actual visual trees.
7. An existing coordinate fixture silently overlapped a populated lane. It now starts with
   an empty target; dedicated tests separately assert overlap rejection and atomic A2/V2.
8. Full-sized creation rows unnecessarily hid audio lanes. Compact creation rows consume
   the same precomputed geometry as headers, separators, hit tests and move placement.
9. Posters disappeared when a clip's left edge left the viewport. Actual source-sampled
   strips cover the visible intersection, with two workers, a replaceable 96-job plan and
   16 MiB/256-entry cache. Tiny cells cannot create negative WPF widths.
10. Opaque identity composition performed millions of small copy operations. An equivalent
    bulk-copy path preserves Full/export bytes; transformed/alpha/blend paths stay shared.
11. The unattended Windows fixture was marked dirty and opened a discard dialog at teardown.
    The test's injected snapshot is now marked clean; product save/discard behavior is intact.

12. Windows measurements exposed 4–5 second restart stalls outside process start/stop.
    Persistent redirected stderr drains now use one dedicated reader per bounded process,
    isolating them from the frame/PCM ThreadPool. Instrumented before/after results are
    retained in the performance note; Recipe kill/job isolation remains unchanged.
13. A cursor at the half-open sequence end must not seek at duration minus one tick.
    It now shows the final canonical output frame; Play from the end restarts at zero.

14. Counting only missing thumbnail keys could churn a working set larger than cache.
    The 96-key visible plan now counts cached entries too, culls vertically offscreen lanes
    and omits unreadably narrow cells. Excess detail shows a zoom hint instead of repeatedly
    decoding a fixed oversized view.
15. A two-entry forward-decoder pool rotated all processes when three source assets contributed
    to every frame or PCM block. Separate eight-entry video/audio LRU pools now amortize normal
    multi-track playback under a documented hard bound; real-codec three-source tests assert that
    process starts stay constant across subsequent frames and blocks.
16. Some waveOut devices may return `TIME_MS` after a `TIME_SAMPLES` request. The device clock now
    converts milliseconds, samples or bytes to 48 kHz sample frames and remains monotonic if the
    returned format changes.
17. Keying one forward stream by source path still churned when duplicated clips used different
    source-in times. Pool entries now represent independent source-time windows, so one path can
    retain several streams under the same eight-entry LRU bound. Real-codec interleaving tests cover
    three offsets for MOV and WAV plus explicit least-recently-used eviction at the hard limit.

## Regression coverage

- Scrub: arbitrary tick, 1,000 coalesced requests, cancellation-ignoring stale completion,
  failure clears viewer and no project mutation.
- Transport: nonzero start, no earlier timeline work, 8 vs 219.6 second bounded preparation,
  device-consumed sample clock, pause during preparation, pause/resume, scrub/stop, native
  and audio failures, edit inside/outside the queued window, underrun/refill and video skips.
- Cache: LRU byte/entry bounds, separate audio/video dependencies, localized trim/gain changes,
  unaffected ranges, relink/source replacement, Recipe regeneration and quality separation.
- Rendering: actual moving MOV pixels and PCM compare forward streams to independent seek;
  process startup is amortized; partial PCM tail and canceled requests; Full/export bytes and
  quality-scaled transforms, blend/opacity, source timing and caption inputs.
- Placement: exact existing track, overlap refusal, new A2/V2 below its compatible group,
  shared mixed-height row boundaries, one-action Undo/Redo and save/open identities.
- Windows: Japanese/English startup guidance, actual row/clip/ruler geometry across resize,
  zoom and scroll, playhead-to-viewer real MOV pixels, rapid scrub, visible thumbnail strip,
  hard visualization work/cache limits, build, worker/raster checks and portable startup.

## Evidence and remaining acceptance

The current Windows harness records real 1080p/30 MOV + WAV codec measurements and separately
attempts native device playback. See [Windows hands-on](../../staging/windows-issue9.md) and the
[performance evidence](issue-9-performance.md). CI audio-device absence is a limitation, not
passed sound/sync acceptance. Full throughput can be below 30 fps; default 1/2 and selectable
1/4, explicit dropped-frame counts and bounded buffering expose the degradation policy.

Physical sound, A/V sync/latency, prolonged playback, subjective scrub feel, density/readability,
DPI interaction and preview/export perceptual comparison remain Akino's checklist. No stable
before/after screenshots or human visual acceptance are claimed. No schema/authority blocker
was found; merge readiness still requires the final CI result and ordinary PR review.
