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

The IR editor shows the actual native program: v1 `ops` or v2 `outputs`. Presets
are copied into each clip and never change applied work after a preset update.
**Pythonスクリプト読込** accepts a literal-only Recipe extension, for example:

```python
stagger(delayTicks='1234800')
flight(x=-500, y=-200, durationTicks='17640000')
shake(amplitude=8, frequency=12)
rotate(degrees=5)
```

Translate, scale, rotate, opacity, shake, flight and stagger retain their existing
v1 semantics. For programmable animation, write in the Python source editor and
press **スクリプトを適用**, or import a `.py` file. **退場サンプル** inserts a working
instant-appearance / end-relative scatter example. Compilation commits one native
edit; source and IR persist, share Undo/Redo, and survive host save/reopen.
Uncompiled source cannot be silently saved as the previous animation. Compiler
errors retain your draft and leave the committed document unchanged.

```python
def animate(t, duration, i, n, seed):
    span = min(0.3, duration)
    p = clamp((t - duration + span) / span, 0, 1)
    return {'y': 40 * p, 'opacity': 1 - p}
```

`t` and `duration` are clip-relative seconds from the canonical 35,280,000-tick
clock. `i` is the zero-based shaping-cluster index; `n` is the count of clusters
from the actual native text layout. Combining marks, ligatures and surrogate pairs
are not split into bytes/code points. Ignoring `i` applies the same displacement
to the whole line. `seed` is the clip's saved unsigned seed. `x`/`y` are offsets
from the clip origin, `rotation` is degrees, `scale` defaults to 1 and `opacity`
defaults to 1 (clamped to 0..1). Scale/rotation act on each shaped glyph at its
layout baseline, as in v1; this does not bend outlines or rotate the full line
around its center.

The supported Python authoring subset includes local assignment/augmented
assignment, arithmetic `+ - * / // % **`, comparisons, `and/or/not`, conditional
expressions and `if/elif/else`, helper functions with simple parameters, and
literal `for ... in range(...)` loops (maximum 64 iterations per loop). All
function paths must return. Numeric global constants, `pi` and `tau` are available.
Functions: `sin/cos/tan/sqrt/abs/floor/ceil`, two-argument `min/max/pow`,
`clamp(value,lo,hi)`, `lerp(a,b,p)`, `smoothstep(lo,hi,value)` and
`noise(seed,index,channel)` (deterministic -1..1; integer channels use floor).
Trigonometric inputs are radians. Define the entry point exactly as
`animate(t, duration, i, n, seed)` returning a dictionary with any of
`x/y/rotation/scale/opacity`.

The compiler translates AST into bounded expression IR; it does not execute user
Python. C++ evaluates the same IR for companion/host preview and export. Imports,
attributes, arbitrary calls, recursion, while loops, comprehensions and filesystem,
network/process access are unsupported. Limits: 64 KiB source, 4096 AST nodes,
16 functions, 8 call levels, 512 expanded statements; 1024 IR nodes, depth 32,
64 KiB program, 2 million evaluated nodes per rendered frame. Lazy conditionals
can guard domains such as division by zero. Nonfinite/invalid-domain/out-of-range
results fail the frame and report a script diagnostic; they do not produce NaNs.

Examples: [end-relative scatter](examples/kinetai-exit-scatter.py),
[harmonic wave](examples/kinetai-wave.py). Existing v1 files remain accepted;
v2 programs require the matching new native runtime (capability 262144).
Older builds reject an unknown program version rather than flattening it.
The font status reports faces actually used; fallback faces may differ across PCs.

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

Each editor uses the shared MCP connection UI. New preferences auto-connect locally
read-only at startup, before a file is opened; saved OFF remains OFF. Composition
replacement keeps the connection and rotates its target token. See
[startup lifetime](mcp-startup.md). The companion exposes
`get_composition`, `compile_script`, `edit_composition`, `undo`, `redo`, `preview_frame`.
`compile_script` is a bounded ReadOnly query accepting `{source: "..."}` and
returning `{program, source}` without editing. Copy both into explicit target
clips and use the existing guarded `edit_composition` transaction to apply them.
Re-export the updated companion registration from **MCP / AI** and refresh its
Hub catalog when installing this build; preserve existing connection settings. Edit grants,
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
bytes/text, 32 v1 primitives/clip (v2 limits above), 4096px canvas, one-day duration. Layout caches are
bounded by entries and glyph shapes; native history uses reversible deltas with
100-entry and 32 MiB bounds. Full-song resource and hardware quality measurements
remain in [Windows acceptance](../staging/windows-kinetai.md).


## Timeline editing and saved compositions

Kachinco's embedded `KineticText` clips display a summary of the first three text
entries in the timeline. This summary uses the saved composition, requires no
`.ktproj` draft file and does not attempt video decoding. It is not a rendered
frame: an intentionally transparent animation interval does not imply load failure.
Use the clip's context menu **キネタイで編集… / Edit in KineticTypo…** to edit that
specific asset. The toolbar can still create new text when no text asset is selected.
Selecting the intended existing clip distinguishes editing from creation; the
companion's normal timeline can also be transparent outside text ranges.

The checkbox in each track header enables/disables the whole video, audio or
subtitle track for preview and export. It preserves individual clip flags and
shares native Undo/Redo and project persistence. Disabled clips remain editable.
