# Inspector property editing (#29)

The Inspector follows image-editor property panels: compact aligned numeric rows,
separate Basic / Transform / Audio sections, percentage opacity and scale, and
sliders beside precise numeric input. Photoshop's layer opacity/blend controls and
Krita's dockers are interaction references, not new application dependencies.

- Timing and X/Y position commit on Enter or focus loss; Escape restores the value.
- Opacity (0–100%), scale, rotation and **clip** gain use slider + numeric input.
  Scale/rotation slider limits are convenient working ranges, not domain limits;
  valid larger numbers remain editable without being clamped on refresh.
- Only relevant sections are shown: video has compositing/transform, audio has gain
  and mute. Existing video clips do not acquire implicit audio playback.
- A gesture captures one immutable snapshot, revision and stable clip/sequence IDs.
  Preview applies the existing typed command through the native codec's transient
  project operation. This derived candidate is never a second EditorSession.
- Preview uses the existing evaluator/renderer and latest-wins requests. Release
  executes one ordinary native command with the captured expected revision.
  Escape, capture loss, selection changes and external edits discard the candidate.
  A no-op gesture creates no history entry. Stale release cannot overwrite MCP edits.
- Numeric errors remain visible and do not reset unrelated input. Disabled/mute and
  blend choices commit immediately through the same command path.

References: https://helpx.adobe.com/photoshop/desktop/create-manage-layers/apply-layer-effects/set-layer-opacity-and-blending-modes.html
and https://docs.krita.org/en/user_manual/getting_started/navigation.html

Windows automated checks cover narrow-panel layout and actual control events,
candidate isolation, cancel, one-step Undo/Redo and stale revision. Physical pointer,
DPI and visual acceptance remains part of #42.
