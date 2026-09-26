# Shared editing API and live MCP adapter

The C# API remains the sole persistent editing authority. The live named-pipe MCP
adapter and stdio bridge are described in [ADR 0005](decisions/0005-mcp-core-migration.md).
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

The C# API returns REVISION_CONFLICT for expectedRevision mismatch. MCP Core
rejects stale guards with `error.code: stale_revision`; re-query and re-plan.
Dry-run runs the same validators and returns diagnostics without committing or
reserving IDs. It does not guarantee a later commit if another caller edits.
There is no durable request deduplication yet. After an uncertain response, query
the explicit IDs/revision before retrying.

A future separately authorized MCP export route would use `IExportService` and transient job IDs/status. The
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


## Scoped live access (Issues #13 / #19)

`Flamoris.Mcp.Core` 1.1.0 supplies official SDK 2.2.0 protocol handling, a
same-user/local-only named pipe, transient 256-bit capability, Read only / Edit
permissions, request limits, cancellation, diagnostics and status projection.
`KachincoMcpHost` marshals onto the WPF dispatcher. There is one EditorSession,
Project, history and monotonic revision; Core never owns these authorities.

Kachinco registers closed `HostTool<T>` DTOs over its existing operations. The
command registry explicitly allows or denies every current `EditCommand` type.
The schema `input.commands.items.oneOf` and decoder reject unknown/duplicate fields,
missing constructor fields, invalid nulls, enum names and integer ticks. Batches
have 1–64 commands. Domain validation and atomic rollback remain in EditorSession.

### Wire compatibility change

Call `mcp.context` with `{}` to obtain `runtimeId`, transient `documentToken`,
revision and permission. Kachinco tools now take Core's `{ input, guard }`
envelope. Queries require `input`; guard is optional. Mutations require a guard
with runtime/document identity and decimal-string `expectedRevision`.
`expectedRevision` is no longer an application input field. Example:

```json
{
  "name": "edit_batch",
  "arguments": {
    "guard": {
      "runtimeId": "<mcp.context runtimeId>",
      "documentToken": "<mcp.context documentToken>",
      "expectedRevision": "1"
    },
    "input": {
      "commands": [{
        "type": "SetTrackEnabled",
        "sequenceId": "00000000-0000-0000-0000-000000000002",
        "trackId": "00000000-0000-0000-0000-000000000005",
        "enabled": false
      }],
      "dryRun": false
    }
  }
}
```

`undo`/`redo` use empty `input` and the same required guard. `get_project` keeps its
v2 Project envelope, string revision, current UI context and shared history flags.
Domain results retain `success`, string revision and structured diagnostics.
Clients must check domain `success` as well as MCP `isError`: Core's `isError`
represents boundary failures, whose structured/text content contains
`{ "error": { "code": "stale_revision" } }` (or other Core error codes).
No application-local JSON-RPC error mapping or protocol negotiation remains.
Core 1.1.0 uses official C# SDK 2.2.0's modern 2026-07-28 and legacy initialization.

### Permission and lifetime

Read only admits inspection, Clapper resolution and bounded Recipe validation.
Edit adds ordinary typed edits and shared history; it never grants a filesystem
path. `CreateProject`, `RegisterMedia`, `RelinkMedia`, `SetGeneratedProvenance`
remain forbidden. `recipe_generate`, `export_start`, `job_status`, `job_cancel`
are unregistered (`unsupported_capability`) until separately reviewed source/output
permissions exist. UI import, generation, export and retention of generated files
for Undo/Redo remain unchanged.

Every enable creates a fresh `flamoris-...` pipe and capability. Connection copy
provides transient client-launch JSON (`command`, `args`, `env`). Pass the capability
only in `FLAMORIS_MCP_CAPABILITY`, never argv, persistent client settings, Project
or logs. The bridge removes its inherited environment variable at startup.

Stop, permission rotation, valid-load replacement admission, New, document loss
and shutdown revoke before further access and close active/waiting connections.
Session document-instance identity changes even on same-ID replacement. Human
Redo cannot resurrect old access after Undo to null. Failed/cancelled file loading
preserves access; the existing UI revokes before attempting valid-load replacement.

Every mutation enters `RequestContext.CommitAsync` once and uses the ordinary
session operation. Core checks guard/permission/busy/cancellation immediately before
that callback; EditorSession checks cancellation before history changes. Prepared
work cannot commit after a cancelled/expired/revoked request. A commit itself may
remove the document and revoke its own connection: a lost/cancelled response then
requires reauthorization and inspection, not automatic retry. No hidden rebase or
request deduplication exists.

Healthy idle has no deadline. Started-frame deadline: 2 minutes; request: 15 seconds;
write: 5 seconds. Input is strict UTF-8, depth bounded, at most 4 MiB per frame;
concurrent requests are bounded by Core (default four). One bridge attaches at a
time. Core status supplies endpoint availability (green), authenticated connection,
and foreground activity; it does not identify a particular AI. Kachinco projects
busy/active pointer gestures and retains normal editing when transport fails.
