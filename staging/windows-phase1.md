# Phase 1 Windows hands-on acceptance

Authority: Issue #3. Use a small real `.mov` with a video stream and a real `.wav`.
Do not use private acceptance media in Product/Test or commit it to the repository.

## Automated gates

- `dotnet test test/Kachinco.Tests/Kachinco.Tests.csproj -c Release`
- `dotnet build product/Kachinco.App/Kachinco.App.csproj -c Release`
- Windows startup smoke from `.github/workflows/foundation.yml`
- Linux real-file test generates a small MOV/WAV, probes both with the production
  ffprobe adapter, and keeps those fixtures transient.

## Physical Windows flow

Record date, commit and tester when running this list. Automated startup is not a
claim of visual or interaction acceptance.

- [ ] Create a landscape project; confirm S1/V1/A1 rows and fixed headers.
- [ ] Import a real MOV without a duration dialog; confirm it appears in the media bin.
- [ ] Import a real WAV without a duration dialog; confirm type/duration/audio metadata.
- [ ] Double-click or select **配置**; confirm placement on a compatible track at the edit cursor.
- [ ] Drag horizontally, then between compatible tracks; confirm incompatible rows reject cleanly.
- [ ] Drag both trim handles; confirm source/timeline bounds reject without partial edits.
- [ ] Click the ruler, split at the orange edit cursor, delete, Undo and Redo.
- [ ] Zoom and scroll; confirm clip time does not change until an edit command commits.
- [ ] Save and reopen; confirm IDs/ranges by behavior and equivalent timeline layout.
- [ ] Move one source file away, reopen, confirm warning state, relink it, and confirm timeline placement is unchanged.
- [ ] Confirm standard Windows menu, independent toolbar and readable media/inspector panels.
- [ ] Confirm transport buttons are disabled and no fake playback occurs.

## Implementation-run status

The implementation environment was Linux and had real FFmpeg/ffprobe available;
the generated MOV/WAV command/probe path was exercised there. Physical Windows
visual acceptance remains intentionally unchecked until a Windows hands-on run.
