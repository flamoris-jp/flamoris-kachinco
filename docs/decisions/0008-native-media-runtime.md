# ADR 0008: native media resource ownership

Status: design in progress for #33; no media cutover accepted yet.

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
