# Glyph import and Linux Kinetai

Implementation candidate. Physical desktop/GPU/production-server acceptance is
separate from source/CI verification. Linux support is headless, not a Linux GUI.

## Windows: Glyph → キネタイ → カチンコ

1. Generate a schema-2 SVG bundle using [FLAMORIS Glyph](https://github.com/flamoris-jp/flamoris-glyph).
2. Open KineticTypo and choose **Glyph読込**, then the bundle's `manifest.json`.
   This replaces the composition's clips as one native undoable edit.
3. Timed SRT cues retain their times. Untimed text/title cues use the current
   composition duration (stated in the picker). Review placement before applying.
4. Adjust size, position, tint and the existing presets/Python motion script.
   Each visible occurrence is a motion unit, even when its Unicode character repeats.
5. Save `.ktproj`, export RGB/mask, or choose **カチンコへ適用** as before.

Imported vector paths/fills/occurrence IDs/source license are embedded in the
document and then the host project. Playback needs neither the original bundle
nor a font. The font field does not replace Glyph geometry. **選択文字のGlyphを解除**
returns the selected clip to normal text; after detaching the installed font
applies again. Text changes retaining old vectors fail visibly rather than
showing a different word with stale shapes.

The SVG fill is retained; the existing clip color acts as a tint, white by
default. Size is the normalized design-cell size. Motion pivots on each vector
glyph's center. Spaces, tabs, line breaks and per-glyph advance remain layout
inputs. There is no new kerning or automatic paragraph layout for vector assets.

## Linux: headless Kinetai commands

Build the ordinary Linux CLI. Native compilation additionally needs
`pkg-config libcairo2-dev libpango1.0-dev`. Runtime needs Cairo/Pango and fonts for
ordinary text; the existing container carries the runtime libraries and Japanese
Noto fonts. Outlined Glyph playback does not require fonts. The renderer reports
`Pango-Cairo-CPU`; this is native CPU text drawing, not GPU acceleration.

```bash
kachinco kinetai new --text "赤い靴" --output text.ktproj --width 1280 --height 720 --duration 5
kachinco kinetai import-glyph bundle/manifest.json --output glyph.ktproj
# Add --untimed-duration 5 only when untimed cues/title are present.
kachinco kinetai validate glyph.ktproj
kachinco kinetai script glyph.ktproj --script motion.py --output animated.ktproj
kachinco kinetai frame animated.ktproj --time 1.5 --output glyph.png
kachinco kinetai export animated.ktproj --output new-pair-folder
kachinco kinetai add movie.fkproj --composition animated.ktproj --time 0 --output edited.fkproj
kachinco render frame edited.fkproj --time 1.5 --output timeline.png
kachinco export edited.fkproj --output movie.mp4 --encoder cpu
```

`kinetai add` registers the self-contained composition and adds one ordinary
video track/clip in a single native transaction. `--sequence UUID` is required
when the host project has multiple sequences. Duration extends only when needed.
All CLI output paths must be new. The script command applies compiled source/IR
to every clip in the output composition; no user Python is executed directly.
Use the existing guarded native/MCP composition editing for individual clips.

Existing CLI/resident MCP preview/export now draw embedded KineticText assets
through the same native renderer. No MCP catalog/transport is added here, and no
server is automatically deployed. Windows and Linux share saved motion, canonical
time, geometry and alpha rules; Pango and DirectWrite font selection/rasterization
are platform adapters, so ordinary font text is not promised pixel-identical.

## Compatibility and limits

Old text version 1 remains accepted. Imported Glyph uses text version 2 and the
matching native capability 524288. Host `.fkproj` stays schema 8, with independently
versioned embedded text. Older runtimes reject v2 instead of losing its geometry.

The importer accepts Glyph bundle schema 2, Style DNA schema 1, normalized viewBox
and M/L/H/V/Q/C/Z numeric closed paths. It verifies asset SHA-256, occurrence/text
mapping, palette/stroke links and relative paths and rejects active XML, external
resources, traversal/reparse paths, missing units and unsupported versions.
Native code validates the embedded command data again, including text mapping,
closed contours, source provenance, numeric and geometry budgets.

Bounds: existing 16 MiB text document and 128 active clips; 5,000 imported
occurrences, 64 paths per glyph, 100,000 commands per path, one million commands
per document. Import: 20 MB manifest, 2 MB SVG, 64 MB aggregate SVG. Layout cache
has at most 128 entries and a 32 MiB geometry/key budget. Large songs may require
splitting bundles; an explicit error is preferable to truncation. Complex brushes,
arbitrary SVG, per-stroke motion and Linux GUI are outside this slice.

Test fixtures are actual exports of Glyph 0.2.0 from original Apache-2.0
experimental centerlines. They are test-only and never production dependencies.
See [ADR 0030](decisions/0030-glyph-and-linux-kinetai.md).
