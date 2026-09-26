# ADR 0006: Broaden probe-backed production media import

Status: proposed for Issue #15.

## Decision

Accept MOV/MP4 as video and WAV/MP3/M4A as audio through the existing
`IMediaProbe` -> ordinary `RegisterMedia` / `RelinkMedia` commands. A shared pure
Core extension policy bounds supported source references and supplies UI hints.
Infrastructure checks actual ffprobe container and selected stream metadata;
extensions never prove content, duration or codec support.

The persisted `MediaKind.Mov` and `MediaKind.Wav` names remain compatibility tokens
for the video and audio editing paths. MP4 uses Mov; MP3/M4A use Wav. No new enum,
asset model, timeline path or project schema is introduced. Current schema v2 and
v1 input structure remain unchanged. Old MOV/WAV projects round-trip unchanged.
New paths are an additive validation expansion: old builds reject MP4/MP3/M4A
references as unsupported rather than silently dropping them. Do not reopen a
new-format project in an older build without a backup. UI displays Video/Audio
and the source extension instead of mislabelling MP4 as MOV or MP3 as WAV.

Probe must find the appropriate first stream (`0:v:0` or `0:a:0`) and a compatible
actual container (MOV/MP4/M4A share FFmpeg's ISO media demuxer family). Unknown or
missing selected codec/essential dimensions or audio metadata is invalid. Use
selected-stream duration, falling back to container duration, never to a later
stream. Cover art in audio files does not turn them into video assets. MP4/MOV
embedded audio remains metadata only and is not added to the audio timeline.
Relink may cross containers within a media kind, retaining every stable asset/clip
ID, source range and placement; incompatible kinds or too-short files fail.

The file picker and single-file drop into the media bin share the same probe-backed
import handler. Media-bin-to-timeline drops retain the existing stable-ID path.
No external-file timeline insertion or batch-import semantics are added.

## Runtime and verification

The portable ZIP includes .NET and the MCP bridge; it deliberately does **not**
bundle or download FFmpeg/ffprobe. Preserve the documented external PATH runtime.
Use real generated MOV/WAV/MP4/MP3/M4A fixtures, probe and decode through that stack,
round-trip/Undo/Redo and cross-container relink tests, and Windows shell/portable
acceptance. Codec availability depends on the installed FFmpeg build; unsupported
or invalid inputs produce probe/decode diagnostics, not conversion or a new backend.

No proxies, transcoding, codec-wide guarantees, second timebase, new MCP file grants
or schema migration. Project / EditorSession / Command / Query remain authority.
