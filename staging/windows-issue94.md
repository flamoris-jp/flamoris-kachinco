# Issue #94 Windows / Media Player acceptance

Automated real-codec tests: Mp4TitleReplacementTests. Physical Windows UI and hardware
playback are separate; do not infer completion from headless FFmpeg checks.

- [ ] Build/package current branch; open Title image → Replace first frame in completed MP4.
- [ ] Select a real CPU export and a real NVENC export. Reuse a selected title and choose
      a different image; inspect the actual fitted preview. Repeat without an open project.
- [ ] Save under new names; verify original bytes unchanged and project dirty/history unchanged.
- [ ] Check frame zero is title and frame one is original frame one. Same resolution, FPS,
      5101 → 5101 frames, video/audio duration and start. Play across the reported opening
      boundary in Windows Media Player; seek immediately before/after it and to the end.
- [ ] Confirm no freeze, black, duplicate/drop, corruption or audio offset at the boundary.
- [ ] Note elapsed times: normal timeline export vs first-frame replacement, with resolution,
      rational FPS, source encoder, source size, reported re-encoded/copied counts and hardware.
- [ ] Compare opening quality; decoded copied pictures and AAC must match the source.
- [ ] Cancel while encoding/copying/verifying; no destination/incomplete file or work dir remains.
- [ ] Existing destination/source selection is rejected; changed inputs and unsupported formats
      explain failure. No automatic bulk transcode is hidden from the preview.

Synthetic benchmark evidence belongs in docs/reviews/issue-94-first-frame.md. It is not
physical acceptance or a guarantee of a speedup on every source/hardware.
