# Production roadmap

This is the active implementation order. `project-plan.md` preserves the long-term
Clapper → Recipe → Clip vision. Issue #1 is the foundation scope authority.

| Phase | Deliverable | Completion evidence |
| --- | --- | --- |
| 0 — Foundation (Issue #1) | C# solution, WPF shell, immutable domain, deterministic time, shared editing/query/history, JSON, evaluation/compositor/FFmpeg boundaries | Headless MOV/WAV registration → placement → edit → save/reopen → equal timeline; Windows build |
| 1 — Media and timeline authoring | Actual MOV/WAV probe, relink, inspector, interactive move/trim/split, media bin | Real files edit and reopen; missing-media diagnostics |
| 2 — Shared preview/playback | Decoder, audio clock adapter, seek, shared RGBA compositor, Normal/Screen | Screen overlay plus WAV synchronization; image/audio parity fixtures |
| 3 — Subtitle/SRT workflow | Caption editing, SRT import/export, styled caption renderer | Caption round trip and shared preview/export placement |
| 4 — FFmpeg production export | Snapshot export service, renderer/audio handoff, H.264/AAC MP4, progress/cancel | Landscape and portrait MOV+WAV+caption export; ffprobe duration/frame checks |
| 5 — MCP editing | Server adapter over existing commands/queries, job status | Client queries, batches edits, undoes and exports the same visible project |
| 6 — Clappers | Named ranges and pixel-space point/rectangle, commands, UI, persistence | `A-1` resolves after reopen and targets stable track/clip IDs |
| 7 — Restricted Recipe proof | Separate restricted worker, typed render IR, deterministic inputs/seeds | Repeated text/particle render is reproducible; limits tested |
| 8 — Recipe → Clip | Provenance, regeneration with explicit replacement policy | “A-1にグエー” creates an ordinary clip and undoes as one action |
| 9 — Expansion/hardening | Needed primitives, Kinetai/AudioAnalyzer adapters, packaging/performance | Real FLAMORIS production scenarios justify each addition |

Phase 0 does not complete any decoding, playback, production export or live MCP.
Each later phase begins with its acceptance criteria and contract changes. Preserve
the shared authority; add performance work only after profiling an actual workflow.
