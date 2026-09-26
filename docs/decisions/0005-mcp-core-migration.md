# ADR 0005: Live MCP through Flamoris.Mcp.Core 1.1.0

Status: proposed for Issue #19. Supersedes ADR 0003's transport/protocol/lease
implementation, preserving its application permission and shared-session policy.

## Decision and authority

Consume `Flamoris.Mcp.Core` 1.1.0 from GitHub Packages. The existing bridge project
becomes a launcher of Core's `StdioBridge`, outputting `Flamoris.Mcp.Bridge.exe`.
The portable package still ships a self-contained runtime under `mcp/`.

External client -> stdio bridge -> authenticated same-user/local-only pipe ->
running WPF -> the SAME EditorSession / Project / Command / Query / Undo / Redo.
`KachincoMcpHost` only projects this authority and marshals onto its dispatcher.
The session exposes a transient document-instance token and a pre-replacement
notification, with no persistence or second revision/history authority. Explicit
Open/New, identity loss/change (including Undo to null), and shutdown invalidate
the boundary. Failed validation does not replace a document. Human Redo cannot
revive old access, even when the persistent Project ID is restored.

Core owns capability generation/authentication, grants, guarded request lifecycle,
admission, cancellation/deadlines, framing, pipe security, official SDK 2.2.0
protocol handling, diagnostics and status/activity projection. Kachinco retains
closed DTO/schema semantics, command allowlist, editor context, file policy,
Recipe compiler, WPF/i18n, logging context and packaging ownership.

Each mutation enters `RequestContext.CommitAsync` once, synchronously calls the
ordinary `EditorSession` operation and refreshes WPF. The session checks the request
token before changing history. No background external file jobs are admitted.
Compilation is bounded and cancellable; Core rechecks authority before disclosure.
Stop/permission rotation revoke first. A cancelled precommit callback cannot commit
later; a completed commit is not undone by a subsequent lost response.

## Compatibility

The six Kachinco tool names and typed command semantics remain. Core adds
`mcp.context`. Tool arguments use Core's `{ input, guard }` envelope; mutation
`expectedRevision` moves into `guard` alongside `runtimeId` and `documentToken`.
Clients must re-query/re-plan on stale guards, never automatically rebase.
Domain results retain `success`, revision and diagnostics; clients inspect
`success` as well as the common transport/tool `isError` and `error.code`.
Old handwritten 2025-03-26 negotiation and error-number mappings are superseded
by the Core 1.1.0 / official C# SDK 2.2.0 compatibility contract.

Read only permits query/resolution/bounded Recipe validation. Edit adds only
reviewed ordinary batch/history operations. CreateProject, raw media registration,
relink, provenance and Recipe/export/job tools remain externally denied.
No generic file/process/eval access is added. UI generation/export are unchanged.

Pipe names are addresses in Core's `flamoris-` namespace. Every enable rotates a
256-bit capability, passed only through `FLAMORIS_MCP_CAPABILITY`, never argv,
Project, settings or logs. Explicit copy presents transient client-launch JSON;
it must not be saved as durable settings. Revoke closes/clears the connection view.
Same-user compromise is outside this capability's threat model.

One client at a time; healthy idle is unbounded. Started-frame, request and write
deadlines remain bounded. Core status drives red/green availability and foreground
activity; provider/tunnel-client management is not introduced.

## Evidence and non-goals

References: Kachinco `d0e028f`, Cutwork `1cdec5a` (ADR 0003, host/tools, packaging),
MCP Core `89331c6` (host integration, security and package consumption).
Tests must cover actual host/shared history, guards, permissions, replacement,
no-late-commit, malformed input/reconnect and published official-client Windows
smoke. Physical Windows visual/DPI/audio acceptance remains separate.

No domain/timebase/preview redesign, HTTP/LAN/cloud, Hub, managed provider,
new media/timeline tools or unrelated UI cleanup.
