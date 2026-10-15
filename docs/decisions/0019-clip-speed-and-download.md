# ADR 0019 — Clip speed and edited clip download

Status: Accepted for implementation of #78 and #79.

## Speed

Native `SetClipSpeed(sequenceId, clipId, playbackRate)` accepts 1, 2, 4 or 8.
Slow motion, reverse, variable speed and pitch preservation are outside this first
slice. Audio changes pitch with speed; video embedded audio remains separate.
Source-in and exact source duration are retained; timeline duration is the ceiling
of source duration / rate (less than one tick of padding). Source queries use
source-in + local tick * rate, capped to the last source tick. No ripple occurs:
other clips/tracks and sequence duration stay fixed. An extension that exceeds the
sequence or overlaps the next clip fails atomically; extend/move in the same batch.
Volume/visual keys follow source time: speed edits scale signed local ticks by
old/new rate, truncating toward zero; colliding keys or overflow reject the edit.
Trim shifts keys by source delta / rate and derives the new source interval;
changing placement alone retains source duration. Split partitions source duration
at local split * rate and shifts the right keys by the timeline split offset.
Duplicate/move retain rate and source duration. Shared native evaluation supplies
preview, capture and export. Native sample decimation advances PCM at rate 1/2/4/8
with ordinary per-output-sample envelope evaluation; no separate FFmpeg timeline.

Schema v5 requires playbackRate and decimal-string sourceDurationTicks on every
clip, plus all v4 fields. Only non-unit speed or a retained fractional-tick source
span selects v5; v1–v4 migrate to rate 1 with implicit source duration. Removing
speed can return to legacy conditional v2/v3/v4 if no extra span is needed.
Existing ABI layouts stay unchanged; additive rate APIs require capability 16384.

## Download

Per-attachment read-only tools: `clip_download_begin`, `clip_download_status`,
`clip_download_read`, `clip_download_cancel`. Begin captures a validated immutable
snapshot and schedules bounded background rendering. Existing native evaluator,
SharedFrameRenderer/SharedAudioRenderer and MP4 encoder remain authoritative.
Only the selected clip contributes. The original sequence canvas and local key
positions are preserved. WAV is 48 kHz stereo PCM16; MP4 is H.264, silent, with
black opaque background (no alpha), sequence rational FPS and Full/Half/Quarter
canvas. Image clips may also use MP4. No source-file/raw download or path inputs.
Ranges use clip-relative timeline ticks. Maximum 30 s, 64 MiB output, 128 KiB
chunks, one job per attachment; expiry is ten minutes from last access. One active
render per window; cancellation and a two-minute deadline reach codecs/renderers.
The ordinary disabled/muted result is silence/black. Explicit `inspectDisabled`
ignores only track/clip enable and audio mute; gain/curves remain authored values.
This changes only a derived snapshot, never the project/history.

Each access passes the usual ReadOnly/Edit grant and runtime/document/revision
guards. Open/New, disconnect, revocation, cancellation and expiry destroy temporary
outputs. Any intervening edit or project-path change invalidates a job and its
bytes. Source availability/stamps are checked before and after rendering and on
reads. Metadata includes original IDs/revision, requested duration, encoded
frame/sample duration, format, size, SHA256, dimensions/FPS or rate/channels.
Read returns actual bounded base64 bytes, offset, count and per-chunk SHA256; Hub
forwards JSON without owning media. Clients assemble in offset order and verify
size/SHA256, save the advertised filename, then use their supported file/audio
input or analysis tool. Successful download does not imply a model can listen.
