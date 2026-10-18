# Caption timing gestures

Acceptance: move a timeline subtitle horizontally by dragging its body; drag
either shaded edge to adjust its start/end. Keep text, style, enabled state and
other captions intact. The opposite edge stays fixed when trimming. Captions
stay inside the sequence and cannot be inverted. Overlap remains permitted.

- Work from the gesture's original range; preview block position/width locally.
- On release send one revision-guarded `UpdateCaption` through shared history.
- Click without a drag selects only; Escape, zoom and project refresh cancel.
- Frame quantization uses `TimelineTime`; magnetic snap includes the playhead,
  media boundaries and other caption boundaries. Move snaps either edge while
  retaining duration. The existing snap toggle, or Alt held while dragging,
  disables magnetic snapping for finer placement.
- SRT import times remain unchanged until a gesture is committed. Styles remain
  in the project; no new project schema, native command or Hub catalog is needed.

Automated: `CaptionTimingTests` checks boundaries, snapping, style/history and
stale revisions. `Kachinco.WindowsSmoke --caption-timing` exercises actual WPF
Thumb routed drag events, previews, both trims, cancellation and shared history.

Physical acceptance after updating Kachinco: drag an imported lyric against the
song waveform at multiple zooms; check both handle cursors, snap toggle/Alt,
Escape, Ctrl+Z/Redo, save/reopen and playback at each edited boundary. Automated
routed events do not replace physical mouse/audio synchronization acceptance.
