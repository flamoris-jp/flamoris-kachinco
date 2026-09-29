# ADR 0008: native media resource ownership

Status: proposed for #33; accepted when its PR is merged.

## Scope and staging

Keep C# EditorSession and TimelineEvaluator as authority. Preserve the external
FFmpeg/ffprobe executables and current CLI semantics; do not bundle codec libraries.
Move child-process/pipe lifetime, decoded storage and bounded cache primitives into
C++, then switch the existing media adapters only after real-codec parity tests.

The first implementation commit adds a cancellable native pipe boundary. It is
not, by itself, completion of #33. Managed adapters may retain domain projection,
canonical decimal seconds formatting and existing diagnostic mapping during this
phase. Native decode/forward-window/cache ownership must be verified before closing
#33; playback scheduling and editing cutover remain #34 and #35 respectively.

## Process boundary

No shell interpolation. Windows uses UTF-8 to UTF-16 conversion, explicit argument
quoting, an inherited-handle allowlist, suspended creation and a kill-on-close job.
POSIX uses posix_spawnp, close-on-exec pipes and a dedicated process group. Only
stdout/stderr are exposed, and stdin is the null device. Each channel has a single
serialized reader. Native reads poll with bounded waits; cancellation kills the
owned process tree/group. SafeHandle pins active calls. Managed disposal requests
cancellation before releasing the owning handle, so a blocked read cannot retain a
child forever. Native code never calls back into managed code.

Read buffers are caller-owned and never retained. Native child objects own their
OS handles until deterministic disposal. Status and OS error numbers cross as
integers; no process-wide last-error string or credential data is exported.
Arguments/paths remain private execution inputs and are not added to diagnostics.

## Acceptance still required

Generated MOV/MP4/WAV/MP3/M4A fixtures, accurate random/forward reads and reset,
PTS metadata, partial PCM/EOF behavior, process start failure, cancellation,
pipe saturation, Unicode/quoted paths, cache bounds/invalidation and repeated
open/close. Run on Linux and Windows, including portable startup/shared MCP.

## Current cutover and retained adapters

The native boundary owns FFmpeg/ffprobe processes, pipe reads/cancellation, immutable
RGBA/PCM buffers, PCM validity/tail padding, and the preview payload LRU. Both random
and forward production reads use this path. Preview and export retain existing C#
command construction and the same external codecs; ffprobe JSON projects through
the existing MediaProbeParser. No CLI option, stream selection or duration parsing
policy changes in this phase.

The managed forward adapter still selects the bounded stream pool and projects
showinfo PTS using canonical time. Explicit owner cancellation/seek resets release
native stream handles; forward frame and sample buffers use native primitives.
Moving this scheduling/evaluation orchestration is #34, not an alternate editor.
The managed PreviewCache remains only for WPF thumbnail presentation and the parity
oracle. Production frame/audio payloads use the native cache. Immutable cache values
preserve tick/index/dimensions/pixels/samples; managed reference identity is not part
of the ABI. The clip-boundary regression now verifies all those values, hit/miss
counters and process reuse instead of object reference identity.

Cached payload budgets preserve the existing 96 MiB video / 8 MiB audio limits and
256-entry ceiling. Each entry adds at most 32 bytes of fixed transport metadata.
Leases hold immutable shared buffers through eviction/clear; managed adapters copy
into the current presentation values and immediately release the lease. Domain
snapshots, provenance and persistence never enter this transport serialization.

Linux native execution is a glibc test-host path; closefrom spawn actions prevent
unrelated parent file descriptors from entering codec children. Windows remains the
supported desktop distribution. ABI 1 adds process/cache capabilities; app startup
rejects a stale phase-1 DLL before opening the editor.

## Validation notes

The native process test warms the Windows CRT/thread path once before measuring
handle growth. Observed Windows counts were 60 before first use, then 65 after
iterations 0, 4 and 31, and still 65 after saturation/cancellation. Every later
iteration and final teardown must stay at or below the warmed count. This measures
leaks without conflating one-time runtime initialization with per-process ownership.
Native suites also run under Linux ASan/UBSan/leak checks. Managed native boundary
tests run on both OSes; existing real-codec and packaged editor/MCP regressions remain
gates. No subjective A/V quality or universal speed improvement is claimed here.
