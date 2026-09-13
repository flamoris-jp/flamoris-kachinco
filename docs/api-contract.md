# Shared editing API and future MCP adapter

This milestone exposes a callable C# API, **not a network MCP server or JSON-RPC
dispatcher**. Future transport schemas must map these commands instead of
implementing a second editor. Persistent file JSON is a different versioned boundary.

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
- `RegisterMedia(asset)` — explicit MOV/WAV metadata, no decoding/probing.
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

## Future wire adapter requirements

Use nonempty UUID strings for IDs and decimal Int64 strings for ticks/revisions,
plus explicit rational FPS integers. Reject unknown operations/fields and preserve
diagnostic code, severity, entityId and path. Do not serialize the abstract command
hierarchy with unsafe CLR type metadata. Define an explicit versioned operation
discriminator and schema when adding transport. Reuse Core validation afterwards.

An expectedRevision mismatch returns REVISION_CONFLICT; re-query and re-plan.
Dry-run runs the same validators and returns diagnostics without committing or
reserving IDs. It does not guarantee a later commit if another caller edits.
There is no durable request deduplication yet. After an uncertain response, query
the explicit IDs/revision before retrying.

MCP export operations will use `IExportService` and transient job IDs/status. The
FFmpeg encoding boundary receives already rendered/mixed media; no effect, clip,
caption or time semantics may be recreated in an MCP tool or FFmpeg command string.
