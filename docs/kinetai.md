# KineticTypo / キネタイ

Build the normal complete Windows portable package:

```powershell
./product/packaging/publish-windows.ps1
./artifacts/windows/FLAMORIS-Kachinco-win-x64/Kachinco.App.exe
# Standalone:
./artifacts/windows/FLAMORIS-Kachinco-win-x64/kinetai/KineticTypo.App.exe
```

In Kachinco choose **KineticTypo / キネタイ → 文字演出を作る・編集**. A selected
KineticText asset opens its embedded composition; otherwise a new composition uses
the selected sequence canvas/FPS. Edit the text/font/color/position/timing/seed,
select ブルブル/飛来/散る or edit the versioned motion IR, and press **文字と動きを反映**.
Scrub/play to inspect. **カチンコへ適用** saves the draft and explicitly returns one
undoable transaction. A new composition creates a video track at the captured
playhead; editing an existing asset updates all its instances. Shortening below
an existing source range is rejected. Cancel/crash never modifies Kachinco.

Kachinco remains usable while the companion is open. If the project/revision
changes, the return is rejected and the draft location is displayed. Import that
`.ktproj` as a fresh asset when useful. Text state is embedded in `.fkproj`, so the
companion and temporary draft are not needed for playback/reopen.

Standalone Open/Save uses `.ktproj`. **歌詞読込** replaces composition clips from
SRT or legacy `ponpon_export` JSON (`lines[].pressTime/releaseTime/text/id`), also
supporting timed `lines`/`items` with `start/end`. Seconds convert once to canonical
ticks; each imported clip gets a fresh identity and source provenance. Neither
Analyzer, semantics nor a soundtrack is required.

## Motion scripts

The JSON editor shows the actual native motion IR (`version:1`, `ops`). Presets
are copied into each clip and never change applied work after a preset update.
**Pythonスクリプト読込** accepts a literal-only Recipe extension, for example:

```python
stagger(delayTicks='1234800')
flight(x=-500, y=-200, durationTicks='17640000')
shake(amplitude=8, frequency=12)
rotate(degrees=5)
```

Translate, scale, rotate, opacity, shake, flight and stagger are supported.
Ticks use 35,280,000/second. The trusted isolated worker parses AST; it never runs
user Python. Imports, loops, variables, attribute calls, arbitrary expressions,
filesystem and process access are rejected. Native validation rechecks all IR.
Source/IR/font/seed persist through reopen. DirectWrite shaping clusters, including
combining marks, ligatures and surrogate pairs, are animation units. The font
status reports faces actually used; fallback faces may differ across PCs.

## RGB + mask interchange

**RGB＋マスク書出** writes a new folder containing `text_rgb.mp4`, `text_mask.mp4`
and `text.ktpair.json`. The mask comes from native alpha, not RGB brightness.
RGB is associated color over black; decoded full-range gray supplies opacity.
The native conversion unassociates once before the existing CPU/D3D11 compositor,
so normal blending does not multiply alpha twice. Black text and shadows survive.

Initial encoding is CPU libx264 (RGB CRF 18, 4:4:4; mask lossless gray). GPU text
rendering and encoding are independent: hardware encoding selection for this
companion-specific pair exporter is a future optimization. No fixed size advantage
is promised. Both streams use the same requested frame ticks and no soundtrack.
The manifest binds pair identity, checksums, file sizes, dimensions, rational FPS,
exact frame count, zero origin, duration, color/range, seed/program and renderer.
Both streams are checked for CFR PTS before publication and on import. Incomplete,
changed, mismatched, unknown-version or out-of-folder pairs fail import.

Use Kachinco's **文字素材・RGBマスクを読込** for either `.ktproj` or `.ktpair.json`.
A mask pair is one asset/clip. Shared native source mapping drives both decoder
requests under trim/speed; appearance/automation/blend follow normal video clips.
This first importer uses adjacent immutable RGB/mask files; relink a pair by
importing its validated manifest again. Generic single-file relink is rejected.

## MCP and rendering status

Each editor uses the shared user-enabled MCP connection UI. The companion exposes
`get_composition`, `edit_composition`, `undo`, `redo`, `preview_frame`. Edit grants,
revision/document guards and busy editing apply. Kachinco's `edit_batch` includes
`SetTextComposition`; `get_project` exposes embedded state and `preview_frame`
uses the same native drawing. Refresh any pinned Hub producer descriptor when
adopting the expanded `edit_batch`; no Hub deployment is performed by this PR.

Native GPU text requires Windows DirectWrite/Direct2D/D3D11. Hardware creation does
not silently switch to software. WARP is explicit native test evidence. Device
loss attempts one same-tick hardware reinitialization and reports reset count;
ordinary Kachinco CPU composition recovery is unaffected. Internal surfaces are
premultiplied BGRA, existing composition receives straight RGBA. Preview includes
CPU readback; this is not a zero-copy claim. Linux still supports ordinary projects
and masked video; rendering embedded KineticText reports unsupported backend.

Limits: 16 MiB/document, 10000 clips, 128 simultaneously active clips, 4096 UTF-8
bytes/text, 32 primitives/clip, 4096px canvas, one-day duration. Layout caches are
bounded by entries and glyph shapes; native history uses reversible deltas with
100-entry and 32 MiB bounds. Full-song resource and hardware quality measurements
remain in [Windows acceptance](../staging/windows-kinetai.md).
