# ADR 0023: Replace the first frame of an exported MP4 (Issue #94)

Accepted scope: replace, never insert, exactly the first displayed video frame.
This is a file finishing operation in Infrastructure, independent of project/session,
Undo/Redo and timeline evaluation. Existing title-image selection can supply the image;
it does not imply a reserved frame in an arbitrary MP4. No schema or MCP change.

- UI: Title image → Replace first frame in completed MP4. Pick MP4 and image (or the
  selected project title), inspect the fitted preview and processing range, save a
  separate MP4. Resolution, rational FPS, video frame count, presentation timestamps
  and original audio are preserved. Images fit centrally with black padding.
- MVP accepts self-contained H.264/AVC, progressive 8-bit yuv420p, square pixels,
  zero-start CFR MP4, one video and optional one AAC stream. Rotation, other tracks,
  VFR, non-grid timestamps and unsupported bitstream layouts fail explicitly.
- Inspect compressed packet timing. Find the first subsequent closed IDR boundary
  (verify length-prefixed H.264 NAL type 5, not just the keyframe flag). Re-encode
  frames before that boundary with libx264 fast/CRF 18, replacing frame zero only.
  Copy subsequent video and the entire original audio; never evaluate a timeline.
  Preserve the video timescale and decoder reorder delay across the splice.
  FFmpeg concat auto-conversion carries SPS/PPS at the codec transition.
- With no subsequent suitable IDR, the entire video is the opening region. Explain
  this before saving; never claim a bulk-copy speedup. The opening region has an
  additional lossy generation. Later copied pictures and AAC are not re-encoded.
- Verify packet timestamps/frame count and audio packet hashes/times after remux,
  and decode the opening/splice with strict error handling before publication.
  Unsupported joins fail; there is no silent full-video fallback.
- Create a private temporary directory beside the destination, cancel/join child
  processes before cleanup, and publish only after validation. Destination must be
  new and distinct from all inputs; original media is never overwritten. Hold a
  read-only source handle during finishing, and reject changed preparation inputs.
- FFmpeg/ffprobe run without a shell, with file-only protocol/container allowlists,
  bounded output and deadlines. Private generated concat names avoid path injection.

Validation includes real H.264 B-frame/IDR joins, rational FPS, AAC packet identity,
frame timing/count, decoded copied-frame equality, unsupported input and cancellation.
Physical Windows/Media Player acceptance and measured production speed remain separate.
Reference: https://ffmpeg.org/ffmpeg-formats.html#concat

## Production-export correction (2026-10-18)

The reported NVENC export had a complete 30fps packet grid but its MP4 edit-list
summary ended five 1/15360-second ticks (0.326 ms) before the nominal frame end.
The original exact `duration_ts == frames * step` check incorrectly rejected it.
Allow only a one-movie-millisecond rounding budget, rounded up to a video tick,
for the summary. Packet count, PTS grid and every packet duration remain exact;
VFR, missing/duplicate frames and larger summary discrepancies still fail.
Header-only duration may normalize within that rounding budget on remux; no
frame is added/removed and audio packet timing is unchanged.

`has_b_frames` is not the initial decode timestamp offset. The reported NVENC
input started DTS at -3 frames while the x264 opening started at -2. Inspect the
encoded opening and, only when needed, copy/remux it with
`setts=pts=PTS:dts=DTS+(sourceFirstDts-openingFirstDts)` before concatenation.
Keep the input/output video timescale, all PTS and the copied body's DTS exactly.
Never use a DTS-only expression without `pts=PTS` (the filter's default would
otherwise alter PTS). No additional lossy encoding is involved in this alignment.

Kachinco's raw-RGBA CPU encoder may omit pixel-aspect metadata. Treat that absence
like the already supported unspecified SAR; explicit non-square SAR stays rejected.
Regress with rounded-edit/extra-DTS synthetic media and an actual Kachinco CPU export.
No user artwork or production media is committed as a test fixture.
Reference: https://ffmpeg.org/ffmpeg-bitstream-filters.html#setts
