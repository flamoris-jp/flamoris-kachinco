# KineticTypo Windows acceptance — #109

Initial implementation. Do not mark physical GPU acceptance complete from WARP
or a successful cross-build. Record date, adapter/driver, resolved fonts, backend,
reset count, exact source commit and observations below.

- [ ] Complete package starts both EXEs on a clean Windows user (no .NET SDK).
- [ ] Hardware status says D3D11-Hardware and identifies the physical adapter.
- [ ] Japanese/English, emoji, combining marks, ligatures, Arabic/RTL and missing
      fonts stay correctly shaped during stagger; fallback faces are reported.
- [ ] Same tick agrees after forward playback, reverse seek, specified-time MCP
      capture and export. Saved source/IR/font/seed survive restart.
- [ ] Open/save/edit/Undo/Redo; unfinished controls prompt before close.
- [ ] Host create/edit/apply is one Undo. Cancel/crash leaves host untouched.
      Host edits/document changes cause a conflict with a retained draft.
- [ ] SRT and legacy PONPON work without Analyzer/audio/semantic files.
- [ ] RGB/mask import validates missing/changed/different CFR/dimension/range pairs.
      Pair move/trim/speed use one mapping; preview/export agree.
- [ ] Black/colored/half-transparent text, outlines/shadows and gray edges have
      no dark double-alpha halos. Compare over white, black and colored backgrounds.
- [ ] Measure combined pair size and two-decoder CPU/GPU use against a comparable
      alpha alternative; record visual quality, not only file size.
- [ ] Device removal/reset reports failure or one same-frame recovery; ordinary
      Kachinco clips/captions retain existing CPU recovery behavior.
- [ ] Record full-song seek latency, preparation time, frame latency, working-set
      and VRAM. Native surface payload does not include every driver allocation.
- [ ] MCP read-only rejects edits; edit uses same visible history and revision.
      Specified-time capture does not seek. Sharing remains a user-visible choice.

CI: native contracts execute real DirectWrite/D2D via explicit WARP on Windows;
managed tests protect schema migration/history/import/mask/source-time behavior;
portable startup is automated. Physical visual/performance checks remain unchecked
until actual measurements are attached.
