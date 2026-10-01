# ADR 0013: bounded preview preparation and independent presentation

Status: proposed for Issue #53.

Native remains the consumed-sample clock, generation and decode/presentation policy
authority. The serialized managed host runs independent asynchronous audio, video
preparation and presentation loops. Native presentation does not reserve decode work;
native video scheduling skips missing old requests before decoding. A ready frame is
discarded only when another prepared frame is already due, preserving the newest
available image under sustained overload.

At most three forward frames exist across ready storage plus one in-flight request
(the in-flight request occupies one of those three slots). The displayed frame and
startup/frozen frame are separate. Each prepared frame retains immutable renderer
RGBA and an owned BGRA32 presentation buffer; this explicitly doubles pixel storage
for those bounded frames, without growing either native cache. Worker tasks perform
cache/key/decode/composition and native channel copying. WPF uses the BGRA backing
array for its final WritePixels copy, without a per-presentation array or pixel loop.
Export and native cached payloads keep their existing RGBA/PCM contract.

All native playback decisions and queue mutations stay on the host synchronization
context. The workers never mutate that handle or call the physical audio device.
Cancel/seek/quality/context changes supersede generation, freeze the device, and
join both producers before a new session starts. Source workers use immutable
contexts; outside-window edits still refresh the context between requests.

Fixed-size 128-request timing windows report recent p95 plus lifetime average/max.
Logs aggregate at most once per second per video/audio/controller category, with a
final session summary. Presentation conversion and WritePixels are measured separately.
Windows physical Full/Half/Quarter A/V acceptance remains explicitly pending.

Same-driver measurements also isolated packet polling as a dominant preparation
cost. Forward video/PCM blocks now use one bounded worker with native blocking
pipe reads, not managed two-millisecond polling per packet. The host registers
both owner cancellation and a 30-second deadline to terminate the owned writer,
which unblocks the read. POSIX waits on pipe readiness; Windows reads directly.
Each call still borrows a pinned slice only for its duration. General process
stream readers keep their existing bounded-time interface. Capability 2048 covers
the new presentation and worker-read entry points; older DLLs fail the startup
capability check without an ABI layout change.
