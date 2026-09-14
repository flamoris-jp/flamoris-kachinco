# Kachinco production slice — Windows acceptance

## Start

1. Extract the portable ZIP into a writable folder; launch `Kachinco.App.exe`.
   .NET is included by the portable publish. Source builds require .NET 10 SDK.
2. Install FFmpeg separately and put `ffmpeg.exe` and `ffprobe.exe` on PATH.
   Kachinco does not download or bundle these executables.
3. Recipe generation additionally requires Python 3 (`python` on Windows PATH).
   The distributed `recipe-worker.py` must remain beside the app.
4. Import MOV/WAV directly from the welcome screen, then create a landscape or
   portrait sequence; alternatively use File > New. Save as `.fkproj`.
   Source MOV/WAV and generated MOV files remain external assets; keep them.

## Ordinary editing

- Import MOV/WAV; drag from the media bin onto a video/audio row.
- A drop places the full asset at the pointer's snapped time and extends sequence
  duration if necessary. One Undo reverses both changes. Empty-project default is
  8 seconds; Sequence > duration can change it without clipping existing items.
- Timeline header owns zoom, fit and snapping. Fit a one-hour sequence and verify
  both ends are visible. Drag clip bodies to move; edges trim. During each kind of
  drag, press Escape and verify the preview returns to its original range and Undo
  history is unchanged. Split uses the cursor.
- Add tracks from Sequence menu; duplicate from Clip menu. Inspector exposes
  enabled, position, scale, rotation, opacity, Normal/Screen, gain and mute.
- Subtitle menu edits captions and imports/exports SRT. Captions render bottom
  center in Yu Gothic with an outline. V1 files load and save as V2, preserving
  existing media/track/clip/caption identities. Back up before opening in old builds.

## Preview and output

- Play (Space) automatically prepares a cold preview, showing preparation/render
  progress. `プレビュー準備` explicitly prepares without autoplay. Both use the same
  compositor/audio service as final export. This remains render-ahead playback.
- After native media opening, play/pause, stop, frame stepping and timeline seek use the
  actual media player. An edit invalidates the prepared preview.
- Stop cancels pending preparation; render/decode/transport failures show a reason.
- File > export creates H.264/AAC MP4. Progress window supports cancellation.
  Existing output is replaced only after successful encoding; source paths cannot
  be used as output. Preview/export are flattened onto black. Generated MOV retains
  alpha. MOV embedded audio is not mixed; import WAV separately.
- First implementation decodes independently per requested frame/block. Preparing
  long sequences can be slow. Hardware decoding, proxy cache and performance
  optimization remain unclaimed until a production workflow is profiled.

## Clapper / Recipe

- Open Clapper / Recipe, enter name/range and optional point/rectangle in canvas
  pixels. Save. Named Clappers appear on the timeline ruler.
- Select a saved Clapper and enter e.g.:

```python
text(text="グエー", x=0, y=100, vx=180, size=64)
particles(count=24, x=100, y=200, vx=80, vy=-20, size=5)
```

- `検証` runs the restricted compiler. `生成 / 再生成` asks for a **new** MOV file.
  The proof supports up to 10 seconds, 128 calls and 2000 total primitives.
  `x/y` are relative to the Clapper origin, `vx/vy` pixels/second. Rectangle
  geometry clips drawing. Seed controls the deterministic particle layout.
- Generated output becomes an ordinary clip in one transaction. Select the saved
  Recipe to regenerate the same asset lineage. Existing clip positions/source
  ranges survive; incompatible shorter replacements fail. Old generated files are
  retained for Undo. Arbitrary imports/loops/attributes/calls are rejected.

## MCP

- AI connection > start/stop opens a read-only command string with a unique pipe ID.
- Configure a local stdio client to run `mcp/Kachinco.Mcp.exe --pipe <displayed-id>`.
  Keep this editor open. The endpoint is restricted to the same Windows user.
- Target protocol is MCP 2025-03-26. Tools query the visible project/context,
  apply typed batches with expectedRevision/dryRun, undo/redo, resolve Clappers,
  validate/generate Recipes and start/query/cancel export jobs.
- All Int64 command fields and revisions are decimal strings. Timebase is
  35,280,000 ticks/second. Query after any revision conflict.

## Human acceptance — not performed by CI

- [ ] Dragged clips land on the intended row/time at multiple zoom/scroll levels.
- [ ] Wheel/scroll, long-clip placement and pane sizes feel natural at actual DPI.
- [ ] Normal/Screen overlay looks correct; text is legible in both presets.
- [ ] Playback pause/seek and WAV synchronization are perceptually correct.
- [ ] Caption edit/import/export and save/reopen retain timing and text.
- [ ] A-1 + グエー generates; regenerate after moving/trimming the generated clip.
- [ ] Undo/Redo and reopen preserve authoring/provenance; old source files still exist.
- [ ] MCP client edit appears in the same visible timeline and shares Undo.
- [ ] Cancel preview/export/Recipe and close/reopen without stale dialogs or media.

Automated evidence is recorded in the PR; startup checks are not visual acceptance.
Run the focused [Issue #7 editor checklist](windows-issue7.md) for startup, clip
visualization, family styling, geometry, coordinate mapping and transport feedback.
Phase 9 remains needs-driven for performance and external integrations. Kinetai,
AudioAnalyzer, unrestricted Python, extra effects and broader media formats are
not implemented by this slice.
