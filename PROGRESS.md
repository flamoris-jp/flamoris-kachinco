# Delivery progress

## Physical Linux CLI smoke accepted — 2026-10-24 (JST)

- CLI 0.1.1: external MCP project query, guarded dry-run/commit, styled Japanese
  captions, composed-frame visual inspection and explicit saved revision 2 accepted.
- Three-second 1920 × 1080 / 30 fps export completed: 90 frames, actual
  `h264_vaapi`, no encoder fallback, 1,915.2177 ms job total. Composition was
  90 CPU frames / 0 GPU frames; this is not a representative speedup benchmark.
- Durable workspace publication completed. All 39,889 MP4 bytes were retrieved;
  ffprobe verified H.264/AAC, stereo 48 kHz and exactly 3 seconds / 90 video frames.
  A decoded MP4 frame was visually checked, including Japanese glyphs.
- No imported image/video/audio or audible source was used. Shared Undo/Redo,
  restart/reload, reboot/outage, failure/cancellation, production-media A/V,
  Windows reopen and simultaneous live Windows/Linux acceptance remain pending.
- [Bounded evidence and remaining gates](docs/linux-cli-acceptance.md).
  Issue #100 remains open. The older preparation records below are historical.

## Configured resident project storage — 2026-10-23

- CLI 0.1.1 candidate adds optional `workspace.json`: selected project folders
  with `project.fkproj`, portable uploaded `media/` and durable `exports/`.
- Settings apply on graceful restart; existing data/legacy resident projects are
  not migrated or reset. Native commands, persistence, history and encoding remain
  shared. A required CIFS/NFS mount guard fails on missing storage.
- Encode locally, publish with exclusive staging/flush/non-overwriting rename,
  retain completed durable MP4s independently of Job cleanup. Failed publication
  is observable and retains the local encoded file for operator recovery.
- Keeps 0.1.0's exact Updater mount/settings contract and opts into the existing
  retained project mount; no Updater profile mutation or additional service.
- Local targeted storage/host/upload checks: 23 passed; native contracts 5/5.
  Full regression: 379 passed, 1 skipped; the same two Unix pipe transport tests
  fail under this workspace's socket restrictions. Full CI and Docker/Windows
  candidate gates are recorded on the PR.
  NAS mounting, permissions/locking, real GPU export and live Hub acceptance
  remain deployment work, not certified by a source configuration or local test.

## Linux CLI distribution preparation — 2026-10-23

- Gate immutable CLI publication on the shared Product workflow and actual
  Docker/Updater candidate acceptance. Publish the exact accepted image/catalog,
  Hub tool catalog, descriptor generator, guide and SHA256SUMS; no host build.
- Release identity remains cli-v0.1.0. Existing tags are never replaced. Physical
  LIME VAAPI, Hub attachment and speed remain separate deployment acceptance.

## Clip speed and single-clip downloads — Issues #78–#79

- ADR 0019 and the client guide define native constant speed, exact retained source
  span, conditional v5 persistence, key retiming and explicit no-ripple behavior.
- UI/MCP use SetClipSpeed at 1/2/4/8. Shared native evaluation and PCM decimation
  feed preview, capture, export and the attachment-scoped read-only download jobs.
- Four bounded download tools return edited WAV/silent MP4 bytes with metadata,
  file/chunk SHA256 and cancellation/expiry/revision/document/source checks.
- Producer wire fixture pins the changed edit_batch and new tools. Hub companion
  catalog is additive; existing uploads, previews and connection settings survive.
- Local Release native 5/5 and managed regression 299/299 passed, including
  schema rejection, BGM isolation, real-media frame parity and cancellation.
  WPF cross-target compilation passed with zero warnings/errors. Unix pipe-only tests cannot run under this workspace's socket
  restrictions; the unchanged full suite and Windows package gates run in CI.
- Final branch CI and live Mango/tunnel verification remain pending. This does not
  claim prior upload #76 was physically verified or deploy either repository.


## Read-only MCP frame capture — Issue #72

- PR #73 merged after final Product CI #148 passed, including the controlled
  Windows descendant/process-file lifetime repair (#74).
- Live rollout applied the exact eight-tool Hub catalog successfully, with desktop
  connected and no schema mismatch. Calls then exposed a Core 1.3.0 interoperability
  regression: generic Web JSON emitted `resultType:null`, rejected by Python MCP.
- Core #15 / PR #16 fixes outbound Hub serialization using SDK default options.
  This follow-up consumes Core/Wpf 1.3.1. New desktop build/CI, package publication
  and actual external image/playback/viewer acceptance must be verified separately.

Initial implementation validation follows (before the live transport finding):

- ADR 0017 defines `preview_frame`, canonical tick/frame snapping, snapshot/revision
  checks and bounded full-canvas JPEG/PNG image results without playback/history edits.
- PR #73 implements the shared renderer query and Windows encoder, capture contracts,
  portrait viewer pixel parity and external packaged-bridge PNG acceptance.
- Core PR #14 supplies actual image blocks on pipe/Hub routes (1.3.0); Hub PR #50
  adds reverse gateway and real external HTTP MCP/WebSocket image regressions.
- Local capture contracts 6/6 and native 5/5 pass; prior full run: 282 pass and only
  two named-pipe tests blocked by local AF_UNIX restrictions. WPF cross-build passes.
- Product CI #145 (`750bc2cefb6d6ec3bbfec42eb55dde5569542667`) passes Linux
  285/285, native regular/sanitizer 5/5, Windows ABI 48/48, representative full-viewer
  parity and bounded PNG/JPEG encoding, packaged external bridge image delivery,
  shared-history/permission/lifecycle acceptance and portable ZIP/startup checks.
- Core 1.3.0 is published and its Windows 70-test/package suite passes; Hub #50
  merged with full CI plus external HTTP MCP/WebSocket PNG transport coverage.
- Live production desktop/viewer and authenticated OpenAI tunnel/Hub acceptance is
  pending after bundle/catalog update. Follow staging/windows-issue72.md; leave
  Issue #72 open until that evidence is recorded.

## Preview playback lifetime repair — Issue #70

- Baseline: `5cfc7bb`; 2026-10-06 physical log confirms hardware decode, subsequent
  stream-limit fallback and media-tail retries. Latest Full preparation maximum
  2064.737 ms; presentation maximum 3.5856 ms. Private logs/media stay uncommitted.
- Scope: bounded continuous video streams, retire inactive contributors before
  decode (including cache hits), clean EOF/tail reuse and per-explicit-Play skip reset.
- Design recorded in ADR 0016. No project schema, effect, audio clock or export
  evaluation change; decoder selection remains native. CPU recovery is retained.
- Implementation complete in PR #71: continuous video/PTS backpressure, complete
  contributor retirement (including cache hits), EOF/tail repair and explicit Play reset.
- Local managed build: zero warnings/errors. Focused decoder/codec/playback tests
  pass 48/48; full temporary reflection-runner execution passes 277 tests, excluding only
  the two pre-existing named-pipe McpEnvelopeTests restricted by this host. The
  runner copied the existing recipe worker to its temporary app directory; no test
  assertions or Product sandbox were relaxed. Native regular and ASan/UBSan
  contracts pass 5/5 (local leak detection disabled for the traced host; CI keeps it).
- Author self-review fixed a tail replay bug: pixels consumed during a failed
  request cannot be associated with an older successful request tick. Added a
  regression, preserving the exact-first-retry rule without another retained buffer.
- CI #141: Linux 278/278 and native sanitizers pass; Windows build/native WARP
  passes, but real-codec teardown catches a locked source file. The dedicated
  stderr reader pins the native process SafeHandle; cancellation alone did not
  guarantee immediate child/file/GPU release. Join that reader before disposing
  the native process, use context-free PCM drain completion, and add repeated
  reset/immediate-delete coverage. CI #142 confirms that teardown regression passes.
- Product CI #142 (`88222389beffebab436f101356df3cfacf40cf61`): Linux 279/279,
  native regular/sanitizer contracts 5/5, Windows ABI/codec tests 48/48, full solution
  build/native WARP, worker/raster, archive/publish, packaged external MCP and shell
  startup all pass. Run: https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/37463769971
  This final progress update changes documentation only; Product/Test trees remain
  identical to the validated commit. Physical improvement is not claimed until
  the same workload is rerun on Mango.

## GPU preview and effect pipeline — Issue #67

Baseline: reviewed main/tag `v0.1-pre-gpu`, `b0d9bad`.

| Work | Status | Evidence |
| --- | --- | --- |
| Current authority and design audit | Complete | AGENTS, Issue #67, native composition, FFmpeg process and WPF presentation inspected; ADR 0016 |
| Native D3D11 backend and bounded resources | Complete | CI #137 optimized WARP: exhaustive color/alpha, seeded transforms, resource budget/reset and managed ABI pass |
| D3D11VA forward decoding | Complete | NV12 hardware download, two hardware processes, sticky software forward retry |
| Preview integration and backend diagnostics | Complete | Same immutable frame/clock authority, Auto/CPU/D3D11, joined latest-wins switching |
| Independent review and fixes | Complete | Separate read-only source approval at the CI tree; parity, lifetime, codec formats, memory and switch races reviewed |
| Automated verification and merge | Complete | CI #137 Linux/Windows all pass; PR #68 squash-merged as `9bd10ba` |
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
- CI #137 passes optimized shader parity after exact IEEE double integer-bit byte
  conversion, preserving the arithmetic, source sampler and one-byte tolerance.
  Added all 65,536 front-byte/alpha-byte pairs, Normal/Screen and five opacity
  values over varied opaque backdrops, alongside 80 seeded multilayer transforms.
  Separate review verified the conversion mathematically and approved the exact
  tree used by CI. Windows UI/package, packaged external MCP and clean-PATH startup
  gates also pass.

### Implemented delivery outcome

- Reviewed and validated code head: `05def0d48ec9939b31cfd0b00188bab3ccd93d07`.
- Product CI #137: https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/37331815732
  — Linux/headless and Windows/shell jobs both **success**. All 270 managed Linux
  tests, native ASan/UBSan/leak contracts 5/5, optimized WARP pixel/resource
  contracts, Windows managed ABI/backend 41/41, actual WPF selector/raster,
  portable archive/publish, packaged external MCP and clean-PATH startup pass.
- Separate read-only agent review approved the exact validated source tree. This
  is not independent human review or Mango physical acceptance.
- PR #68: https://github.com/flamoris-jp/flamoris-kachinco/pull/68 — squash-merged
  to main as `9bd10ba0afd5334a3d940c260f8789dc028f6e88`.
- Issue #67 stays open for representative 1080p speedup, total VRAM, actual
  presented/drop behavior and physical A/V checks on Mango. The reproducible
  benchmark and remaining scenarios are in `staging/windows-issue67.md`. No
  hardware speedup or zero-copy result is claimed.
- This outcome update changes documentation only; Product/test/workflow files
  remain exactly the validated and merged implementation.

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

## Issue #82: GPU MP4 export (2026-10-16, PR review pending)

- Added independently selected Auto/CPU/D3D11 export composition and Auto/CPU/NVENC
  encoding. ExportFrameRenderer owns the existing bounded native compositor through
  SharedFrameRenderer, isolated from preview. No project schema/evaluation changes.
- NVENC performs a real three-frame initialization encode at the target size/fps,
  explicit FFmpeg GPU index 0 by default, with an eight-second deadline. Failed
  hardware video replays from frame zero once through existing libx264; cancellation,
  renderer validation and audio/mux failures do not trigger replay. Encoder processes
  are joined before temporary files are deleted or reused.
- UI export menus, completion/log metrics and existing MCP clip-download default
  Auto path are connected. Ready clip metadata adds actual acceleration diagnostics;
  inputs and producer catalog are unchanged. No full-sequence MCP export existed
  in current main. Audio contracts and clip bounds remain unchanged.
- Added POSIX injected-executable tests for init/midstream failure, actual CPU
  H.264 recovery, explicit CPU, no replay on renderer faults, cancellation at retry,
  bounded double failure and probe timeout/cancellation. Shared export comparison
  includes transforms, Screen/alpha, captions and speed; Windows also uses native
  WARP. Added WPF menu/real portrait export smoke and four-way export benchmark.
- Local: native Release contracts 5/5; focused managed cases 37 pass, one Windows
  WARP case skipped on Linux. Full solution Windows-target cross-build succeeds
  with zero warnings/errors. Standard local runner/parallel MSBuild are blocked by
  host Unix-socket restrictions; a temporary reflection runner executes the actual
  test methods. CI must run the standard full managed/Windows/package gates.
- Design: ADR 0020. Physical procedure and benchmark: staging/windows-issue82.md.
  Mango NVENC/driver picture quality, speed, total VRAM and physical A/V acceptance
  are not measured. Do not close #82 or claim acceleration from simulated hardware,
  WARP or requested mode. Independent human PR review and merge remain pending.

### Issue #82 automated acceptance result

Product CI #155 at code commit `26763e604d22f22ccc6013f306b211d5ed67ec78`
passed: https://github.com/flamoris-jp/flamoris-kachinco/actions/runs/38891627059 .
Linux standard runner passes 321 tests (one Windows WARP case skipped) and native
ASan/UBSan/leak contracts. Windows passes native/WARP contracts, full solution
build, 67 managed ABI/codec/export cases (nine POSIX injection cases skipped),
actual WPF menu and portrait H.264/AAC export, worker/raster, portable archive,
published editor/bridge external MCP, media import and clean-PATH startup.
This documentation-only follow-up leaves Product/Test/workflow source unchanged
from that validated code commit. PR #83 awaits independent human review and merge
authorization; #82 remains open for measured Mango physical acceptance.

## Exported MP4 first-frame replacement — Issue #94

- Title-image menu supports completed MP4 + image/selected title, fitted preview,
  explicit opening-encode/body-copy counts and save under a new filename.
- File-only Infrastructure finishing path replaces frame zero, preserves frame grid,
  uses a closed-IDR splice and copies later H.264 and original AAC. No project mutation
  or timeline rendering; no overwrite or incomplete-file publication.
- New real-codec tests 10/10, native 5/5 and WPF cross-target build pass. Existing
  regression 355 pass/1 skip; two existing Unix named-pipe tests hit this workspace's
  socket restriction and remain CI gates. Windows input/Media Player acceptance is
  separate. See docs/reviews/issue-94-first-frame.md for benchmark and limits.

## 2026-10-23 — Issue #100 Linux CLI / resident host (implementation, physical acceptance pending)

- Added `Kachinco.Cli`: project create/inspect/validate, probe-backed import/relink,
  shared typed edit batches, atomic Save As, PNG/JPEG frames and CPU H.264/AAC MP4.
- Native EditorSession/evaluator/compositor/codec remain the sole authority.
  Linux Pango/Cairo supplies styled Japanese captions; WPF pixel/font parity is
  explicitly not claimed. Windows media paths require explicit relinking.
- Added one-project resident host using the common outbound Hub provider,
  serialized edits/history, atomic checkpoints, preview/uploads and one bounded
  cancellable export job with opaque artifact chunk retrieval. Separate LIME
  descriptor generation preserves Mango's existing route.
- Added self-contained Linux publishing, user systemd installer, ADR 0024 and
  setup documentation. Installer preserves config/project and waits for actual
  connector configuration before service activation.
- Development-host verification: native 5/5; four new host/caption/job tests;
  real CLI import/edit/save/reopen, styled Japanese captions, PNG/JPEG, exact
  six-frame H.264/AAC, source/output preservation, missing media and SIGTERM cleanup.
  Full managed run: 366 pass / 1 GPU skip / 2 pre-existing Unix-pipe failures due
  to this host's socket restriction; these remain CI gates, not waived tests.
- Physical LIME installation, Hub registration, Windows visual/reopen acceptance
  and AMD GPU acceleration remain open. Server observation reported LIME healthy;
  this development host cannot resolve/reach its private SSH hostname. No claim
  that a prepared unit/bundle is already deployed or connected.

### Updater deployment correction

- LIME rollout uses Updater, as the operator specified. The user-systemd script is
  a developer fallback, not the managed installation path.
- Added application-owned Docker packaging, a digested repository catalog and
  candidate metadata, with a non-root entrypoint and loopback process health.
  Updater owns install/start/version orchestration; projects and media stay in
  preserved data mounts. Existing media roots are configured read-only.
- The catalog/profile was validated locally against actual Updater source
  `d7182b40b05013be7f597dbaf9e952689649e816`, using fixture archive bytes only.
  This proves schema/settings/profile compatibility, not Docker image validity.
- Added candidate CI for an actual image build, actual Updater install/start and
  container MP4 export. This host has no Docker executable; those checks have not
  run here. No release, image, catalog or service was published/deployed.

### VAAPI encoding and simultaneous Windows/Linux Hub routing

- Added Linux `auto/cpu/vaapi` encoding with a real three-frame bounded VAAPI
  initialization probe. Explicit `vaapi` requires hardware and never silently
  uses CPU; Auto permits one video-only libx264 replay. Metrics report the actual
  encoder, selected VAAPI node, fallback and CPU composition counts.
- Native composition, Pango captions, decode and audio remain CPU work. No Linux
  GPU composition, zero-copy pipeline or measured speedup is claimed.
- Added `gpu check`; managed VAAPI startup probes hardware before process health.
  Mesa/libva and application OCI identity labels are part of the Docker image.
- GPU access requires the paired Updater source `93561720aed68e34ee3d4ffb9ccd81a9f035ab79`
  (typed DRM node and observed render-group mapping). Candidate CI pins that source;
  the referenced branch is published for review. This is not a
  published/installed Updater capability yet.
- Actual Hub parser acceptance combines 34 same-named tools under independent
  `kachinco` and `kachinco_lime` namespaces, separate connector IDs/tokens. Live Hub
  inspection still shows Windows `kachinco` only; no LIME route was registered.
- Related encoder/host tests: 20 pass / 1 Windows WARP skip, including five new
  injected-VAAPI cases. Available headless regression: 371 pass / 1 skip; two known
  workspace Unix-pipe failures remain CI gates. Real CPU codec/caption/cancellation
  smoke passes. Physical LIME VAAPI, Docker GPU access, A/V quality and speed remain
  separate deployment acceptance; Server Manager confirms only amdgpu/16-GB presence.
