# Issue #9 Windows hands-on and measurements

This checklist requires Akino's physical Windows session. Automated source pixels,
geometry, process counts and sample-clock contracts do not establish natural scrub
feel, audible sound, A/V synchronization or visual acceptance.

## Setup and measurements

Use the CI portable ZIP or build `Kachinco.slnx -c Release` with .NET 10 on Windows.
FFmpeg/ffprobe must be on PATH. Run:

```powershell
dotnet run --project test/Kachinco.WindowsSmoke/Kachinco.WindowsSmoke.csproj -c Release
```

This generates three-second 1080p/30 MOV + 48 kHz WAV fixtures. The performance part
records cold/cached scrub, 60 forward frames plus mixed audio, decoder starts, host
CPU time, sampled combined host/FFmpeg working set, cache bytes and native playback
when a device exists. Output: `artifacts/interactive-preview-metrics.json`, uploaded
as `interactive-preview-evidence` by Windows CI. Decode throughput is explicitly
separate from device-paced playback. No device means unavailable audio measurements;
it must not be converted into a passed audio/perceptual test. Timing values describe
that runner and run, not a product-wide real-time guarantee.

For physical measurement also record CPU model/RAM, GPU, source codec/GOP, display
scaling, output device/latency and FFmpeg version. Measure Full, 1/2 and 1/4. Compare
8-second and 219.6-second sequences with the same visible content at the same start
position. Record Play → first visible frame, Play → first audible sound, sustained
playback and drift for the entire test interval. Artificially heavy layered clips
should show video-skip counts; audio starvation should show Buffering and resume
without a jumping fake clock. Use Task Manager for host **and** FFmpeg CPU/memory.

## Human acceptance (not yet signed off)

- [ ] Import MOV and WAV from a fresh launch; create landscape sequence.
- [ ] Place on V1/A1 at known ruler times, including after zoom/scroll.
- [ ] Click/drag ruler and the red playhead: viewer shows that time; clips do not move.
- [ ] Scrub rapidly in both directions; no late old frame appears after release.
- [ ] Play from zero; buffering and first picture/sound feel responsive.
- [ ] Play from the middle of ~219 seconds; no preparation from the beginning/full duration.
- [ ] Listen to WAV and compare visible timing with known sync cues. MOV embedded audio
      remains excluded; import WAV separately. Verify silence before/after clips.
- [ ] Pause/resume, seek, stop; pause holds position, scrub pauses silently, stop shows zero.
- [ ] Edit during preparation/playback. Nearby changes restart locally, unrelated video
      stays cached after audio changes. Undo/Redo must update affected output.
- [ ] Full / 1/2 / 1/4: identical timing/composition intent and readable status; lower
      resolution may alter sampled edges/text pixels without changing persistent state.
- [ ] MOV thumbnails identify content, including when its left edge is offscreen.
      Filename and selection/trim/move affordances remain legible.
- [ ] Drop second WAV on `+ A` below A1: A2 appears there and placement is one Undo/Redo.
      `+ V` behaves equivalently. Existing track drop uses exactly that track.
- [ ] Same-track overlapping drag/drop/move/trim is rejected with guidance. Existing
      intentionally authored overlap shows `⚠`: video composites in evaluator order;
      audio contributions sum before the final clamp. Move it to free space to separate.
- [ ] Save/open: stable IDs, tracks, source-in, placement and final output are unchanged.
- [ ] Compare Full preview against exported MP4 for transforms, opacity, Screen/Normal,
      captions and generated Recipe MOV. Allow codec compression differences only.
- [ ] Missing/relinked media and failed decode clear stale viewer content and show a reason.
- [ ] Record density/readability/discoverability and real editing feel at 100/150/200% DPI.

Screenshots are optional evidence of layout only. No before/after or visual acceptance
is claimed solely because Windows startup or semantic geometry assertions pass.
