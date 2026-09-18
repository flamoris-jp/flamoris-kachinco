# Issue #13 self-review and evidence

Baseline: main `366839f12ffae59a64b6c8120b481b57690f2e30`.
Read AGENTS, Issue #13, ADR 0003, 2D Issue #102 / merged PR #103 and ADR 0010.
No HTTP, second editable session, copied Project/history, persistent schema or
35,280,000-tick timebase change. The Recipe service's pre-existing isolated dry-run
validator was neither added nor repurposed as MCP authority.

## Reviewed boundaries

- Every external mutation uses the original EditorSession.Execute/Undo/Redo.
  The transient lease serializes revocation with commit and Core rechecks request
  cancellation before changing history. Normal WPF refresh follows successful edits.
- Closed typed registry and explicit allow dispositions; reflective schemas cannot
  expose a new command. Constructor shape validation rejects ignored/computed
  properties as well as unknown fields. Reflection is schema description only.
- Read only rejects mutation/history even via direct calls omitted from discovery.
  Edit rejects CreateProject, RegisterMedia, RelinkMedia and generated provenance.
- File-capable external jobs are deliberately disabled per Issue #13's fail-closed
  option. The unsafe external background job admission/commit code was removed.
  UI Recipe/export paths and all previously committed generated files are unchanged.
  A future source/output grant is not implicitly authorized by this change.
- Revoke precedes both UI ReplaceProject call sites, regardless of persistent ID.
  Failed/cancelled loading is intentionally distinct from replacement admission.
- PR #14's P1 found a missed lifecycle: Undo of the initial CreateProject batch
  restores null; UI AddSequence/first import can then implicitly create a different
  Project. The lease now binds to the existing session and stable Project ID.
  Every shared WPF Refresh observes document loss before the next dispatcher action;
  lease admission and post-operation checks also revoke on null/different identity.
  UI Undo/Redo and first import use Show/Refresh; MCP history uses lease.Run then
  Refresh. Same-document immutable snapshot changes preserve access. Human Redo
  still works after revocation and cannot reactivate the old lease. Explicit Open
  still revokes for the same persistent ID. No Core history or revision was added.
  Stop/rotation/Closed cancel inline compilation and close the native handle.
- Buffered requests yield to WPF input, preventing a fast client from starving
  Stop/New/permission changes. A connection failure never escapes onto async-void UI.
- Native CreateNamedPipe applies PIPE_REJECT_REMOTE_CLIENTS in dwPipeMode and the
  same owner/DACL as .NET 10 CurrentUserOnly, with first-instance and noninheritable
  overlapped handles. No firewall assumption, secret-as-address claim or TCP listener.
- Pipe address is renewed on enable. Stale connection windows close and clear;
  already copied client configuration is invalid rather than silently retargeted.
- Strict UTF-8, 4 MiB frames/responses, depth 64, 64 commands, read/request/write
  timeouts. Bridge EOF cancels pumps; a noncancellable console reader cannot hang
  process exit indefinitely. Query-before-retry remains necessary after commit.
- Official C# SDK 1.0.0 exists only in WindowsSmoke. Product has no new NuGet
  runtime dependency. Existing package/workflow/artifact retention boundaries remain.

## Automated evidence

Final CI links/results are recorded in the PR. Headless regression, solution build,
existing Windows worker/raster/preview smoke, fresh publish and external-process
package acceptance are separate gates. The package driver asserts negotiated
2025-03-26, full discovery/calls, track+caption atomic edit, automatic UI text
projection, UI Undo/Redo, UI edit seen by MCP, MCP history, rollback, stale revision,
read-only direct rejection, New/permission/Stop revocation, stdin/editor EOF and
package test-dependency exclusion. P1 regression coverage adds Edit UI/MCP Undo
of Project creation, Read only UI Undo, human Redo without grant resurrection,
implicit AddSequence creating B, denial of old query/edit/pipe access, explicit
re-enable and editing B, plus native Open-dialog same-file reopen revocation.
Headless cases additionally cover first-import implicit creation and same-ID
immutable edits/history preserving access. Results/commit/run links are in the PR.
Editor and bridge run with System32-only PATH;
the external test driver retains its own .NET/official SDK dependencies.

The deterministic prepared-generation barrier test prepares ordinary commands,
revokes, enables a fresh lease, then resumes the old commit attempt at the same
revision. The old attempt cannot mutate Project/history. This is a guard test,
not a claim that external file generation remains enabled.

Local Linux workspace has no dotnet executable: the three requested local dotnet
commands returned command-not-found. XML parsing and git diff whitespace checks
were performed locally; build/runtime results come from GitHub Windows/Linux CI.
No successful local WPF execution is claimed.

## Human acceptance still required

- Real artwork/audio, Ctrl+Z/Ctrl+Y keyboard focus, Japanese/English usability and DPI.
- Open/reopen a real user file and inspect connection copy/window usability.
  Synthetic native-dialog same-file revocation is automated separately.
- Different-user/elevation and actual second-machine SMB client matrix. Kernel flag
  enforcement is the implementation guarantee; no remote exploit reproduction or
  live multi-machine security test is claimed.

Use `Refs #13`; do not mark human acceptance complete or close unrelated Issue #5.
