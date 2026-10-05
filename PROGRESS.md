# Delivery progress

## GPU preview and effect pipeline — Issue #67

Baseline: reviewed main/tag `v0.1-pre-gpu`, `b0d9bad`.

| Work | Status | Evidence |
| --- | --- | --- |
| Current authority and design audit | Complete | AGENTS, Issue #67, native composition, FFmpeg process and WPF presentation inspected; ADR 0016 |
| Native D3D11 backend and bounded resources | In progress | Hardware device, transform/opacity/normal/screen compute path, explicit CPU fallback |
| D3D11VA forward decoding | In progress | Strict hardware download with software forward retry; existing time/seek authority |
| Preview integration and backend diagnostics | In progress | Same immutable CPU frame contract; explicit transfer accounting |
| Independent review and fixes | In progress | Separate read-only reviewer checks parity, lifetime, codec formats and total memory |
| Automated verification and merge | Pending | Native/headless and Windows runtime/package CI are merge gates |
| Mango 6 GB performance acceptance | Pending physical verification | No measured FPS/utilization/VRAM claims; Issue #67 remains open for acceptance |

- 2026-10-05: verified GitHub main through the connected integration. Preserved all
  tracked source bytes against its Git tree; no unauthenticated clone required.
- Design: existing executable FFmpeg and WPF array contracts require hardware
  decode download plus one compositor output readback. Record transfers explicitly;
  do not describe this slice as GPU-resident or zero-copy.
- Native compositor pool has a 256 MiB payload ceiling. Separate bounded FFmpeg
  decoder surfaces and driver overhead require physical total-memory measurement.
- Work proceeds in focused design/foundation/decode/compositor/integration/review
  commits. GPU initialization and shader failures must preserve CPU recovery.

## Completed: still images and AI-authored effects

Scope: Issues #62, #63, #64. Baseline: reviewed main `42cb62c`.

## Status

| Work | Status | Evidence |
| --- | --- | --- |
| Requirements and current authority audit | Complete | Issue acceptance criteria, AGENTS.md, native codec/editor/evaluator, media and MCP boundaries inspected |
| Design and AI effects principle (#63) | Complete | ADR 0015 and AGENTS.md adopted before implementation |
| Still images and native transform automation (#62) | Complete | PNG/JPEG/WebP, shared UI/MCP history and preview/export |
| Configurable reusable Effect Library (#64) | Complete | executable-adjacent library default, explicit local path preference |
| Regression tests and review/fix loop | Complete | Product CI #130: Linux 249/249; ASan/UBSan 4/4; Windows ABI/UI/worker/package/external MCP all pass |
| Merge and final verification | Complete | PR #65 squash-merged as `14fd5a9`; Issues #62/#63/#64 closed as completed |

## Decisions and verification log

- 2026-10-05: confirmed no open PR competes with this delivery. Preserve one native EditorSession and canonical 35,280,000 ticks/second.
- Persist meaningful implementation units as separate commits. Record limitations honestly; physical Windows visual acceptance is separate from automated CI.
- AI-authored effects use ordinary automation and the existing restricted Recipe compiler. No arbitrary Python execution or new renderer.

- Native contracts: 4/4 passed; Core, Infrastructure and cross-target WPF build: zero warnings/errors. First managed run: 227/231 passed; two assertions need new image policy and two named-pipe tests require unrestricted CI host.

- Review/fix loop 1: updated formerly unsupported PNG fixtures to GIF; kept both existing named-pipe tests in CI (local host cannot create their sockets).
- Review/fix loop 2: capture now samples exact signed visible endpoints through native interpolation; clamped convex sampling protects positive subnormal scales and finite extremes. v4-only enums reject flag/comma variants. Thumbnail decoders now release their owned native caches.
- Review/fix loop 3: native visual capability handshake rejects older DLLs at startup. Product CI run 129 passed Linux but caught an old Windows import-guidance expectation; test now requires the image-aware text.
- Added regression coverage for real PNG/JPEG/WebP decode, image preview/export pixel parity, v4 persistence/strict validation, trim/split/history, safe versioned library I/O, parameterized apply, restricted Recipe reuse and MCP permissions/revisions. Added Windows UI Save as Effect/apply/history acceptance and screenshot evidence to the existing smoke job.
- PR #65 is the review/merge surface: https://github.com/flamoris-jp/flamoris-kachinco/pull/65. Self-review is implementation-author review, not independent human review. Exact final-head CI remains the merge gate.

- Final local review checks: 247/247 managed tests passed (only the two existing socket-restricted McpEnvelopeTests excluded locally); native contracts 4/4; WPF and Windows smoke harness cross-build zero warnings/errors. Full headless tests and actual Windows runtime/package acceptance remain enabled in CI.

## Completed delivery

- Final reviewed code head: `e2f7b5a8180bf921828d2f5fec3780d903af20cd`.
- Product CI #130: https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/37314902947 — both Linux/headless and Windows/shell jobs **success**. Linux includes all 249 managed tests and native ASan/UBSan 4/4; Windows includes 23 ABI tests, actual image/automation/Effect Library UI/history, restricted worker/raster, package integrity/publish, packaged external MCP/media import and clean-PATH startup.
- The Windows `effect-library.png` screenshot was inspected: path/explicit grant, list/details, save/update/delete/apply and parameter controls are readable within the scrollable window. It is layout evidence, not physical DPI/playback acceptance.
- PR #65 merged to main with the repository-supported squash method: `14fd5a9aa9589d25a92935df6cca15cd7bc16ef5`. All three issues were verified closed as completed after merge.
- This final documentation-only update records outcomes after the code merge. No Product/test files change. Physical Windows/DPI acceptance remains the existing #42/manual staging work; full parallax and unrestricted Python are outside these issues.
