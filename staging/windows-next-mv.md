# Next-MV Windows acceptance (#104, #113, #116)

Automated checks: `NextMvChecks` in the ordinary WindowsSmoke suite covers an
embedded text asset with a nonexistent draft path, no FFmpeg visualization work,
foreground resize handles, clicked-clip context routing, and video/audio/subtitle
track switch commands with Undo/Redo and saved/reopened state. `GpuExportChecks`
checks real H.264/AAC export and the retained saved-path completion window.
Headless `GpuExportTests.FinalizationReportsRealPhasesAndCancellationProtectsDestination`
checks audio/mux/save event ordering, publication-before-success, cancellation at
each final phase, protected original destination and temporary cleanup.

Physical checks still required on Mango:

- Reopen an MV containing embedded text and a missing/old companion draft. Verify
  timeline text summary; select a text clip and open its context edit. Verify the
  existing composition, scrub preview and export. An empty editor reached through
  a new composition is a separate selection/workflow case, not FFmpeg decoding.
- Two distinct text assets: right-click the second while the first was selected;
  editing must use the second. Ordinary media must not offer text editing.
- Hover and drag both ends of video/audio/subtitle clips, including narrow clips
  and after zoom. Confirm ↔, no move interception, one history entry, Escape cancel.
- Toggle video/audio/subtitle track headers; confirm dimmed row, no visual/audio/
  caption output for the disabled track in playback and final export. Re-enable,
  save/reopen, Undo/Redo; retain previously disabled individual clips.
- Export an audio MV long enough to observe finalization. Observe audio processing,
  audio encode/mux/faststart, saving, moving indicator and elapsed time. Move the
  window and cancel. A completed result remains visible with the actual saved path
  and a Close button. Failure/cancel never displays success; editor is usable again.
- Compare ordinary-media thumbnails/waveforms, finished A/V synchronization and
  output frame counts. These changes do not alter the shared render evaluator.

No physical acceptance is claimed by a cross-build or startup smoke.
