# `.fkproj` v1 contract

UTF-8 JSON. Required envelope fields: `format: "flamoris-kachinco"`,
`schemaVersion: 1`, `timebase: "35280000"`, `project`.
All fields below are required, including explicit `null` for optional media
metadata. Unknown fields/versions, duplicate property names, null required fields,
integer enum encodings and uninitialized arrays are rejected. No migration from
an invented v0 is provided. Envelope version is inspected before payload decoding.

| Object | Fields |
| --- | --- |
| Project | id, name, assets[], sequences[] |
| Asset | id, name, sourcePath, kind (`Mov`/`Wav`), durationTicks, sampleRate (integer/null), channels (integer/null) |
| Sequence | id, name, settings, durationTicks, tracks[] |
| Settings | width, height, fpsNumerator, fpsDenominator |
| Track | id, name, kind (`Video`/`Audio`/`Subtitle`), enabled, clips[], captions[] |
| Clip | id, mediaAssetId, startTicks, sourceInTicks, durationTicks, enabled, appearance, audio |
| Appearance | transform, opacity, blend (`Normal`/`Screen`) |
| Transform | x, y, scaleX, scaleY, rotationDegrees |
| Audio | gain, muted |
| Caption | id, startTicks, durationTicks, text, enabled |

IDs are globally unique nonempty UUID strings. All tick fields and timebase are
signed 64-bit decimal **strings**, preserving exact integers for JavaScript/MCP
clients. Valid persisted times are nonnegative, durations positive; additions must
fit Int64. Other integer settings are JSON numbers. FPS must be reduced and 1–240.
Canvas is initially 1920×1080 or 1080×1920. RGB properties are finite numbers.

The source range is `[sourceInTicks, sourceInTicks + durationTicks)` at rate 1.
Timeline range is `[startTicks, startTicks + durationTicks)`, contained in sequence.
Only WAV maps to audio tracks and MOV to video tracks in this version. A MOV's
embedded audio is not automatically included. Caption text retains newlines.

Save order: assets/sequences by ordinal UUID, tracks in semantic bottom-to-top
order, clips/captions by start then ordinal UUID. Load preserves identity and
meaning; source array order for clips is not a compositing override.

Source paths are local absolute or project-relative references. No media is embedded
or decoded. Opening succeeds without touching media, after which the Phase 1 resolver
reports availability by stable asset ID. Relative paths resolve against the opened
`.fkproj` directory. Relink probes a compatible replacement and commits a typed
`RelinkMedia` command that preserves the asset ID and every referencing clip ID and
placement. Save As retains the reference text, so absolute paths remain preferable
when moving a project between directories.

No history, revision, UI state, caches or FFmpeg options are persisted. Read/write
limit is 16 MiB for this milestone. Save validates first, writes/flushed a temporary
sibling, then replaces the target. Atomic replacement is best effort on the local
filesystem, not a cross-machine/distributed durability contract.
