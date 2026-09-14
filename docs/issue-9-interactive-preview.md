# Issue #9 — interactive preview contract

Pre-implementation audit: 2026-09-14; main `c0a3601321efd807d480a620ab2be13e16d5e4d0` (PR #8).
AGENTS, Issue #9, Issue #7 design/implementation, evaluator/renderers/export/decoder,
transport/viewer wiring, visualization, authoring planner, MCP/session and existing tests were inspected.

## Official reference audit

- Adobe [timeline navigation](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-sequence/navigate-sequences-in-the-timeline.html): direct ruler click/drag and frame stepping.
- Adobe [monitor quality](https://helpx.adobe.com/premiere/desktop/get-started/source-and-program-monitor-adjustments/set-display-quality-for-the-source-and-program-monitors.html): independent playback/paused display resolution.
- Adobe [proxy workflow](https://helpx.adobe.com/premiere/desktop/organize-media/ingest-proxy-workflow/ingest-and-proxy-workflow.html), [media cache](https://helpx.adobe.com/premiere/desktop/troubleshooting/playback-issues/choppy-playback-and-poor-performance-issue.html), [render sequence sections](https://helpx.adobe.com/premiere/desktop/render-and-export/render-sequences-for-playback/render-a-section-of-a-sequence.html): distinguish source proxies, derived cache and explicit range rendering from normal playback.
- Blackmagic [current Edit page](https://www.blackmagicdesign.com/products/davinciresolve/edit), [Resolve 21 new features](https://documents.blackmagicdesign.com/SupportNotes/DaVinci_Resolve_21_New_Features_Guide.pdf?_v=1776322810000): direct timeline/scrub; current release includes subframe audio feedback (not added in this slice).
- Official [Resolve 20 Editor's Guide](https://documents.blackmagicdesign.com/UserManuals/DaVinci-Resolve-20-Editors-Guide.pdf?_v=1757574010000), [Beginner's Guide](https://documents.blackmagicdesign.com/UserManuals/DaVinci-Resolve-20-Beginners-Guide.pdf?_v=1757574013000), [Colorist Guide](https://documents.blackmagicdesign.com/UserManuals/DaVinci-Resolve-20-Colorist-Guide.pdf?_v=1757574010000): proxy preference, optimized media and optional render cache are separate controls. These are the guides linked by the current training page; oversized PDFs were consulted through indexed official excerpts, not claimed fully read.
- [FFmpeg seek](https://ffmpeg.org/ffmpeg.html#Main-options), [raw formats](https://ffmpeg.org/ffmpeg-formats.html), [showinfo](https://ffmpeg.org/ffmpeg-filters.html#showinfo): input `-ss` seeks to an earlier codec seek point and accurate decoding discards preroll. Output `-ss` alone decodes preceding input. Raw bytes carry no timestamp/header; a streaming adapter must carry source timestamps explicitly.
- Microsoft [waveOutGetPosition](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutgetposition), [waveOutWrite](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutwrite): actual device position, bounded prepared PCM buffers; inspect returned time format and reset position on device reset.

Adopt direct scrub, localized work, preview resolution and explicit buffering/drop feedback.
Keep Cutwork shell grammar. Do not add proxy asset management, GPU codec implementation,
full NLE routing, audio scrub audition or an automatic full-sequence preview export.

## Findings

`SharedFrameRenderer` already accepts evaluated arbitrary times; only the Play adapter forces
`SnapshotExportService` over every frame. `SeekPreview` does nothing before that MP4 exists.
Decoder process startup is paid for every frame/block. The compositor and audio evaluator can
be reused; changing their authority is unnecessary. The first poster is only about 71×40 DIPs
inside a long clip and remains at its left edge, so horizontal scrolling can hide the only image.
There is no group-level creation drop target, and Place permits silent same-track overlap.

## Responsibilities and semantics

1. **FFmpeg** demuxes/seeks/decodes MOV to timestamped RGBA and WAV to 48 kHz stereo PCM.
   Input seek may decode codec preroll, but Kachinco never evaluates earlier sequence ranges.
   Reuse bounded forward source streams during Play to amortize process startup; scrub uses
   cancellable accurate random seek. Unsupported/invalid stream metadata fails explicitly or
   falls back to the existing accurate random decoder, never guessed frame indices.
2. **Kachinco** keeps TimelineEvaluator, source-time mapping, track order, transforms, opacity,
   Normal/Screen, captions and audio summation/clamp. Generated Recipe MOV is ordinary media
   with provenance dependencies. Export remains shared evaluated/rendered RGBA + mixed PCM
   into FFmpeg encoder/muxer. Normal Play does not call SnapshotExportService.
3. **Scrub** requests one evaluated time and is silent. One running request + one replaceable
   pending request, cancellation and generation checks provide latest-wins; errors clear stale
   viewer pixels. **Play** begins at the current canonical tick, primes only one video frame and
   ~200 ms PCM, then maintains at most ~500 ms queued PCM and a few forward video frames.
   Duration affects endpoint checks, not startup frame count. Seek flushes device/worker
   generations; pause holds device position; stop returns to zero and displays that frame.
4. **Quality** is transient Full / 1/2 / 1/4 (default 1/2). The same compositor receives a
   scaled output target and equivalently scaled translation/caption raster dimensions.
   Timing, rotation, scale, opacity, blend and source sampling remain identical. Lower-resolution
   pixels differ by resampling/rasterization; Full is the export reference, not a second renderer.

## Derived cache and invalidation

5. Use separate byte-bounded LRU stores: composed video (~96 MiB), mixed PCM (~8 MiB),
   thumbnails (~16 MiB), also with entry limits. Keys include sequence/time or sample range,
   quality/size, ordered evaluated contributors, appearance/audio properties/caption text,
   stable media identity, resolved source path/size/mtime and generated provenance. No disk
   preview cache or Project fields. Decoder streams, active output buffers and WPF bitmaps
   have separate explicit bounds; cache size is not a claim of total process RSS.
6. Dependency fingerprints act as lazy range invalidation: a WAV edit cannot change video
   keys; an edit outside a frame/block's contributors preserves it. Move/trim/delete/enable,
   transform/blend, captions, relink and regeneration change affected keys. Undo may reuse
   a matching prior entry. File metadata is rechecked on request; explicit refresh/relink handles
   source replacement. Unknown semantic changes conservatively miss/restart. Selection,
   scroll and zoom do not enter preview keys. Workers verify their current generation and
   dependencies before publishing. Canceled work cannot publish under another request.
   During Play, edits outside the queued/in-flight window preserve readiness; affected-window
   edits reset audio at the actual current tick and re-prime locally while keeping unrelated cache.
7. **Audio clock:** actual output-device consumed sample frames, mapped as
   `startSample + consumedFrames` through TimelineTime.SampleToTicks at 48 kHz. Start is
   quantized to the next sample (less than one sample adjustment); no independent wall-clock
   timeline. Silent sequences still queue actual silence through the device. Scheduling delays
   only poll/wake work; they never advance playback time. No audio device is an explicit failure,
   not a timer fallback. The sequence-end editing cursor shows the final canonical output
   frame rather than attempting a source seek at duration minus one tick; Play at the end
   restarts from zero. Native buffers are freed only after reset/completion/unprepare.
8. **Overload:** preserve audio order/sample timing; drop late video work, keep bounded forward
   work and expose dropped frames/buffering. On PCM underrun, position stops at submitted
   samples; refill briefly and resume. Do not fabricate elapsed media time or silently export.
   The user can select 1/4; no unproven real-time guarantee. Scrub cancels playback and wins.

## Readability and placement

Show source-sampled poster strips across the visible clip intersection below the name.
Choose bounded sampling/density, cache by source time, coalesce viewport changes and use
at most two thumbnail decodes concurrently; no per-scroll unbounded task queue. Keep image
geometry visible when a long clip's beginning is offscreen. Preserve WAV source-range peaks.

Display explicit `+ Video track` / `+ Audio track` drop lanes after each visual group.
Existing compatible row means that exact track; group creation lane atomically adds V2/A2
and places through EditorSession. Reject accidental overlap in authoring placement/move/trim
with a useful diagnostic. Existing persisted overlapping content remains valid and gets a
visible overlap warning; deterministic existing compositor/mix semantics remain compatible.
Human UI and MCP continue to use the same commands/session; no direct Project edits.

## Verification plan

Headless: arbitrary/canceled/latest scrub, bounded queues/cache/streams, dependency reuse and
invalidation, Full parity and scaled geometry, actual clock transitions, nonzero Play with
8-second vs 219.6-second duration, auto-track/overlap atomic Undo/Redo/save-open, real MOV/PCM.
Windows: existing build/startup/geometry plus viewer pixels and thumbnail visibility; generated
1080p MOV/WAV cold/warm scrub, first-frame/first-device-progress latency, sustained throughput,
Full/Half/Quarter CPU/RSS/cache and overload metrics. Record unavailable audio devices as such;
test clocks do not establish physical sound/A-V acceptance. Perceptual checks remain Akino's.

## Implemented limits and review clarifications

- Thumbnail planning retains at most 96 desired jobs, two active jobs and 48 cells per
  visible clip. It replaces the pending viewport plan; obsolete active jobs are canceled.
  Source samples use a half-second grid clamped to the clip's source-in. The 16 MiB cache
  owns WPF thumbnail pixels or waveform peaks, with a 256-entry cap. Small bookkeeping
  overhead is included in the byte charge; runtime/decoder overhead is separate.
- Codec streams retain at most two video sources and two audio sources, each capped at
  two seconds; video additionally caps 64 frames and timestamp entries. Raw pipe backpressure
  bounds ahead-of-consumption bytes. Full identity opaque composition uses an equivalent
  bulk copy; alpha/transform/blend paths keep the shared reference equations.
- Creation rows are compact 24 DIPs; existing clip lanes remain 72 DIPs. Both header and
  content use the same precomputed TimelineTrackGeometry row boundaries and hit testing.
  Track creation inserts below the last displayed compatible lane. Labels count in display
  order, giving A1 then A2; IDs, not labels, remain authority. Commands are AddTrack,
  ReorderTrack, optional SetSequenceDuration, InsertClip in one expected-revision EditBatch.
  MCP can submit exactly that batch. Low-level explicit InsertClip remains compatible with
  existing overlap semantics; authoring gestures reject accidental overlap before submission.
  Intentional overlaps compose by the evaluator's stable start/ID order within each track,
  or sum PCM before clamp. The visible warning also covers explicit MCP-authored overlaps.
- Duration changes conservatively restart the local transport, even beyond the device window,
  so a paused producer cannot retain an obsolete endpoint. Other outside-window edits keep
  the current device queue. Initial source fingerprints protect queued content on relink or
  source replacement; per-frame/block cache keys recheck current file metadata.
- Selection/scroll/zoom do not call SetContext with a new revision. If another caller supplies
  an equivalent snapshot, deterministic dependency keys still reuse its frames/PCM.
- Scrub is deliberately silent and pauses Play. Paused quality is the selected playback
  quality too; there is no separate paused-resolution preference or proxy authoring UI.
