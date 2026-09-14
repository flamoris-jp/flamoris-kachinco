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
`recipe_validate`, `recipe_generate`, `export_start`, `job_status`, `job_cancel`.
Tool schemas describe arguments; batch command fields match camelCase C# constructor
names. Int64 values are decimal strings, IDs are UUIDs, enums are exact names.
`get_project` includes the v2 envelope and transient visible sequence/clip/playhead.

Example command within `edit_batch.commands`:

```json
{"type":"SetSequenceDuration","sequenceId":"00000000-0000-0000-0000-000000000002","durationTicks":"352800000"}
```

A Recipe job first compiles/renders outside the session and commits only if the
captured revision is still current. `replaceMediaId` explicitly preserves existing
clip placement; incompatible source ranges fail. Old MOV generations remain on disk
for Undo. Paths refer to the machine running Kachinco, not the MCP client's host.
