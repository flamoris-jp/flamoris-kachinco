# Issue #94 first-frame replacement validation

## Scope and review

The new operation edits an exported file; it has no session, native editor commands,
project persistence or timeline evaluator. UI reuses a title selection's resolved
asset or an independently selected image. Only frame zero is replaced, never inserted.

Review checks: next boundary must be an actual closed IDR (NAL type 5), not an open-GOP
keyframe flag; packet partitions cannot straddle its presentation boundary. Preserve
video timescale and B-frame reorder depth, use concat SPS/PPS conversion, remux original
AAC with unchanged packet hashes/PTS/DTS/durations. Validate the opening/splice decode
and entire packet presentation grid before publishing. Source is held read-only and
checked again before publication; destination must not already exist. Generated private
names avoid concat-list quoting of user paths. Cancel joins processes before cleanup.

Opening re-encoding is lossy (x264 fast/CRF 18), and a file without a later suitable IDR
requires full-video encoding, explicitly disclosed before saving. No timeline-render or
encoder fallback is hidden. Unsupported formats fail without an output.

## Automated results (2026-10-18)

- `Mp4TitleReplacementTests`: **10/10 pass** with real FFmpeg. Includes 30/29.97fps,
  B frames 0/1/2/3, AAC/no audio, 7-frame full-opening case, **5101 → 5101 frames**,
  strict full decode, copied decoded-picture hashes, frame-zero/one pixel checks,
  presentation timestamps, cancellation cleanup, existing output/source preservation,
  changed-input rejection, unsupported codec, VFR/rotation rejection, transparent image
  flattening and aspect-fit padding, and open-GOP keys rejected as copy boundaries.
- Native contracts: **5/5 pass**. WPF cross-target Release build: **0 warnings/errors**.
- Existing full managed regression run before the final two added cases: 355 pass,
  1 platform skip, 2 environmental failures in `McpEnvelopeTests` because this workspace
  denies Unix socket creation (`SocketException: Permission denied`). These are existing
  named-pipe transport tests, outside this file operation. CI runs them without that
  workspace restriction; this record does not count them as passing locally.
- Windows CI now also selects the new real-codec tests, since Windows path/process and
  MP4 decoder behavior are relevant. Physical Mango/Media Player/NVENC input acceptance
  remains the separate [staging checklist](../../staging/windows-issue94.md).

## Synthetic performance and quality measurement

Linux workspace, .NET 10.0.402, FFmpeg 6.1.1, CPU libx264. Single illustrative run, not
Mango hardware acceptance, not an extrapolation to the 5101-frame production video.
Synthetic `testsrc2`, 1920×1080, 30fps, 180 frames / 6 seconds, H.264 fast/CRF18,
closed GOP 60, stereo 48kHz AAC; solid red title PNG.

The comparison uses the **actual `SnapshotExportService`** with SharedFrameRenderer,
SharedAudioRenderer and FfmpegEncodingBackend CPU policy: source MP4 on a video track,
decoded original audio on a WAV track, and title image over frame zero. New finishing
path operates on that source MP4 and same title. Source fixture generation and user
interaction are excluded from both times; new-path preparation is shown separately.

| Path / phase | Measured time |
| --- | ---: |
| Normal Kachinco timeline export, 180 rendered frames | 62.392 s |
| New MP4 inspection + fitted title preparation | 0.290 s |
| New replacement including validation, 60 encoded + 120 copied frames | 1.938 s |
| New path including preparation | 2.236 s |

For this workload, total preparation + replacement took about 1/28 of the normal
render time. It is not a universal speed guarantee; GOP length, source size, encoder,
resolution, file I/O and validation affect the outcome.

Quality: decoded frames **60–179 are exactly equal** to the source (framemd5); video
frame count stays 180; original audio packets/timestamps match (runtime validation).
For re-encoded source frames 1–59 (excluding intentionally replaced frame zero), FFmpeg
SSIM reports **All: 0.998615**. This is additional compression, not lossless equivalence.
Source and replacement decode strictly without errors. Physical playback/subjective
quality are not claimed by this synthetic check.
