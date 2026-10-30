# ADR 0030 — Glyph assets and Linux Kinetai

Status: implementation candidate; accepted owner requirement 2026-10-30.

## Decision and acceptance

- Add an explicit local Glyph schema-2 bundle import in the Windows companion and
  Linux CLI. Verify hashes, bounded path-only SVG and occurrence/cue links before
  committing. No Python/font/model is required for playback of imported vectors.
- Embed numeric closed vector paths, original fill, occurrence/stroke IDs and
  source provenance in the native text document. No external asset path becomes
  playback authority. Move/edit/save/undo through the existing native session.
- Text document version 1 stays byte/schema compatible. A document containing
  Glyph clips uses version 2; each such clip has one additional `glyphs` object.
  Other clip fields and the motion v1/v2 contracts remain unchanged. Older
  binaries reject document v2. Host project schema 8 already embeds independently
  versioned text documents; its outer format does not change.
- The new native capability 524288 identifies Glyph document support. Never load
  vectors into an older matching-ABI runtime without checking this capability.
- `glyphs` has version, source (song, style, sha256, license), and units. Units
  carry occurrenceId, variantId, character, line, column, advance and paths; each path has
  id, RGBA fill, commands (absolute M/L/C/Z). Import converts quadratic/H/V/relative
  SVG into this normalized typed geometry. Native revalidates all persisted data.
- Preserve cue timing exactly using canonical 35,280,000 ticks/second. Untimed
  text/title cues require an explicit duration. Do not invent letter timings.
- Vector units are the visible lyric occurrences, in source order. `i/n` in motion
  scripts refer to those units. Spaces/newlines retain placement; vector paths
  scale about each glyph center. `fontSize` is their design-cell size. Saved fill
  is multiplied by the clip tint (white by default). Font family does not replace
  vector geometry; editing vector-bound text requires regeneration or detaching.
- Native Linux text drawing uses Pango shaping and Cairo CPU rasterization.
  Windows retains DirectWrite/Direct2D/D3D11. Both call the existing native motion
  evaluator and color/alpha conversion. Backend metadata must state CPU vs GPU;
  no hardware/zero-copy equivalence or identical font raster pixels is claimed.
- Keep layout caches bounded. Geometry, closed contours, input sizes and active
  clips are bounded. SVG parsing never opens an external resource; local file
  import rejects reparse/symlink assets and path escapes.
- Linux is initially headless: composition import/validation/frame, scripts and
  RGB/mask export, plus ordinary Kachinco timeline import. No Linux GUI is claimed.
  Existing CLI/MCP preview/export then render embedded text without a companion.
  Do not add a parallel project/history/evaluator or a new MCP transport.

## Verification

Native validation/history + renderer contracts, managed bundle rejection and
round-trip tests, Linux CLI real render/export smoke, Windows WARP Glyph checks,
existing Windows package/build gates. Physical GPU/UI and production font/codec
acceptance remain explicit staging work after the source/CI checks.
