# Shared editing API and live MCP adapter

The C# API remains the sole persistent editing authority. The live named-pipe MCP
adapter and stdio bridge are described in [ADR 0003](decisions/0003-live-mcp.md).
Persistent file JSON remains a separate versioned boundary.

## Lifecycle, queries and mutations

| Surface | Contract |
| --- | --- |
| `new EditorSession()` | Empty transient editing session; no IO |
| `GetProject()` | Immutable ProjectSnapshot: project, revision, canUndo, canRedo |
| `GetSequence(sequenceId)` | Structured result with sequence or SEQUENCE_NOT_FOUND |
| `TimelineQueries.ListSequences/ListMediaAssets` | Explicit stable order by ordinal UUID |
| `TimelineQueries.ListClips/ListCaptions` | Ordered by startTicks then ordinal UUID; use IDs to address entries |
| `Execute(EditBatch)` | Ordered typed commands, optional expectedRevision, optional dryRun |
| `Undo/Redo(expectedRevision)` | One committed batch, revision increment, structured failure |
| `ReplaceProject(project, expectedRevision)` | Validated Open/New lifecycle; clears history; no filesystem IO |
| `ProjectFileStore.LoadAsync/SaveAsync` | File IO boundary with cancellation and diagnostics |
| `TimelineEvaluator.Create/Evaluate` | Validated immutable source snapshot and half-open frame evaluation |
| `ExportPlanner.Create/EvaluateFrame` | Rational export plan using that same evaluation |

Snapshot project includes every sequence, track, asset, clip, caption, duration,
source range, enabled flag and property. Tracks retain bottom-to-top semantic order.
To make an AI decision atomically, query **one ProjectSnapshot** and use its revision
for all operations; don't pair a stale sequence query with an unrelated revision.

## Commands

- `CreateProject(projectId, name)` — only on an empty session.
- `CreateSequence(sequenceId, name, settings, durationTicks)`.
- `RegisterMedia(asset)` — explicit validated MOV/WAV metadata. Human import first
  obtains it through `IMediaProbe`; Core still has no file/process dependency.
- `RelinkMedia(mediaAssetId, sourcePath, durationTicks, sampleRate, channels)` —
  replaces source metadata for the same logical asset after probe/compatibility
  validation; referenced clip IDs and placements do not change.
- `AddTrack(sequenceId, trackId, name, kind)` — appends above existing tracks.
- `InsertClip(sequenceId, trackId, clip)` — explicit asset ID and source/timeline range.
- `MoveClip(sequenceId, clipId, targetTrackId, startTicks)`.
- `TrimClip(sequenceId, clipId, startTicks, sourceInTicks, durationTicks)`.
- `SplitClip(sequenceId, clipId, splitTicks, rightClipId)` — strict interior split.
- `DeleteClip(sequenceId, clipId)`.
- `SetClipProperties(sequenceId, clipId, enabled, appearance, audio)`.
- `SetTrackEnabled(sequenceId, trackId, enabled)`.
- `ReorderTrack(sequenceId, trackId, newIndex)` — zero-based bottom-to-top index.
- `AddCaption(sequenceId, trackId, caption)` / `DeleteCaption(sequenceId, captionId)`.

`TimelineEditPlanner` is the shared Phase 1 gesture/adapter planner for move, trim
and split. `TimelineViewport` converts pixels to canonical ticks with explicit
decimal round-half-away-from-zero behavior. `TimelineSnapping` compares integer
tick distances and resolves equal distances to the lower tick. None is persistent
state, and all resulting edits still execute through `EditorSession.Execute`.

Each command must leave a valid candidate before the next executes. Add referenced
assets/tracks before clips. A failure rolls the whole batch back. An unknown command
fails UNSUPPORTED_COMMAND. Caller-generated IDs make dependent batch operations
unambiguous. Empty/null operations and invalid domain data are rejected.

## Example (headless human or AI adapter)

```csharp
var session = new EditorSession();
var projectId = Guid.NewGuid();
var sequenceId = Guid.NewGuid();
var assetId = Guid.NewGuid();
var trackId = Guid.NewGuid();
var clipId = Guid.NewGuid();
var result = session.Execute(new EditBatch([
    new CreateProject(projectId, "FLAMORIS"),
    new CreateSequence(sequenceId, "MV", SequenceSettings.Landscape,
        8 * TimelineTime.TicksPerSecond),
    new RegisterMedia(new MediaAsset(assetId, "Shot", "shot.mov", MediaKind.Mov,
        8 * TimelineTime.TicksPerSecond)),
    new AddTrack(sequenceId, trackId, "V1", TrackKind.Video),
    new InsertClip(sequenceId, trackId, new Clip(clipId, assetId, 0, 0,
        8 * TimelineTime.TicksPerSecond, true,
        ClipAppearance.Default, AudioProperties.Default))
], ExpectedRevision: 0));
// Check result.Success; inspect result.Diagnostics before saving or proceeding.
// session.Undo() undoes this entire insertion, including project creation.
```

## Wire adapter requirements

Use nonempty UUID strings for IDs and decimal Int64 strings for ticks/revisions,
plus explicit rational FPS integers. Reject unknown operations/fields and preserve
diagnostic code, severity, entityId and path. Do not serialize the abstract command
hierarchy with unsafe CLR type metadata. The live adapter uses an explicit `type` discriminator and a closed C# command
allowlist with strict constructor-field decoding. Reuse Core validation afterwards.

An expectedRevision mismatch returns REVISION_CONFLICT; re-query and re-plan.
Dry-run runs the same validators and returns diagnostics without committing or
reserving IDs. It does not guarantee a later commit if another caller edits.
There is no durable request deduplication yet. After an uncertain response, query
the explicit IDs/revision before retrying.

MCP export operations will use `IExportService` and transient job IDs/status. The
FFmpeg encoding boundary receives already rendered/mixed media; no effect, clip,
caption or time semantics may be recreated in an MCP tool or FFmpeg command string.

## Production commands and tools

- `SetSequenceDuration(sequenceId, durationTicks)` rejects shrinking through existing items.
- `TimelineEditPlanner.Place` produces full-duration insertion plus any necessary
  explicit duration extension as one revision-checked batch.
- `UpdateCaption(sequenceId, captionId, startTicks, durationTicks, text, enabled)`.
- `AddClapper/UpdateClapper(sequenceId, clapper)`; `DeleteClapper(sequenceId, clapperId)`.
- `AddRecipe/UpdateRecipe(sequenceId, recipe)`; updates advance revision by one.
- `SetGeneratedProvenance(mediaAssetId, provenance)` is used with generated-media replacement.
- `ClapperQueries.Resolve(project, sequenceId, name)` uses sequence-local unique ordinal names.

MCP tools: `get_project`, `edit_batch`, `undo`, `redo`, `clapper_resolve`,
`recipe_validate`. Read only exposes inspection, Clapper resolution and bounded Recipe
validation; Edit additionally exposes batch and shared history.
Tool schemas describe arguments; batch command fields match camelCase C# constructor
names. Int64 values are decimal strings, IDs are UUIDs, enums are exact names.
`get_project` includes the v2 envelope and transient visible sequence/clip/playhead.

Example command within `edit_batch.commands`:

```json
{"type":"SetSequenceDuration","sequenceId":"00000000-0000-0000-0000-000000000002","durationTicks":"352800000"}
```

UI Recipe generation first compiles/renders outside the session and commits only if the
captured revision is still current. `replaceMediaId` explicitly preserves existing
clip placement; incompatible source ranges fail. Old MOV generations remain on disk
for Undo. Paths refer to the machine running Kachinco, not the MCP client's host.


## Scoped live access (Issue #13)

Each explicit enable creates a fresh, document-bound `McpAccessLease`. This is only
transient authorization. No second Project, editing session, history or timebase
is created. WPF and the one admitted client serialize on the same dispatcher;
Core still serializes state/history with its gate. Revisions remain monotonic.

`edit_batch.commands.items.oneOf` describes every exposed constructor, nested
record, enum, nullable field, UUID and decimal-string Int64. Unknown fields,
computed properties, null nonnullable fields and unknown commands are rejected
before Core. A separate explicit permission disposition must approve each command.
Batches are limited to 64 commands (the internal Core limit is unchanged).
`CreateProject`, `RegisterMedia`, `RelinkMedia`, `SetGeneratedProvenance` are denied.
Use already imported asset IDs for ordinary clip editing. The UI owns Open/New,
import/probe/relink and output selection. No generic filesystem/process tool exists.

External `recipe_generate`, `export_start`, `job_status`, `job_cancel` are intentionally
unavailable until source IDs and exact output targets have a separate reviewed UI
grant. Direct invocation fails closed, including in Edit. Their unsafe background
job admission path is removed; no externally accepted Recipe can later publish or
commit after Stop. UI Recipe/export services and generated-file Undo retention are
unchanged. The lease commit guard is barrier-tested against prepared generation.

Stop, permission rotation, successful-load replacement admission, New and shutdown
revoke first, cancel in-flight compilation and close active/waiting pipes. A failed
load or cancelled chooser preserves access; a revision failure after a valid load
is ready leaves MCP disabled. Reopening the same project ID requires re-enable.
Normal bridge disconnect is observed at the next pipe IO; an inline compiler may
finish within its five-second limit, but produces no Project/file output. No MCP
background jobs survive disconnect in this release. Stop cancels immediately.

Requests and responses are bounded to 4 MiB; strict UTF-8/depth 64 are retained.
Read idle/partial-frame deadline: 2 minutes; request: 15 seconds; write: 5 seconds.
Malformed envelopes use -32700 (invalid JSON) or -32600 (invalid envelope), tool
arguments -32602, unknown methods -32601, permission -32001. Domain diagnostics stay
inside tool results with `isError`. No hidden retry/rebase or request deduplication.
A lost response after commit is ambiguous; query IDs/revision before retrying.

Protocol target: 2025-03-26. The test-only official C# SDK package is pinned to
`ModelContextProtocol.Core` 1.0.0. Modern-only per-request metadata/server discovery
is not implemented or claimed. No Product SDK dependency was introduced.
