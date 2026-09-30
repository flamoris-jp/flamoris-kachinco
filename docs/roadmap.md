# Production roadmap

`project-plan.md` preserves the long-term vision. `main` is the reviewed baseline;
Issues #5–#9 implemented the production slice; #31–#36 complete the native engine
cutover. Physical Windows acceptance remains separate by owner direction.

| Phase | Current implementation | Evidence / remaining acceptance |
| --- | --- | --- |
| 0 — Foundation | Existing | Shared domain, time, commands, persistence and evaluator |
| 1 — Media/timeline | Existing plus hands-on fixes | Full-length drops, atomic extension, pane controls; MP4/MP3/M4A probe-backed import (ADR 0006); Windows pointer/DPI check pending |
| 2 — Shared preview/playback | Interactive scrub / buffered playback | Shared renderer/mixer, bounded forward decoding and sample clock; see Issue #9 evidence and physical A/V checklist |
| 3 — Subtitle/SRT | Implemented fixed-style slice | SRT round trip, shared Windows caption rasterization; visual acceptance pending |
| 4 — FFmpeg export | Implemented | Real MOV/WAV landscape/portrait H.264/AAC/frame-count fixtures; cancel/cleanup contracts |
| 5 — MCP editing | Implemented local stdio bridge | Same visible session and history, revision checks, export/Recipe jobs; external-client hands-on pending |
| 6 — Clappers | Implemented point/rectangle slice | Unique named ranges, UI/monitor/ruler, commands, v2 migration and reference tests |
| 7 — Restricted Recipe proof | Implemented literal-call subset | Separate bounded Python AST compiler; text/particles, seed, worker denial and repeatable Windows pixels |
| 8 — Recipe → Clip | Implemented | Alpha MOV + provenance, atomic insertion and explicit regeneration preserving clip placement |
| 9 — Hardening/expansion | Packaging implemented; needs-driven work remains | Self-contained Windows ZIP; profiling and Kinetai/AudioAnalyzer integrations await real production need |

No phase status here implies completed human visual acceptance. Use
[`staging/windows-production.md`](../staging/windows-production.md) for that checklist.
The first Recipe proof supports at most 10 seconds. Final export decodes independently per requested frame/block. Interactive Play amortizes
codec startup with bounded forward streams; no universal real-time performance claim is made.

## Native migration milestone

#32 ABI, #33 media/cache, #34 evaluation/playback and #35 editor/project codec
cutovers are merged. #36 removes unused managed codecs, delegates canonical time
and completes packaging/architecture audit. One native engine is required; there
is no managed production fallback. The managed shell retains WPF, async desktop/
codec/filesystem integration, MCP transport and the restricted Recipe worker.

The owner requested physical Windows A/V checks after migration completion.
Use the final #36 portable bundle and [native checklist](../staging/windows-native-phase2.md).
#28 remains open pending clip-boundary pause evidence; #29 Inspector and #30
automation UI/domain/persistence are independent follow-up features. The native
automation evaluation seam does not deliver editable/persisted animation curves.
