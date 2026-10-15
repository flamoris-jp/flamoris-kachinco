# Clip speed and MCP downloads

## Speed

Select a clip, choose the Inspector's **Playback speed** (1, 2, 4, 8). Alternatively
send an ordinary guarded `edit_batch` command:

```json
{"commands":[{"type":"SetClipSpeed","sequenceId":"<uuid>","clipId":"<uuid>","playbackRate":8}]}
```

The same source interval becomes shorter; other tracks and later clips stay put.
Returning to 1x can require moving later clips or extending the sequence in the
same batch. These explicit edits remain one Undo. Audio becomes faster/higher in
pitch. Change a separately imported video/audio pair explicitly in one batch to
keep it together; embedded video audio is never silently added. Slow motion,
reverse and pitch preservation are not implemented. Keys follow source time,
rounded toward zero; collisions/overflow reject the edit. Split/trim/duplicate
preserve the native rate. Sequence duration is not shortened automatically.

v1–v4 input defaults to 1x. A speed project writes v5 with required playbackRate,
sourceDurationTicks and v4 automation arrays. Source duration is exact; timeline
length rounds up by less than one tick. Old applications reject v5 explicitly.

## Download

1. Read `mcp.context` / `get_project`; resolve sequence and clip UUIDs.
2. Call `clip_download_begin` with `sequenceId`, `clipId`, optionally clip-relative
   `startTicks` and `durationTicks` (decimal strings). Quality is `Full`, `Half`
   (default) or `Quarter`. A single request covers at most 30 seconds.
3. Poll `clip_download_status` with the returned `downloadId` until `state:ready`.
   `failed` or `cancelled` is terminal. Rendering runs on an immutable snapshot in
   the background; starting a job does not move playback or create editor history.
4. Read `clip_download_read` using decimal-string `offset` and optional `count`
   (1–131072). Decode `dataBase64`, verify `chunkSha256`, append bytes at the
   returned offset and continue to `eof`. Re-reading an offset returns the same
   bytes while the snapshot is valid.
5. Verify assembled length and SHA256 against `metadata.sizeBytes` / `sha256`,
   then save using `metadata.fileName` / MIME. Call `clip_download_cancel` to
   release the temporary output. Releasing a ready job permits another download.

All tools accept the ordinary `{input,guard}` MCP envelope. Pass current runtime,
document and expected revision for predictable stale-request errors. Both ReadOnly
and Edit may download; Edit is required to change speed or import assets. Any
intervening edit, Save As path change, document switch or source stamp change
invalidates the download. Cancellation/revocation/disconnect remove temporary
bytes; idle jobs expire after ten minutes. One job per attachment and one active
render per window; render deadline is two minutes. No arbitrary path/URL input or
host path is returned.

WAV is PCM16, 48 kHz stereo (maximum 64 MiB including header). It includes trim,
speed, gain and envelope. Video/image clips become silent H.264 MP4; the original
sequence canvas is flattened onto opaque black, at Full/Half/Quarter resolution
and original rational FPS. Transform/opacity/automation are shared with preview.
Other tracks/captions are excluded. Disabled clips/tracks and muted audio produce
black/silence. Explicit `inspectDisabled:true` overrides enable and mute only on
the derived render snapshot; zero gain/opacity and authored curves remain active.
Metadata distinguishes requested tick duration from encoded sample/frame duration;
range starts align audio to the first sequence sample at/after the requested tick
(with less than one sample of start quantization).

A client may attach the resulting WAV/MP4 to a supported media input or feed a
local analysis tool. A text-only MCP consumer must decode and assemble the bytes
first. Download success is independent of whether the selected AI model supports
audio/video input; no automatic listening or external upload is claimed.

## Hub configuration and acceptance

`test/Kachinco.Tests/Fixtures/clip-download-tools.json` pins the actual MCP Core
wire descriptor for all four download tools and the updated `edit_batch`.
The matching Hub example is `_kachinco-clips.example.yaml`. Merge the new tools
and replace only `edit_batch` in the existing deployment catalog, preserving
uploads, preview, other editing/query tools and connection settings. Copying this
partial example over the whole catalog would remove existing tools.

Upgrade the desktop before installing that catalog. Reconnect and verify catalog
parity. Live Mango/Hub acceptance still requires retrieving a real edited clip
through the external tunnel, assembling its bytes and checking preview/audio.
Automated producer tests and the Hub HTTP/WebSocket forwarding fixture protect
separate boundaries; neither is a claim of physical acceptance.
