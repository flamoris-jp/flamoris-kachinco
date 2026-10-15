# ADR 0018 — Bounded MCP media upload

Status: accepted for implementation under #76; desktop/Hub rollout is separate.

## Problem and ownership

AI-assisted video production required a human to copy generated WAV and alpha MOV
files into the media bin. Add a narrowly scoped byte-transfer capability to the
existing live editor. Kachinco owns staging, validation and media registration;
MCP Core still owns grants/guards and Hub only forwards calls. There is no second
project, media registry, HTTP listener or provider integration.

## Contract

All four tools require the current Edit grant and runtime/document/revision guard.
Their input is closed; Int64 values are decimal strings.

- `media_upload_begin`: `uploadId` (caller-created UUID), `fileName` (basename,
  max 180 characters), `sizeBytes` (1–536870912), `sha256` (lowercase hex).
  Returns upload ID, received/total bytes and 131072-byte chunk limit.
- `media_upload_write`: `uploadId`, `offset`, `dataBase64`, `chunkSha256`.
  Writes the next contiguous chunk. Repeating identical already-written bytes is
  harmless; a different retry or a gap fails. Repeating begin with identical
  metadata reports the current received offset. Neither operation changes history.
- `media_upload_finish`: `uploadId`. Rehash the complete file and inspect it with
  the existing `IMediaProbe`, then recheck the guard before atomically publishing
  it and executing ordinary `RegisterMedia`. Return asset ID and new revision.
- `media_upload_cancel`: `uploadId`. Delete only an unfinished owned transfer.

`uploadId` is also the eventual `mediaAssetId`. After an uncertain finish, inspect
`get_project` for that ID; never automatically repeat a failed transport call.
Changing revision during preparation leaves the transfer available for an explicit
retry with a fresh guard. Begin/write/cancel require guards even though their
bounded staging effects do not change project revision.
Staging calls are background activity; only a finished import triggers the normal
successful-edit notification.

Supported extensions match ordinary import: PNG/JPEG/WebP, MOV/MP4, WAV/MP3/M4A.
The probe, not the caller, supplies kind, duration, image limits and audio metadata.
No tool accepts a filesystem path, URL, executable or arbitrary media metadata.
Images use the existing five-second import duration.

## Files and lifetime

The host chooses `%LOCALAPPDATA%/FLAMORIS/Kachinco/imported-media`. Each attachment
gets a private staging directory and at most two unfinished transfers (at most
1 GiB reserved). Names on disk are generated UUIDs plus a supported extension;
the supplied basename is display metadata only. Staging expires after 30 minutes
without activity and is removed on cancellation, grant revocation or shutdown.
Hash/probe failures remove unfinished files. Probe preparation is bounded to 20
seconds and can be interrupted by cancellation/revocation. Request cancellation
leaves a live attachment's transfer resumable; revocation discards it. A one-minute
sweep removes transfers idle for more than 30 minutes while the process is alive.
An abrupt process/OS crash can leave staging files; crash recovery and durable-file
garbage collection are not included in this slice.

Only finish publishes a durable file. Failed/stale/cancelled commits cannot add
an asset. If registration fails, publication is rolled back. Successfully imported
files survive Undo, reconnect, project switching and shutdown for Redo and saved
projects; automatic garbage collection of imported media is outside this change.
Project v1/v2/v3/v4 formats and timeline placement are unchanged. Inserting the
new asset on a track remains an ordinary explicit `InsertClip` command.

## Acceptance

Cover closed schemas, real WAV/MOV/MP4 import, bad names/extensions/digests,
size/chunk bounds, duplicate/out-of-order chunks, stale revision/document,
read-only denial, cancellation/revocation during probing, rollback, Undo/Redo and
reopen. Hub tests forward maximum chunks and guarded receipts through its existing
HTTP/reverse-WebSocket boundary, including offline/no-replay behavior.
