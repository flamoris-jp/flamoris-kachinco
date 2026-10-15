# Issue #72 — desktop update and external visual acceptance

Automated Windows smoke covers a 1080×1920/30 fps still-image sequence with overlap,
screen blend, opacity, X/scale keyframes and captions at 0/2.5/4.9/5 seconds. It compares
full viewer/capture RGBA before encoding and decodes PNG/JPEG dimensions. Packaged
smoke uses the published editor and bridge with an external official SDK client;
query response must contain an ImageContentBlock and leave get_project unchanged.
This is automated evidence, not physical production acceptance.

## Update

1. Save the current project and close Kachinco. Download the accepted CI/package
   ZIP linked from the PR (or the subsequent Windows package release). Unpack into
   a new version folder. Use the entire bundle including `mcp/`; replacing only
   Kachinco.App.exe leaves incompatible old Core/bridge assemblies.
2. Start the new Kachinco.App.exe and reopen the project. Existing per-user MCP
   connection settings stay in LocalAppData/FLAMORIS/Kachinco; no token reissue or
   server reset is required. Keep FFmpeg available as for ordinary preview.
3. Connect MCP. In settings, copy Hub registration configuration. Update only the
   existing Kachinco upstream catalog with this generated catalog, preserving the
   configured connector token and endpoint. It adds `preview_frame` with the exact
   Core `{input,guard?}` schema. Reload/restart Hub through its ordinary deployment
   mechanism, reconnect the desktop, and confirm no catalog mismatch.
4. Refresh the ChatGPT plugin tool catalog / start a new conversation if needed.
   Confirm `kachinco.preview_frame` appears. Local/tunnel-only clients use
   `preview_frame` through the matching packaged bridge.

## Live acceptance — pending until the new desktop is installed

- Use the portrait manga project, 1080×1920, 30 fps. Record live mcp.context identity
  and get_project revision. Request JPEG width 540 quality 85 at ticks `0`,
  `88200000` (2.5 s), `172872000` (4.9 s) and a known clip boundary. Repeat one PNG
  (omit quality). Actual valid tick range depends on the sequence duration.
- Use the authenticated external route actually used by ChatGPT: outbound desktop
  WSS -> MCP Hub -> configured OpenAI tunnel -> external client. Also test direct
  bridge/tunnel if that connection mode is used. Verify an actual image is visible
  to the AI, correct MIME/dimensions and metadata match the current document.
- Compare with the physical viewer at Full quality at the same resolved tick.
  Image/clip order, transform, opacity and caption should agree; JPEG and final
  downsampling can differ in edge/color samples. Exclude selection overlays.
- Capture while playing: playback continues; selected clip/playhead are not moved
  by the query. Compare project revision and Undo/Redo before/after; no history entry.
- Try endpoint/exclusive duration, unknown sequence, missing media, oversized PNG,
  concurrent captures and editing during decode. Expect structured errors, no image
  on stale results, and normal editing/reconnect afterward.
- Record bundle BUILD-INFO commit, Hub version/catalog, identity/revision,
  requested/resolved ticks, dimensions/MIME, actual client image visibility and
  viewer parity outcome. Do not record secrets. Keep Issue #72 open until this
  deployed acceptance is recorded; do not infer it from unit/CI success.
