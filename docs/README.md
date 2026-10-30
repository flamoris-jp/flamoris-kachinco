# Design index

- [Linux CLI and LIME host](linux-cli.md): CPU rendering/export, resident setup and deployment boundaries.
- [Linux CLI ADR](decisions/0024-linux-cli-and-resident-host.md): shared authority, captions, save and MCP job contracts.
- [Linux VAAPI ADR](decisions/0025-linux-vaapi-encoding.md): required GPU encoding, fallback and independent Hub registration.

- [Production architecture](production-architecture.md): implemented decisions and boundaries.
- [WPF ownership boundaries](ui-ownership.md): shell/partial audit and homes for new editor behavior.
- [Roadmap](roadmap.md): active phased delivery order.
- [Editing API](api-contract.md): headless commands/queries and live MCP mapping.
- [Logging integration](logging.md): configuration, categories, privacy and failure isolation.
- [Project format v1](project-format-v1.md): frozen input `.fkproj` contract.
- [Project plan](project-plan.md): original long-term Clapper/Recipe vision.
- [Windows acceptance](../staging/windows-foundation.md): manual shell checks.

- [Native runtime boundary ADR](decisions/0007-native-runtime-boundary.md): ownership, ABI and portable deployment.
- [Native media ADR](decisions/0008-native-media-runtime.md): codec processes, decoded buffers and caches.
- [Native evaluation/playback ADR](decisions/0009-native-evaluation-playback.md): shared native runtime authority and pause correctness.
- [Media import formats ADR](decisions/0006-media-import-formats.md): MP4/MP3/M4A, probe validation and persisted-kind compatibility.
- [Rendering ADR](decisions/0002-production-rendering.md): shared compositor/export/playback.
- [Live MCP migration ADR](decisions/0005-mcp-core-migration.md): historical Core 1.1.0, typed host, local bridge, permissions and compatibility.
- [Original Live MCP ADR](decisions/0003-live-mcp.md): historical scoped attachment design.
- [Authoring v2 ADR](decisions/0004-authoring-v2.md): migration, Clappers, Recipes and provenance.
- [Volume automation/monitoring ADR](decisions/0014-volume-automation-monitoring.md): editable native curves, conditional v3, sample-accurate shared audio and editor-only monitoring.
- [Inspector controls](issue-29-inspector.md): grouped image-editor-style numeric/slider editing and history semantics.
- [Production Windows workflow](../staging/windows-production.md): prerequisites and acceptance.
- [Editor UX reference audit and coordinate contract](issue-7-editor-ux.md): Issue #7, real NLE references and Cutwork family grammar.
- [Windows hands-on checklist](../staging/windows-issue7.md): automated evidence and remaining physical acceptance.

- [Interactive preview contract](issue-9-interactive-preview.md) and [Windows hands-on](../staging/windows-issue9.md): Issue #9 current preview engine and remaining perceptual checks.

## Historical review and measurement evidence

- [Review index](reviews/README.md): preserved Issue #7/#9/#13 self-reviews and performance evidence. These records do not supersede current contracts.

- [Native editor/project codec ADR](decisions/0010-native-editing-authority.md): shared UI/MCP native editing authority.
- [Final cutover ADR](decisions/0011-native-cutover-cleanup.md): canonical time, retained adapters and deployment.
- [Timeline ripple reorder ADR](decisions/0012-timeline-ripple-reorder.md): same-track insertion, gap protection, native preview and shared history.
- [Preview production/presentation ADR](decisions/0013-preview-production-presentation.md): bounded worker preparation, native presentation decisions and pipe-read cancellation.
- [Preview performance measurements](reviews/issue-53-preview-performance.md) and [Windows acceptance](../staging/windows-issue53.md): comparable benchmark and outstanding physical A/V checks.
- [Native migration final audit](reviews/issue-36-native-audit.md): authority mapping, verification and remaining physical acceptance.
- [Post-migration Windows acceptance](../staging/windows-native-phase2.md): final bundle and physical A/V workflow.


- [Still images, visual automation and Effect Library](decisions/0015-still-images-and-effect-library.md): conditional v4, native time/value primitives and root-bounded reuse.
- [D3D11 preview backend](decisions/0016-d3d11-preview.md): bounded native GPU composition, hardware decode download and explicit CPU fallback.
- [GPU physical acceptance](../staging/windows-issue67.md): same-workload comparison driver and outstanding Mango 6 GB measurements.
- [GPU preview review](reviews/issue-67-gpu-preview-review.md): authority, resource/recovery audit and review fixes.
- [Delivery progress](../PROGRESS.md): Issues #62–#64 and #67, evidence and remaining acceptance.

- [ADR 0017: MCP preview frame](decisions/0017-mcp-preview-frame.md)
- [Issue 72 Windows/Hub acceptance](../staging/windows-issue72.md)

- [GPU MP4 export ADR](decisions/0020-gpu-export.md) and [Windows comparison/acceptance](../staging/windows-issue82.md): independent D3D11 composition and NVENC with CPU recovery.

- [Completed MP4 first-frame replacement](mp4-title-replacement.md), [ADR 0023](decisions/0023-exported-mp4-first-frame.md), [Windows acceptance](../staging/windows-issue94.md).

## KineticTypo companion

- [Glyph import and Linux headless Kinetai](kinetai-linux-and-glyph.md),
  [ADR 0030](decisions/0030-glyph-and-linux-kinetai.md): embedded vector geometry,
  native Pango/Cairo drawing and existing native motion/history authority.

- [User guide](kinetai.md), [runtime contracts](decisions/0028-kinetai-first-runtime.md), [original design](decisions/0027-kinetai-native-text-editor.md), [Windows acceptance](../staging/windows-kinetai.md).

- [Programmable kinetic animation](decisions/0029-kinetai-programmable-animation.md): Python expressions, native evaluation, source editor and MCP compilation.
