# Issue #7 — self-review

Review date: 2026-09-14. Baseline main: `af1731cf56f1cd54786c36606de66bf4966ff410`.
Reference/design decisions were committed before implementation in
[the audit](../issue-7-editor-ux.md). Final acceptance still requires the
[Windows hands-on checklist](../../staging/windows-issue7.md).

## Findings and disposition

| Area | Review outcome |
| --- | --- |
| Authority | No Project schema changes, persistent UI fields, second timeline/time model, direct WPF Project mutation or MCP protocol changes. Import, sequence creation and edits use EditorSession commands and its Undo/Redo history. |
| Lifecycle | First import creates Project + media in one revision only after successful probing. Sequence creation is separate and preserves the bin. Cancel/failure cannot create an empty Project. Existing Open/discard and revision guards remain. |
| Track layout | One row geometry and one vertical scroller replace independently aligned/clamped columns. Header and lane separators paint within identical row bounds. Native Thumb chrome is explicitly transparent. |
| Coordinates | Header origin, ruler origin, clip/playhead X and drop inverse share the documented scale/offset contract. One horizontal value translates both canvases; the scrollbar gutter is reserved equally. Drop preview uses the committed quantization/snap calculation. |
| Media | Posters and streamed peaks are transient Infrastructure projections. WPF only converts frozen display data. Cache work is bounded and stale/cancelled results cannot replace the current entry. SourceIn controls waveform cropping; missing files are not drawn as fake silence. |
| Playback | Play initiates shared snapshot render/export, then waits for native open. UI updates read actual MediaPlayer position; there is no redraw-driven timer. States and failures are explicit. Revision/sequence invalidation and per-generation native players reject stale work/events. |
| Race found and fixed | Starting final export while preview preparation was pending could leave an autoplay request active. Pause now cancels preparation. A dedicated regression covers the pending-native-open case. |
| Native failure found and fixed | Native seek, position polling and transport exceptions could escape UI handlers. They now become Failed with the original reason; tests cover read, seek, pause, stop and resume. |
| Gesture issue found and fixed | Async visualization/layout refresh could replace active Thumbs. Rebuild waits while a gesture is active; explicit model/zoom/snap changes cancel it. Pointer displacement is measured from the original gesture, not accumulated Thumb deltas. |
| Paint order found and fixed | Later lane backgrounds could cover a clip while it moves across rows. Clip/caption layers now sit above every lane background; the playhead sits above clips. WPF semantic checks protect the ordering. |
| Localization | Japanese-first shell resources have live English switching and matching keys. Existing secondary dialogs/domain diagnostics remain partly Japanese/English; complete application translation is not claimed. |

No unresolved implementation blocker was found in this review. This is a source and
automated-contract review, not a claim of physical Windows visual/audio approval.

## Automated evidence

[Product CI run 34848044215](https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/34848044215)
on `0e60343f453d5ed45d7196fcd86ecfa1d102f4d2` passed:

| Gate | Evidence |
| --- | --- |
| Existing + new headless suite | 105 passed, 0 failed, 0 skipped; real FFmpeg MOV/WAV fixtures included. |
| Windows solution build | `dotnet build Kachinco.slnx -c Release`: 0 warnings, 0 errors. |
| WPF layout | Actual Visual Tree header/lane boundaries, clip bounds, ruler/playhead/clip origins across resize, zoom, Fit and vertical/horizontal scroll passed. Additional explicit separator-paint and one-frame-width assertions are included in the final branch suite. |
| Startup / localization | Enabled initial import, actionable welcome text, Japanese/English resources and transport labels passed. |
| Windows existing gates | Recipe worker/raster/caption smoke, portable publish and executable startup passed. |
| Persistence / Undo / Redo | Existing suite plus new initial-import atomicity, sequence-free save/open serialization, stable identities, scrolled placement and Undo/Redo round trips passed. |

The PR records the final branch run, including the last separator/tiny-clip assertions.
This editing container is Linux without a .NET SDK; .NET/WPF results above came from
the repository's existing GitHub Linux/Windows jobs, not local execution.

New regression ownership:

- `EditorUxTests`: row bounds/hit tests; pointer/time conversions; zoom/scroll/rational-FPS placement; startup transactions and persistence/history.
- `MediaVisualizationTests`: polarity/silence/cropping; actual generated MOV pixel and WAV peak data; invalid/cancelled decode; unchanged source model.
- `PreviewPlaybackTests`: native-open gating, actual-position advancement, pause/stop/end, explicit preparation, render/native failures, cancellation, late callbacks and progress, native operation exceptions and pending autoplay cancellation.
- `TimelineLayoutChecks`: rendered WPF geometry, exact tiny-clip width, separator paint bounds, transparent gesture body and shell localization/startup guidance. No full-pixel golden screenshots.

## Remaining hands-on work

Check layout density/readability/discoverability at 100/150/200% DPI and minimum
window size; real dragging/trim feel; poster recognition and waveform readability;
cold/warm render latency; actual speaker output and perceptual A/V sync; final
preview/export comparison and normal Windows save/open interaction.

Before/after screenshots were not obtained reliably in this Linux workspace.
Official NLE images were inspected only as references; no vendor assets were copied
into the product and no generated screenshot is presented as acceptance evidence.
