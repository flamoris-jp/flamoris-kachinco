# Delivery progress: still images and AI-authored effects

Scope: Issues #62, #63, #64. Baseline: reviewed main `42cb62c`.

## Status

| Work | Status | Evidence |
| --- | --- | --- |
| Requirements and current authority audit | Complete | Issue acceptance criteria, AGENTS.md, native codec/editor/evaluator, media and MCP boundaries inspected |
| Design and AI effects principle (#63) | Complete | ADR 0015 and AGENTS.md adopted before implementation |
| Still images and native transform automation (#62) | Complete | PNG/JPEG/WebP, shared UI/MCP history and preview/export |
| Configurable reusable Effect Library (#64) | Complete | executable-adjacent library default, explicit local path preference |
| Regression tests and review/fix loop | In progress | native contracts and managed tests pass; final Windows CI/package smoke pending |
| Merge and final verification | Pending | only after acceptance and required checks pass |

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
