# WPF ownership and extension boundaries

Audited for Issue #21 against main `0735221`. This maps the existing implementation;
it does not introduce an MVVM framework or change session authority.

## One editing authority

`MainWindow` owns one `EditorSession`. Views, dialog adapters and MCP read immutable
snapshots and commit typed commands through `EditorSession.Execute(EditBatch)`.
Open/New use the explicit `ReplaceProject` lifecycle. Undo/Redo, revision checks,
document tokens and MCP revocation remain attached to that same session.
Never put a mutable Project copy, a history stack or a second clock in a ViewModel.

## Current responsibility audit

Paths below are relative to `product/`.

| Existing home | Responsibility that stays there | Boundary for new behavior |
| --- | --- | --- |
| `Kachinco.App/MainWindow.xaml.cs`: dialogs, keyboard/selection handlers, `Refresh*`, `MediaAssetRow` | WPF controls, localization, transient selected IDs, busy/modal lifetime, snapshot and diagnostic presentation | Reusable projections may become App presentation models when they have an actual second consumer; brushes, dialogs and Dispatcher stay in App |
| `MainWindow.xaml.cs`: `Apply`, `PlaceMedia`, import/relink handlers | Thin session adapter: capture snapshot, call planner/probe, execute ordinary batch, refresh | Placement policy belongs in Core `TimelineEditPlanner`; startup/import batches in `EditorStartup`; IO/probe/relink policy in Infrastructure, never in picker handlers |
| `MainWindow.xaml.cs`: `AddSequence`, `Duplicate_Click`, `ApplyInspector_Click` | Existing small typed-command construction and text-to-value adaptation | If reused by another entry point, extract a pure Core planner taking the original snapshot/IDs and returning commands; Core validation remains authoritative |
| `Kachinco.App/MainWindow.Mcp.cs` | Endpoint lifetime, transient connection view, status/cursor and UI dispatcher/modal admission | `KachincoMcpHost`/`KachincoMcpTools` adapt the same session; transport, grants and guarded commit stay in Flamoris.Mcp.Core |
| `Kachinco.App/MainWindow.Production.cs` | Preview bitmap/status projection, waveOut binding, file/progress dialogs, caption/authoring window routing | Preview scheduling, sample clock, export, SRT parsing and Recipe compilation belong in existing Infrastructure services; no timeline evaluation in WPF |
| `Kachinco.App/TimelineSurface.xaml.cs`: drawing, hit testing, drag/trim handlers | Pixel-space gesture preview, capture/cancel, native Thumb behavior, scroll/zoom and command-intent events | Tick/pixel math, snapping, lane geometry and placement/move/trim/split policy stay in Core `TimelineAuthoring.cs` / `TimelinePresentation.cs` |
| `TimelineSurface.xaml.cs`: `LoadProject`, `Dispatch`, `Media_Drop` | Read-only projection of the committed snapshot; events carry stable IDs or typed commands | Surface never calls persistence or owns a session; the window adapter commits one batch and reloads the projection |
| `Kachinco.App/TimelineSurface.Media.cs` | Visible thumbnail plan, bounded work queue/cache, WPF bitmap/waveform drawing, cancellation on disposal | Decode and waveform generation use `MediaVisualizationService` / `FfmpegMediaDecoder`; reusable scheduling belongs in Infrastructure only if a non-WPF consumer needs it |

The existing partial files are navigation boundaries within one view, not separate
owners. Moving methods into more partials only to lower line counts is not a
refactor. This audit retains working code because reusable editing, probing and
playback behavior already has headless homes. Some command construction and
presentation projection remain in the shell deliberately; extract on demonstrated
reuse or independently testable policy, rather than adding empty service layers.

## Rules for the next feature

- A codec/container addition belongs in Infrastructure's import policy and
  `IMediaProbe` pipeline. File filters and drop hints project that policy; they do
  not establish media kind/duration without probing. Relink keeps stable IDs.
- A timeline edit belongs in a Core planner/command. Convert pointer position with
  canonical time utilities, derive from the original committed range, preview
  transiently, then commit once. Escape/cancel must leave history unchanged.
- IO belongs in an existing Infrastructure service where possible. Capture the
  expected revision for edits based on an earlier snapshot; stale work must fail
  instead of overwriting a later edit. WPF owns busy/modal scope across awaits.
- A new inspector/panel may get an App ViewModel for bindable transient state and
  immutable projections. It must not evaluate/render media or persist control values
  directly into Project. Do not introduce a mandatory MVVM rewrite for a new panel.
- Preview changes extend `InteractivePreviewController`, shared renderer/mixer and
  dependency caches. WPF redraw frequency must never advance `TimelineTime`.
- MCP additions adapt existing Product commands/queries and obey file-access grants;
  a new UI import capability does not implicitly authorize external file access.

## Verification by boundary

Core planner/command changes use headless authoring, editing, identity and Undo/Redo
tests. Probe/relink changes use real generated codec fixtures plus malformed-input
and compatibility tests. WPF changes require solution build/startup and relevant
Windows smoke checks; pointer/DPI/audio perception still needs staging checklists.
Documentation-only ownership changes need link review, not synthetic behavior tests.
Keep milestone evidence in [reviews](reviews/README.md), separate from contracts.
