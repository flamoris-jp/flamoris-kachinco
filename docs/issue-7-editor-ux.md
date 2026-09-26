# Issue #7 — reference audit and editor UX contract

Audit date: 2026-09-14. Completed before shell/interaction implementation.
Baseline: Kachinco `af1731cf56f1cd54786c36606de66bf4966ff410` (main, PR #6).
Family reference: Cutwork `7359db7538d9dc34f349715b1c8325537aaf5996` (main).

## References and decisions

Official sources (accessed on the audit date; web pages are not installed-version certification):

- [Adobe: Personalize the Timeline panel](https://helpx.adobe.com/lv/premiere-pro/how-to/working-with-timeline-panel.html): track headers/heights, video names/thumbnails, audio waveforms.
- [Adobe: Live waveform editing](https://helpx.adobe.com/premiere/desktop/add-audio-effects/adjust-volume-and-levels/live-waveform-editing.html), updated 2026-04-15: waveform feedback through edits, with an embedded Adobe Care timeline demonstration.
- Follow-up verification: [Adobe: Navigation controls in the timeline](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-sequence/navigation-controls-in-the-timeline.html), updated 2026-01-07. Inspected its official annotated timeline image in the browser: aligned fixed V/A headers, ruler-origin playhead, labeled video bodies, audio waveforms and lower zoom/scroll control. The embedded video on the waveform page did not play in this browser; no claim of watching that demonstration.
- [Blackmagic: current Resolve Edit page](https://www.blackmagicdesign.com/products/davinciresolve/edit) (page branded Resolve 21): media-pool placement, contextual edge trimming, inspector and timeline controls. Inspected the published timeline image in the browser: fixed left headers, image-filled clip bodies, name strips, vertical playhead, local toolbar. This current page includes older illustrative assets; do not claim every screenshot depicts the latest release.
- Cutwork `src/Cutwork.App/MainWindow.xaml` and `MainWindow.xaml.cs`: command icon styling, toolbar contextual panels, two right panels, status and Japanese/English localization application.

| Decision | Kachinco application |
| --- | --- |
| Adopt | Fixed headers, aligned lanes, names above video posters/audio peaks, distinct selection border, body-move and edge-trim cursors, continuous playhead, timeline-local zoom/fit/snap, compatible-track drop preview. |
| Simplify | One video poster per asset initially; bounded source waveform overview cropped to clip source range; fixed readable row height; single viewer; render-ahead playback with explicit progress. |
| Do not adopt | Seven-mode edit overlays, ripple/roll/slip/slide expansion, source patching, multicam, mixer, effects browser, keyframes, draggable panel docking, vendor colors/logos. |
| Inherit Cutwork | Native Windows Menu; locked ToolBarTray; 34×30 command buttons with tooltips; warm light toolbar #F6F3F1; dark work surface #202124, panels #2B2D31 and status #292B2F; restrained borders; 4/8px spacing; #D20B3A accent; right inspector; Japanese-first localizable labels. No literal image-tool rail. |

## Current implementation audit

- Import's handler returns early when Project is null. RegisterMedia itself requires Project, **not Sequence**. Probe before a single CreateProject + RegisterMedia batch on initial import. A cancelled/failed import must leave no project. Create a sequence separately through an obvious landscape/portrait action; preserve imported assets. Explicit New retains discard confirmation.
- Timeline uses a StackPanel for headers and an independently positioned Canvas for lanes in three ScrollViewers. Default content alignment and unequal scrollbar viewports can shift origins/clamp offsets differently. Replace synchronized vertical viewers with **one physical vertical scroller containing headers and lanes**. Ruler and lanes share one horizontal offset with equal viewport widths. Explicit top/left alignment; no centered oversized Canvas.
- Geometry currently duplicates 46px rows, 4px inset, header width and minimum clip width. Replace with one headless presentation geometry contract; width follows exact time extent, including tiny clips. Header/lane boundaries and trim hit areas use the same row record.
- Existing transparent Thumb template fixes native Thumb chrome, but gives no recognizable media content. Add bounded, asynchronous derived media visualization. Never modify Project or serialize thumbnails/peaks. Generation errors show placeholders with diagnostics, not synthetic waveforms.
- Play currently refuses unprepared previews. Preparation goes through an export-titled window; playing is a boolean set before MediaOpened. Replace with a transient playback coordinator: Preparing → Rendering → Preparing (decoder opening) → Playing; Paused, Stopped, Failed explicit. Play prepares automatically; Stop cancels preparation. MediaOpened gates playing; actual Windows media position drives ticks. Guard asynchronous work by generation + snapshot revision + sequence; old callbacks cannot publish current UI.
- SnapshotExportService → SharedFrameRenderer / SharedAudioRenderer → TimelineEvaluator remains shared by playback and export. Keep full-resolution render-ahead semantics for now, with honest latency/progress; MOV embedded audio remains excluded as documented in ADR 0002.
- Current tests cover basic pixels/ticks, placement/Undo/Redo, codec export, persistence and rendering, but not WPF row alignment, transport state/late callbacks or real clip visualization.

## Coordinate contract (DIPs, derived only)

`TimelineViewport` remains the sole time-scale projection over canonical `TimelineTime` (35,280,000 ticks/second).

- Content origin: time zero at lane/ruler Canvas X=0. `contentX = viewport.TicksToPixels(ticks)`.
- Visible lane coordinate: `viewX = contentX - horizontalOffset`.
- Surface coordinate: `surfaceX = headerWidth + viewX`; ruler and content share headerWidth.
- Pointer: obtain lane-host-relative X; add horizontalOffset **once**, then convert using `TimelineViewport.PixelsToTicks`. WPF Canvas-relative coordinates already include transforms and must not have scroll added again.
- Drop and ruler seek use this inverse and frame quantization. Drop additionally applies the snap toggle; ruler seek remains a direct frame-quantized pointer seek. Drop ghost shows the resulting snapped time, not a different raw position.
- Row i comes from one presentation ordering of immutable tracks. `top=i*rowHeight`, `bottom=top+rowHeight`, `clipTop=top+inset`, `clipHeight=rowHeight-2*inset`. Same half-open row bounds for header, separator, lane and drop hit testing.
- One vertical ScrollViewer moves the two row columns together. One horizontal ScrollBar applies identical translations to ruler and content. Header never receives horizontal translation. Both right viewports reserve the same vertical-scrollbar gutter.
- Drag commits one planner command from the original clip and absolute pointer displacement, never accumulated per-event deltas or WPF direct mutation.

## Acceptance strategy

Headless: geometry boundaries, exact clip widths, zoom/scroll round trips, rational-frame drop placement, lifecycle command batches and save/open + Undo/Redo; waveform sample aggregation/cropping, real small MOV/WAV visualization; playback transitions, actual-position conversion, failure, cancellation and stale-generation rejection.
Windows: solution build; startup smoke; measure actual Visual Tree positions after layout/scroll/resize; transparent gesture chrome; first-run guidance and command availability. Use existing Windows CI when this Linux environment cannot execute WPF. Physical density/readability, sound output and natural editing feel remain human acceptance. No fabricated before/after screenshots.

## Implemented scope and remaining limits

- First import probes before creating Project, without creating Sequence. The empty viewer then offers landscape/portrait sequence actions. New sequences begin at 8 seconds; placement still extends duration atomically. Existing saved durations are unchanged.
- `TimelineTrackGeometry` supplies 104-DIP headers, 72-DIP rows, 4-DIP clip insets and half-open hit bounds. Header bottom borders and lane separators paint the same final 1 DIP inside the row. Exact clip time widths are retained even below 8 DIPs; trim gestures may show a temporary minimum handle width.
- `MediaVisualizationService` generates a 160×90 first-source-frame poster or a mono 8 kHz/2,048-bin absolute-peak overview. Streaming PCM, a 60-second audio decode timeout, two concurrent workers and at most 128 placed assets bound derived work/memory. Missing/failed/omitted visuals are explicit. File path/size/mtime, source duration and kind invalidate the cache; none is Project authority. Waveform display crops by SourceIn/duration after each committed trim. Repeated posters, high-zoom waveform detail and live source-aware waveform updates while dragging remain future work.
- Play automatically renders the captured revision/sequence through the existing export service, then waits for Windows MediaOpened. Actual MediaPlayer position is the only playback clock; audio comes from the rendered WAV mix. Stop cancels rendering/opening; pause during preparation cancels pending autoplay; edits/Undo/Redo/sequence changes invalidate it. Render progress, decoder preparation, playback, pause, stop and render/native errors are visible. Native open has a 20-second timeout. A fresh native player per generation prevents old events from starting a newer request.
- Japanese is the shell default. Menu language switching changes shell resources, initial guidance and transport feedback without changing Project labels. Existing secondary authoring/export dialogs and domain diagnostics are not comprehensively translated in this issue.
- Render-ahead remains full-resolution and may be slow on long sequences. No real-time performance, physical speaker output, perceptual A/V sync or visual acceptance is claimed by automated startup/geometry tests. See [Windows checklist](../staging/windows-issue7.md) and [self-review](reviews/issue-7-self-review.md).
