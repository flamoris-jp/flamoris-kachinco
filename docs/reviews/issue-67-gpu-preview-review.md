# Issue #67 — bounded GPU preview review

Date: 2026-10-05. Implementation-author review plus a separate read-only agent
review; this is not independent human review or physical GPU performance
acceptance. [PR #68](https://github.com/flamoris-jp/flamoris-kachinco/pull/68)
is the implementation and merge surface. PROGRESS.md records the final outcome.

## Authority and supported operations

- Native evaluation, source-time mapping, layer order and `kn_composite_rows`
  remain the reference. The optional backend consumes the same evaluated inputs;
  it introduces no second timeline, project state, effect catalog or AI shader
  execution. Export continues through native CPU composition.
- Native D3D11 implements nearest scale, translation, rotation, opacity and
  encoded SDR straight-alpha Normal/Screen composition. Repository-authored HLSL
  uses double input arithmetic and RGBA8 integer textures. Unsupported hardware
  capabilities select CPU with a diagnostic. Production never selects WARP.
- Auto keeps the single identity frame CPU copy path. Forced CPU and D3D11 permit
  same-project comparison. Full/Half/Quarter retain the shared dimensions and
  appearance rules.

## Transfers, decoding and bounds

- FFmpeg remains an executable boundary. Supported 8-bit NV12 D3D11VA frames are
  downloaded before the existing software scale/RGBA output. Confirmation requires
  a complete frame through the strict hardware-download filter, rather than a
  capability string or successful process startup. Unsupported surfaces, including
  10-bit formats, retry the requested time through the software forward stream.
- Hardware failure stays sticky across ordinary seeks until explicit backend
  reselection, preventing repeated hardware process creation. Partial hardware
  frames are discarded. Cancellation is not converted into a decoder failure.
  At most two hardware processes exist within the existing eight-stream bound.
- Each evaluated layer uploads into one reused texture. Two ping-pong outputs
  compose in layer order; one staging texture reads the final immutable frame
  back for ordinary BGRA preparation/WPF presentation. This is not zero-copy.
- The compositor charges all four texture payloads plus its 80-byte constant
  buffer against 256 MiB. Retained same-frame CPU recovery inputs have a separate
  256 MiB bound; larger requests stream through CPU. Decoder surfaces and driver
  allocations require physical total-memory measurement.

## Failure, cancellation and switching

- Begin/composite/read and their complete async decode are serialized by a frame
  lease. A GPU exception disposes the context and replays the same decoded inputs
  through CPU without a second evaluation or source decode.
- Cancellation never publishes a partial frame. Completion is drained before
  canceled work can release/reallocate texture payloads. GPU completion has an
  explicit deadline and a failed context cannot be reused; readback uses a
  nonblocking map after the completion query.
- Seek, quality/backend changes, project replacement and shutdown join or
  serialize work before reset. Presented/cached frames own CPU arrays only.
- Backend changes enter the existing latest-wins mailbox. The selected version is
  applied before rendering; newer seek, pause and play intent supersedes older
  requests even during a slow backend change. Audio's consumed-sample clock and
  existing drop policy remain authoritative.

## Review fixes and automated evidence

- Separate review corrected retained-input CPU recovery, completion before pool
  reset, hardware failure preservation, hardware stream limits and rapid/slow
  selector races. Paused WPF selector smoke avoids depending on a physical audio
  device; headless race tests cover pending play/pause intent explicitly.
- Local contracts: 268 managed tests pass with only the two existing host-blocked
  named-pipe cases excluded; native contracts 5/5; solution cross-build has zero
  warnings/errors. GitHub Linux runs all 270 managed cases and ASan/UBSan with leak
  detection, successfully.
- Windows native WARP executes the actual production shader against seeded CPU
  reference pixels, including opaque/transparent inputs, alpha, source edges,
  exact integer sampling boundaries, rotation, nonuniform scale and layer order.
  The one-byte SDR quantization tolerance never permits different source sampling.
  Optimized WARP initially failed byte conversion despite coherent generated
  shader arithmetic and parameter layout. The identical failing layer passed
  without optimization. Exact IEEE double integer-bit quantization repaired the
  optimized shader without changing its math, nearest sampler or tolerance.
  All 65,536 front-byte/alpha-byte pairs at Normal/Screen and five opacity values
  over varied opaque backdrops now pass alongside the transformed cases.
- Product CI #137 passed at `05def0d48ec9939b31cfd0b00188bab3ccd93d07`: Linux
  270/270, native ASan/UBSan/leak 5/5, optimized Windows native WARP contracts,
  Windows managed ABI/backend 41/41, actual WPF selector/raster, portable package
  integrity/publish, packaged external MCP and clean-PATH startup.
- Separate read-only agent review approved the exact CI tree. PR #68 was
  squash-merged to main as `9bd10ba0afd5334a3d940c260f8789dc028f6e88`.

## Physical acceptance

No Mango speedup, total VRAM, presented FPS, GPU utilization or A/V acceptance has
been measured. `staging/windows-issue67.md` provides reproducible preparation
comparisons and physical scenarios, with explicit accounting limits. Issue #67
remains open until representative transformed 1080p workloads show improvement
over the checkpoint and the physical playback/resource checks pass.
