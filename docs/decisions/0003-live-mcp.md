# Live editor MCP adapter

Issue #5. Protocol compatibility target: MCP 2025-03-26
([lifecycle](https://modelcontextprotocol.io/specification/2025-03-26/basic/lifecycle),
[tools](https://modelcontextprotocol.io/specification/2025-03-26/server/tools)).
The target is explicitly versioned; no claim of implementing newer protocols.

A small console bridge carries newline-delimited stdio JSON-RPC to the running
Windows editor through a random named pipe restricted to the same OS user.
The editor explicitly starts/stops that endpoint. No TCP listener or cloud service.
The bridge owns no Project, history, evaluator, or command implementation.

Each connected client initializes before listing/calling tools. Project query
returns the durable project envelope plus string revision and transient selection /
playhead supplied by the editor. `edit_batch` dispatches an allowlist of typed
commands through the existing session on the UI dispatcher. Expected revision is
required for mutations; dry-run uses that same path. Undo/redo use the same history.
The original Issue #5 exported background jobs; Issue #13 below supersedes that
external admission contract until a separate file grant exists. Commands contain decimal-string ticks, stable UUIDs and exact
enum names. Invalid or unknown fields are rejected at the typed wire boundary.

Messages are bounded to 4 MiB UTF-8 per line. Oversized/invalid messages fail without
project edits. Closing the editor stops the endpoint. Native stdio
clients must use the displayed pipe name; no arbitrary editor-instance selection.

## Issue #13 scoped attachment hardening

Baseline: main `366839f12ffae59a64b6c8120b481b57690f2e30`.
Retain stdio → named pipe → the exact WPF EditorSession. Port the revocation
lesson from 2D ADR 0010 / PR #103, not its HTTP/Node architecture.

Each enable grants one transient lease for the current document instance.
Read only permits inspection/discovery and bounded, cancellable literal-only
Recipe compilation (not arbitrary Python execution). Edit additionally permits
explicitly reviewed ordinary commands and shared history. New command types
fail closed. CreateProject, raw RegisterMedia/RelinkMedia and generated provenance
are denied. File output/generation tools remain unavailable to external clients
until a separate reviewed source/output grant is provided; UI production features
remain available. Edit alone never authorizes a path.

Stop, permission change, document replacement and shutdown revoke before further
admission; active streams close and in-flight compilation/jobs are cancelled.
Open cancellation/load failure preserves the attachment; once a valid load is
ready to replace the document, revoke even if the final revision check fails.
New uses the same rule after the discard confirmation. The lease binds to the
existing session and granted Project ID, never to an immutable snapshot reference.
The WPF dispatcher observes that binding on every Refresh (including UI Undo/Redo
and first import); lease operations also check it before access and after shared
history changes. Loss of the Project revokes immediately, before another UI action
can implicitly create a Project. Human Redo remains available, but cannot revive a
revoked grant even when it restores the original ID. Even reopening the same
persistent ID requires a fresh enable. Disconnect is detected on the next pipe IO;
inline bounded compilation may finish without producing persistent state or files.
No external background jobs are admitted in this release. A future file grant must
recheck authorization before commit and final publication.
Cancellation after commit cannot undo committed work: query before retrying an
ambiguous response. Keep previously committed generation files for Undo/Redo.

Trust boundary: all processes of the allowed local Windows user at the permitted
elevation are trusted. Pipe names are addresses, not secrets. No bearer/OAuth
credential is introduced. Server creation must explicitly reject remote clients,
in addition to preserving CurrentUserOnly. A fresh random address is generated
on every enable; old connection windows are cleared/closed on revoke. One client
at a time; idle clients have a bounded timeout and can reconnect while enabled.

Keep strict UTF-8, 4 MiB framing and depth 64. Read/request/write deadlines bound
unresponsive clients. Busy and normal requests use one envelope validator. No
request exception may escape the async UI connection boundary. Discovery derives
constructor schemas from the same explicit command registry used by the decoder;
Core validation remains authoritative. MCP 2025-03-26 remains the declared target.
Official SDK interoperability, exact SDK version and packaged Windows results
must be recorded as evidence, separately from human visual/DPI acceptance.

Primary references checked for this change:
- https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio
- https://modelcontextprotocol.io/specification/2025-03-26/basic/lifecycle
- https://github.com/modelcontextprotocol/csharp-sdk
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea


### Windows creation evidence

Reviewed `.NET runtime v10.0.0` `NamedPipeServerStream.Windows.cs`: CurrentUserOnly
builds a DACL granting FullControl to `WindowsIdentity.Owner`; it does not set
`PIPE_REJECT_REMOTE_CLIENTS`. `WindowsLocalPipe.Create` preserves that owner/DACL
and applies native `PIPE_REJECT_REMOTE_CLIENTS` (0x8) in **dwPipeMode**, atomically
with `FILE_FLAG_FIRST_PIPE_INSTANCE` and overlapped IO. It is not passed through
PipeOptions (which feeds dwOpenMode). The bridge retains CurrentUserOnly and `.`.
The protected ACL and noninheritable handle do not broaden access. There is no
extra secret: all local processes accepted by the existing owner/elevation policy
are trusted. This does not defend against compromise of that account.

Package smoke proves the real native handle accepts the local official client.
A separate-machine SMB/elevation matrix remains a manual check; the product's
remote rejection relies on the documented kernel flag, not firewall defaults.
https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs


The current 2026-07-28 stdio specification still documents newline-delimited
stdio and fallback to legacy initialize. This does not make this implementation
modern: the tested declaration stays 2025-03-26, with SDK 1.0.0 negotiation.
