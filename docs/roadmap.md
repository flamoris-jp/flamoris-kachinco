# Production roadmap

`project-plan.md` preserves the long-term vision. `main` is the reviewed baseline;
Issue #5 / PR #6 implements the following production slice for review.

| Phase | Status in PR #6 | Evidence / remaining acceptance |
| --- | --- | --- |
| 0 — Foundation | Existing | Shared domain, time, commands, persistence and evaluator |
| 1 — Media/timeline | Existing plus hands-on fixes | Full-length drops, atomic extension, pane controls; Windows pointer/DPI check pending |
| 2 — Shared preview/playback | Implemented render-ahead slice | Same renderer/mixer as export; real-time performance and physical A/V judgment unclaimed |
| 3 — Subtitle/SRT | Implemented fixed-style slice | SRT round trip, shared Windows caption rasterization; visual acceptance pending |
| 4 — FFmpeg export | Implemented | Real MOV/WAV landscape/portrait H.264/AAC/frame-count fixtures; cancel/cleanup contracts |
| 5 — MCP editing | Implemented local stdio bridge | Same visible session and history, revision checks, export/Recipe jobs; external-client hands-on pending |
| 6 — Clappers | Implemented point/rectangle slice | Unique named ranges, UI/monitor/ruler, commands, v2 migration and reference tests |
| 7 — Restricted Recipe proof | Implemented literal-call subset | Separate bounded Python AST compiler; text/particles, seed, worker denial and repeatable Windows pixels |
| 8 — Recipe → Clip | Implemented | Alpha MOV + provenance, atomic insertion and explicit regeneration preserving clip placement |
| 9 — Hardening/expansion | Packaging implemented; needs-driven work remains | Self-contained Windows ZIP; profiling and Kinetai/AudioAnalyzer integrations await real production need |

No phase status here implies completed human visual acceptance. Use
[`staging/windows-production.md`](../staging/windows-production.md) for that checklist.
The first Recipe proof supports at most 10 seconds. Rendering currently decodes
independently per requested frame/block; no real-time performance claim is made.
