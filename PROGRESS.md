# Delivery progress

## GPU preview and effect pipeline — Issue #67

Baseline: reviewed main/tag `v0.1-pre-gpu`, `b0d9bad`.

| Work | Status | Evidence |
| --- | --- | --- |
| Current authority and design audit | Complete | AGENTS, Issue #67, native composition, FFmpeg process and WPF presentation inspected; ADR 0016 |
| Native D3D11 backend and bounded resources | Implemented; Windows parity fix in progress | Native WARP shader executes; byte parity gate found a mismatch and blocks merge |
| D3D11VA forward decoding | Complete | NV12 hardware download, two hardware processes, sticky software forward retry |
| Preview integration and backend diagnostics | Complete | Same immutable frame/clock authority, Auto/CPU/D3D11, joined latest-wins switching |
| Independent review and fixes | In progress | Separate read-only reviewer checks parity, lifetime, codec formats and total memory |
| Automated verification and merge | In progress | CI #132 Linux: 270/270 and ASan/UBSan 5/5 pass; Windows native parity failure under repair |
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
- Local verification: 268/268 managed tests pass (two existing named-pipe tests
  require the CI host); native contracts 5/5; current WPF/smoke solution cross-build
  has zero warnings/errors. Local ASan/UBSan 5/5 pass with leak detection disabled
  because LeakSanitizer cannot inspect this traced host. Full CI Linux leak checks
  and all 270 managed tests pass in Product run 37327415047.
- Independent review fixed same-frame CPU recovery, canceled GPU completion before
  pool reset, retained hardware failure state across seek, two-stream hardware
  limits and rapid/slow backend selection races. Physical-audio assumptions were
  removed from the new paused WPF selector smoke.
- Windows CI caught a GPU/CPU pixel mismatch after successful real WARP shader
  initialization. Preserve the byte-parity gate and improve sampling diagnostics
  before rerunning; no merge while this regression exists.
- CI #133 isolated the mismatch to partial-alpha Normal blending after a 90-degree
  rotation: red/blue saturated while green matched. Opaque identity and earlier
  transparent/Screen layers passed. Replaced the indexed/unrolled double-color
  expression with explicit scalar RGB calls and a mode branch; keep double source
  coordinates, CPU operation order and the one-byte quantization tolerance intact.
  Windows execution must verify the fix before merge.
- CI #134 kept Linux's full contracts green but the scalar rewrite still failed
  Windows WARP parity: a partial-alpha Screen layer returned red 255 instead of
  18, while green/blue matched. Shader compiler/disassembly diagnostics are the
  next gate; no tolerance or source-sampling change is accepted as a workaround.
- CI #135 confirmed the compiled 80-byte parameter layout and captured the actual
  optimized DXBC. Its color arithmetic/dataflow follows the source, but WARP still
  saturated red incorrectly. CI #136 replayed the identical failing layer with
  optimization disabled: zero mismatched bytes, including the failing pixel.
  This isolates an optimized execution problem; it does not prove which compiler
  or driver stage is responsible. The next repair preserves optimized execution,
  double arithmetic and tolerance while replacing byte conversion equivalently.

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
