# Issues #62–#64 — Still images and reusable AI-authored effects

Date: 2026-10-05. Implementation-author self-review; this is not independent
human review or physical Windows/DPI acceptance. Exact-head CI and merge evidence
are retained on [PR #65](https://github.com/flamoris-jp/flamoris-kachinco/pull/65)
and in PROGRESS.md.

## Authority and persistence

- AGENTS.md and ADR 0015 establish deterministic primitives plus AI composition.
  Ordinary transform curves and restricted Recipes are complementary paths.
- Image is appended to MediaKind without changing legacy tokens. PNG/JPEG/WebP
  use the reviewed probe boundary; animated inputs and invalid dimensions reject.
  The insertion-duration hint never imposes an artificial still-image source EOF.
- One native editor owns point commands, validation, expected revisions, dry-run,
  shared UI/MCP history and checked trim/split shifting. Constant property edits
  preserve curves. Six closed properties have finite, sorted bounded points with
  stable clip-scoped IDs. Bulk curve commands keep effect application to one undo.
- Conditional v4 preserves Image and appearance automation; legacy inputs gain
  empty curves and legacy projects keep exact v2/v3 output. Closed v4 enums,
  missing/unknown/duplicate fields and decimal-string signed ticks are tested.
- Additive exports retain ABI layouts. Capability 8192 detects an older native DLL
  during startup. Managed SafeHandles release failed timeline setup.

## Shared rendering and effect reuse

- Native parameter interpolation evaluates X/Y/scales/rotation/opacity in the
  shared timeline; preview/export use the same compositor. Still frames decode at
  zero, retain alpha, and use a bounded native byte cache. Forward decoding starts
  no moving-source streams for images; thumbnail decoder caches are disposed.
- Review fixed visible-endpoint capture to call the same signed native sampler.
  Convex interpolation/blending clamps protect positive subnormal scales and
  finite numeric extremes without adding a second evaluator.
- Effect Library schema 1 stores inspectable curves or bounded Recipe source,
  stable IDs/version/API metadata, defaults and seeds, independently of per-clip
  Recipe provenance. Applied state is copied; changing/deleting an item cannot
  rewrite a project. Automation adapts to target duration and intensity.
- Recipe save/apply uses the existing restricted compiler; no eval/exec is added.
  Library apply binds a copied Recipe to an existing Clapper. Ordinary explicit
  Generate/Regenerate produces an ordinary clip and provenance. A regression
  generates a bound Recipe without duplicating the existing Recipe registration.

## Filesystem, permissions and UI

- Executable-adjacent default and executable-relative paths are explicit; path
  preference is outside Project. Switching paths never moves/deletes items.
  Updates do not overwrite the library; unsupported schema/API requires explicit
  migration and backups. Missing/invalid/unwritable roots surface diagnostics.
- GUID filenames, bounded JSON/entry counts, strict duplicate/unknown rejection,
  link/reparse checks, cooperative exclusive locking, version checks and atomic
  same-directory publication protect local assets. Shared/NAS guarantees depend
  on filesystem rename/locking support; this does not promise cloud coordination.
- Library MCP tools require per-launch desktop opt-in and existing ReadOnly/Edit
  envelope permissions. MCP cannot supply a root or arbitrary filename. Apply
  plans and commits in the host serialization lane, rechecks the item version,
  supports dry-run, and shares native history.
- UI exposes ordinary transform points and Save as Effect/list/details/apply/
  update/delete/path settings. Async operations are guarded; path/grant changes
  revoke existing MCP connections. Windows smoke covers image Inspector point
  edits/history and saved-effect parameterized application, with screenshot.

## Review/fix loops and evidence

- Initial local run identified two old PNG-rejection fixtures; they now test GIF.
  Two existing named-pipe cases cannot run in this local host and stay in full CI.
- Initial Product CI passed headless/native sanitizers and failed the Windows
  startup assertion on old import guidance. The assertion now requires all new
  image formats; no gate was weakened.
- Local native contracts pass 4/4. Final local managed and Windows cross-build
  outcomes are recorded in PROGRESS.md; final GitHub CI is required before merge.
- Physical playback perception, pointer comfort and DPI acceptance remain the
  existing #42/manual staging work. CI pixel parity and UI screenshots do not
  close those separate physical acceptance items. Full parallax is not required
  by #62; existing Recipe API remains bounded literal text/particles, not arbitrary
  Python math or unrestricted extension execution.
