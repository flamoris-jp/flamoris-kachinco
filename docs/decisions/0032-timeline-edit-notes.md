# ADR 0032: Timed editing notes

Status: accepted for implementation from Akino's 2026-11-14 request.

## Problem and acceptance

A creator needs to place directions such as “pull back quickly here” over a time
range, adjust that range while watching the edit, and let MCP read the same notes.
Using actual subtitles risks publishing production instructions in the movie.

- Add a distinct `EditNote` track kind (numeric value 3). Reuse the canonical
  Caption entry, tick ranges and Add/Update/DeleteCaption commands. This is a
  caption-shaped authoring annotation, not a second text or timeline model.
- Native validates, persists and edits notes through the same transaction and
  Undo/Redo history. Empty/disabled note tracks are retained on save.
- Conditional schema v9 permits EditNote tracks and carries the complete v8
  envelope. Existing projects without notes retain their previous conditional
  v2–v8 output. Frozen v1–v8 inputs reject the new track kind. Old readers reject
  v9 rather than silently converting notes into subtitles.
- Native timeline evaluation returns kind 3 separately. The managed projection
  puts those entries in `EvaluatedFrame.EditNotes`, never `Captions`, video or
  audio. Shared preview/capture/export renderers never rasterize notes. SRT export
  selects Subtitle tracks only.
- Windows shows gold note clips on M lanes, with the existing caption move/trim,
  text inspector, list editing, enable toggle, delete, save and Undo/Redo. A
  transient “show editing notes” option displays active notes as a clearly labeled
  editor overlay using the native evaluator and existing playhead/preview clock.
  That WPF overlay is never part of rendered pixels or title capture.
- MCP reuses get_project/edit_batch, with AddTrack.kind=EditNote; no new tools.
  Producer catalogs reflect the enum automatically. A deployed Hub descriptor
  must be refreshed before this new enum is available externally.
- A native capability bit (1048576) prevents the updated managed host from loading
  a runtime that could confuse notes with captions.

Validation: native kind/codec/history contracts; managed persistence, guarded MCP,
caption-style/trim reuse, native activity and renderer/SRT exclusion checks;
Windows build and editor interaction smoke. Physical Windows interaction remains
an explicit acceptance item, not an inference from headless tests.
