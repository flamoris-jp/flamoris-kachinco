# Issue #36: final native architecture audit

Scope: #31 engine migration, after merged #32–#35. ADRs 0007–0011 define the
accepted boundaries. Physical Windows acceptance is deferred by owner direction
on 2026-09-30 JST until the migration completes; no simulated/CI clock is signoff.

## Production authority map

| Contract | Single production authority | Retained managed responsibility |
| --- | --- | --- |
| Project, all 24 command types, validation, atomic batches | `editor_domain.cpp`, `editor_session.cpp` | Typed wire/query records and one locked EditorSession facade |
| Revision, bounded Undo/Redo, document identity | Native editor session | Precommit cancellation and grant invalidation callbacks |
| v1 input migration / v2 output, required/unknown/duplicate fields, exact ticks | `project_codec.cpp` | Bounded UTF-8 read and atomic temporary-file replacement |
| Rational frame/sample conversion, inverse frame quantization, reduced FPS | `time_math.hpp`, `runtime.cpp` | Decimal display/input, rate normalization and protocol timebase conversion |
| Timeline visibility, layer order and audio range evaluation | `timeline.cpp` | Immutable domain projections, transient selection/placement UI |
| RGBA transform/blend/opacity and PCM mix | `compositor.cpp` | WPF text raster, async stream/encode orchestration |
| Consumed-sample clock, generations, request/decoder policy | `playback.cpp` | Async scheduling, cancellation/join and Windows physical audio I/O |
| Codec processes, owned decoded buffers, bounded frame/audio cache | Native process/decoded media/cache | ffprobe/FFmpeg protocol, asset paths and immutable projections |
| Preview/export semantics | Same native evaluation/composition/mix | Export job/filesystem/FFmpeg encoding adapter, no clip-placement interpreter |
| UI/MCP edits | Same native EditorSession handle | Shared Core 1.2.0 / Wpf permissions, stdio bridge and UI dispatch |
| Recipe execution | Existing bounded AST worker contract | Python isolation/limits, typed IR adapter and native render pipeline |

The UI's `PresentationObjectCache<VisualEntry>` holds WPF-owned bitmap objects,
not decoded frame/audio authority. Production frame/audio uses NativePreviewCache.
BigInteger remains only for input normalization, generic display/FFmpeg protocol
rounding and transient UI geometry; canonical frame/sample paths call native.
Playback's floor frame selection and presentation's nearest-frame quantization
are intentionally different established policies, implemented in the same native
time arithmetic module.

## Cleanup and lifetime review

Unused production ProjectFormatV1/V2 implementations are removed. Frozen codecs,
commands/session/validator, evaluator/compositor and BigInteger time remain only
under test as independent oracles. No production managed mutation, schema codec,
evaluation or composition fallback is retained.

C ABI uses fixed-width values, explicit buffer/session handles and exception
containment. Managed handles use SafeHandle; timeline/decoded leases are immutable.
Native prepare owns an invisible candidate and shallow immutable history; commit
preallocates its response before publishing state. Aborted/cancelled/rejected
changes preserve project, revision and both history stacks. Grant revocation
precedes document publication; delayed media work is generation-guarded.

ABI version stays 1; complete runtime capabilities are 2047. Added time queries
require the new capability, so stale DLLs fail application startup handshake.
Portable packaging checks matching native adapter/runtime, Recipe worker, shared
MCP runtime and engine-free bridge; includes build identity and MIT JSON license.
FFmpeg/ffprobe and Python remain external, explicit prerequisites. Native package
workflow requests 14-day retention for post-migration physical testing. The
repository currently caps Actions artifacts at 3 days; preserve the exact reviewed
ZIP before expiry or run the manual package workflow on reviewed main.

## Verification and remaining acceptance

The final PR must pass four native Release and ASan/UBSan/leak suites, the full
headless compatibility/real-codec/export suite, and both exact-head Product jobs.
Windows gates cover solution build, ABI/editor parity, worker/raster, self-contained
package startup and published external MCP transactions/history/revocation.
The final PR review records exact commit/run IDs and results; a green earlier phase
is not substituted for the final gate.

#35 run 36654269015 and #36 run 36655472936 passed packaged lifecycle cases,
but run 36656495083 reproduced a settings timeout. Diagnostics showed only the
main window, with no settings dialog and no attachment exception. The UI test
waited for modal owner re-enablement, which precedes async attachment completion;
shared MCP disables Settings/Connect while busy. The harness now waits for those
controls to become enabled before invocation, propagates invocation failures and
repeats every document-loss/recreation case twice with all access/history checks.
Attachment/UI diagnostics remain enabled; the final gate must pass this path.

No embedded libav, GPU/hardware acceleration or C++ WPF rewrite is claimed. No
universal real-time performance claim follows from #34 fixture measurements.
Native automation evaluation support preserves constant gain and a deterministic
seam; editable/persisted curves and master monitoring volume remain #30.

| Existing issue | Final audit disposition |
| --- | --- |
| #28 paused frame after clip boundary | Native freeze/join/final-frame and stale generation contracts implemented; keep open until physical reproduction/checklist passes |
| #29 Inspector controls | Independent UI feature remains open; future edits use the native shared commands/history |
| #30 master monitoring / automation | Independent model/UI/schema feature remains open; specify storage/interpolation and native preview/export contract before implementation |

Use `staging/windows-native-phase2.md` with the final bundle and BUILD-INFO.txt for
seek/scrub, A/V cues, long timelines, real media, DPI, save/reopen/export and external
MCP. Record pass/fail/unperformed separately; parent migration closure records the
owner's acceptance sequencing and does not close these independent issues.
