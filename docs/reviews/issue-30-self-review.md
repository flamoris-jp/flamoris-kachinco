# Issue #30 — Volume automation and monitoring self-review

Date: 2026-10-03. This is implementation-author self-review, not independent review
or physical audible acceptance. Final CI run and merge evidence are recorded on
[PR #61](https://github.com/flamoris-jp/flamoris-kachinco/pull/61).

## Authority and compatibility

- One native EditorSession applies explicit point commands, revisions, dry-run and
  history. UI and closed MCP registry share those commands; no second editor exists.
- Point IDs are clip-scoped, ticks signed/clip-relative, curves strictly sorted and
  bounded to 4096 finite [0,16] multipliers. Raw point fields are closed too.
- Constant gain/mute edits preserve keys. Trim moves keys with source-in; split
  preserves the full left curve and offsets the right curve with checked arithmetic.
  Out-of-range authored keys survive, and overflowing shifts reject atomically.
- Native codec keeps frozen v1/v2 shape and emits byte-compatible v2 without curves.
  Curves require v3 arrays on every clip; signed ticks use decimal strings. Unknown,
  duplicate, missing and incompatible-version fields cannot silently erase keys.
- Additive exports retain struct layouts; capability 4096/required 8191 detects an
  older DLL before use. SafeHandles release timelines on curve setup failure.

## Audio and cache review

- Instant queries and per-sample PCM call one native interpolation helper. The
  48 kHz sample grid divides the canonical timebase exactly; signed endpoint spans
  interpolate using checked/unsigned differences without signed overflow.
- Source blocks, offsets, sample bounds, stereo shape and finite samples validate
  before accumulation. Clamp occurs after mixing, preserving normal overlap behavior.
- Preview and export share SharedAudioRenderer. Partitioned blocks match exactly;
  integration evidence compares 4,800-frame cached preview blocks with 48,000-frame
  export blocks, including save/reload and equal-first-gain/different-slope curves.
- Review caught an equal-zero-start cache collision when constant clip gain changed
  during a fade. The signature now includes **complete AudioProperties**, with a
  regression verifying new PCM at the same request coordinates.

## UI and monitoring review

- The Audio Inspector groups constant clip volume and ordinary point add/time/value/
  delete/fade controls. Numeric and slider edits share the compact PropertySlider.
  Native transient projections preview a captured revision; cancel never mutates
  history, release commits once, and stale release cannot overwrite an intervening edit.
- One-second/half-clip fades preserve keys outside their interval and endpoint IDs.
  Duplicate-time input remains visible with diagnostics. Adding at an existing key
  selects it without changing revision. Point time display preserves tick precision.
- Transport monitoring gain is clearly scoped to listening. It is a validated,
  bounded atomic LocalApplicationData preference, outside Project/history/export.
- Changing monitoring updates the current output handle without resetting its queue
  or consumed-sample clock; resumed/new output inherits the gain. Gain setup failure
  disposes the new output. Windows gain conversion supplies equal channel levels.
- Preference parsing rejects malformed/duplicate/unknown/out-of-range fields and
  reads no more than 4097 bytes. Failed writes preserve the prior preference.

## Executed review-time evidence

- Local native contracts: 4/4. Changed curve/cache/native-timeline tests: 16/16.
- WPF and Windows smoke harness cross-build: zero warnings/errors.
- Preliminary Product CI (head `7a1f9f9`, before final cache review fix): 231/231
  headless tests, ASan/UBSan 4/4 and actual Windows volume-control/save/reopen tests pass.
  Final changed-head CI remains a manual merge gate; see PR #61 for its exact result.
- Local full tests passed 228 at the earlier revision; two existing named-pipe cases
  cannot create sockets in this restricted execution environment. Preliminary CI
  runs those successfully; no production/test relaxation was introduced.
- Windows controls verify point selection, add/time/percentage editing, native
  candidate/cancel, one Undo/Redo, stale rejection, fades, v3 save/reopen, invalid time,
  monitoring scope/cancel and 230px audio layout. CI retains volume-automation.png.
- The review-time Windows screenshot was inspected: monitoring label/value and the
  complete point list/time/multiplier/fade controls fit and remain readable. The
  fixture deliberately has missing media; it establishes layout, not audio perception.

Physical audio-device volume response, human A/V perception, pointer comfort and
DPI acceptance remain [#42](https://github.com/flamoris-jp/flamoris-kachinco/issues/42),
with representative-material checks under #28/#53. CI screenshots/PCM do not close
those physical acceptance items. Direct timeline envelope dragging is a future UX
extension; the initial inspector-driven model is complete within #30's scope.
