# AGENTS.md

This file defines repository-wide rules for AI agents and automated development tools operating in `flamoris-jp/flamoris-kachinco`.

The discipline is adapted from `flamoris-jp/flamoris-2D`: explicit Product/Test/History boundaries, small deterministic changes, Command/Transaction mutations, milestone-based CI, and MCP as an adapter rather than a second hidden editor. Kachinco-specific rules below take precedence for this repository.

## 1. Repository purpose and authority

FLAMORIS Kachinco is an AI-native timeline editor and programmable compositor for reproducible video creation.

Its central workflow is:

**Clapper -> Recipe -> Clip**

- **Clapper**: a named, human-authored reference to time and optional canvas geometry/context.
- **Recipe**: a reproducible restricted-Python program describing a generated edit/effect.
- **Clip**: the rendered output placed on the ordinary timeline.

`main` is the reviewed current baseline. Chat output, local experiments, generated code snippets, old issue text, and prototype artifacts are not authority unless explicitly adopted through repository changes.

## 2. Absolute boundary rules

1. Product runtime, Test/Staging, History/Experiments, and current Docs responsibilities remain explicit and dependency-separated.
2. Product runtime must never depend on tests, staging fixtures, private acceptance media, or history-only assets.
3. MCP, UI, scripts, and AI mutate persistent project state through the same Command/Transaction layer. No parallel hidden AI editing path.
4. Persistent identity uses stable IDs. Human-readable names such as `A-1` are lookup labels, not primary identity.
5. Timeline values use one canonical deterministic time model. Do not create separate UI, MCP, subtitle, audio, or renderer clocks.
6. Canvas geometry is stored in documented domain coordinates, never viewport pixels as persistent authority.
7. Preview and final render share the same evaluation semantics. Do not implement final output as viewport/screen capture.
8. Python Recipes never receive unrestricted OS authority. No arbitrary filesystem, subprocess, shell, environment, or network access.
9. Random behavior is deterministic when reproducibility matters. Require explicit/recorded seeds.
10. Generated output retains provenance sufficient for regeneration.
11. AI-generated source code is not trusted input. Validate before execution/render.
12. Do not duplicate media models, timeline models, render evaluators, subtitle models, or command paths merely for AI/MCP convenience.
13. When a requirement is ambiguous, document the uncertainty or propose an ADR. Do not silently invent authority.

## 3. Suggested repository boundaries

Until an accepted ADR changes them:

```text
product/      current editor/runtime implementation and deterministic local unit tests
test/         integration/system tests outside Product runtime
staging/      manual/visual acceptance setup and non-production fixtures
history/      superseded experiments/prototypes/migration notes
docs/         current design, ADRs, architecture reviews and project plan
```

If the implementation language/toolchain later makes a different physical layout more appropriate, preserve the same dependency direction even if folder names change.

## 4. Core domain invariants

### 4.1 Clapper

A Clapper is a persistent domain object, not a transient selection overlay.

It should have:

- stable ID
- human-readable name
- timeline range
- optional point/rectangle/polygon/path geometry
- optional target-track/layer preference
- optional source clip references
- optional notes/intent

Rules:

- names may be duplicated only if lookup behavior is explicit; prefer uniqueness within an appropriate scope;
- geometry must be defined in domain/project coordinates;
- changing/deleting referenced clips must not silently corrupt a Clapper;
- AI resolves a name to a stable ID before mutating dependent state.

### 4.2 Recipe

A Recipe is reproducible authoring state.

It must capture or reference:

- normalized source program or equivalent IR
- Kachinco API version
- input asset references
- Clapper references
- render/output settings
- deterministic seed(s) when randomness is used
- renderer/version metadata needed by the reproducibility contract

Do not save opaque AI-only state that cannot be inspected, replayed, or migrated.

### 4.3 Clip

A generated Clip is a normal timeline clip plus provenance.

Human editing operations such as move, trim, duplicate, mute, stack, and delete must remain ordinary timeline operations.

Recipe regeneration must be explicit about whether it replaces media in-place, creates a new revision, or inserts a new clip. Never silently destroy hand-edited timing.

## 5. Python / programmable rendering rules

Python is an authoring language, not the rendering engine.

Preferred flow:

```text
Recipe Python
    -> validation / restricted execution
    -> typed Render Commands or IR
    -> deterministic evaluator/renderer
    -> media output
```

Rules:

- expose a small explicit Kachinco API rather than arbitrary third-party imports;
- whitelist capabilities instead of blacklisting dangerous modules;
- enforce CPU/time/memory/output limits;
- deny arbitrary network access;
- deny arbitrary filesystem access; expose explicit asset handles and output targets;
- deny subprocess/shell/process-control access;
- deterministic APIs must not depend on wall-clock time;
- generated code should be previewable/validatable before full render;
- renderer behavior must be testable without running the full editor UI.

## 6. MCP / AI invariant

MCP is an adapter over Product queries/commands, not a second editor.

MCP should expose typed operations for capabilities such as:

- project/timeline/track/clip inspection
- playhead and current selection inspection
- Clapper create/read/update/delete/resolve
- media registration/import
- subtitle/caption operations
- Recipe validation and preview
- render/regenerate
- clip insert/replace/move/trim

Rules:

- query and mutation capabilities are separate;
- mutations use stable IDs and domain coordinates;
- compound AI actions use transactions;
- risky/high-impact operations should support dry-run/preview when useful;
- the editor remains usable without AI/cloud services;
- AI suggestions may be creative, but committed project mutations remain deterministic commands.

Principle: **AI writes intent/programs; deterministic commands commit project state.**

## 7. Timeline and media rules

- Keep one canonical timebase and rational FPS model.
- Audio, video, subtitle, generated media, and Clapper ranges must convert through shared time utilities.
- Do not let display frame numbers become the sole persistent time authority.
- Media identity is not a filesystem display path alone; use stable asset IDs with explicit source metadata.
- Re-linking/missing-media behavior must be deliberate and testable.
- Waveform/proxy/cache data is derived state and must not become project authority.
- Generated media output may be cached, but its Recipe/provenance is the durable source for regeneration when promised by the product contract.

## 8. Subtitle/caption rules

Subtitle import/export (including SRT) and styled timeline captions should share a canonical caption domain model where practical.

Do not make SRT text files the internal source of truth for editing.

Caption generation or alignment performed by AI must result in ordinary inspectable caption entries with deterministic time ranges.

## 9. Development order

Unless an accepted ADR changes it:

1. Confirm the current requirement and affected domain boundary.
2. Update design/ADR first when changing persistent data, time semantics, coordinate systems, renderer/Recipe contracts, sandbox capabilities, MCP contracts, or project-file compatibility.
3. Implement the smallest vertical Product change.
4. Add/run the smallest deterministic local tests for changed behavior.
5. Add integration/staging tests only when those environments are actually required.
6. Add CI only when there is a stable behavior/boundary worth protecting.
7. Review through a PR before merging meaningful capabilities to `main`.

Prefer a usable end-to-end slice over broad framework construction.

## 10. Change discipline

- Prefer small understandable changes over architecture pageantry.
- Do not add abstractions, state machines, services, validators, schemas, caches, or workflows unless a product requirement justifies them.
- Keep development-process metadata out of Product project files.
- UI features are incomplete until persistence, Undo/Redo, validation, time/coordinate semantics, and MCP/command implications are considered where applicable.
- Never silently discard Clappers, Recipes, captions, timeline edits, or provenance during migration/reload.
- A generated effect that looks correct once but cannot be reproduced does not satisfy the core Kachinco contract.

## 11. Testing policy

During active implementation:

- run the smallest checks relevant to changed code;
- prefer deterministic unit tests for time conversion, timeline edits, Clapper geometry, serialization, Recipe validation, render-command generation, seeded randomness, command transactions, Undo/Redo, subtitle timing, and migrations;
- avoid premature broad browser/GPU/system harnesses.

At milestones:

- define which stable risks must not regress;
- add the smallest regression suite for those risks;
- use integration tests for cross-module behavior;
- use staging/manual visual acceptance for actual codec/GPU/visual/export risks that deterministic unit tests cannot cover.

## 12. CI policy

- CI protects stable behavior; it does not orchestrate development.
- Prefer one check per distinct risk over overlapping gates.
- Keep expensive codec/GPU/visual/end-to-end checks manual or explicitly triggered until automation has clear value.
- Do not duplicate identical validation on PR and `main` without a distinct post-merge risk.

## 13. GitHub workflow

```text
main          reviewed current baseline
feature/*     implementation work
docs/*        design/research/ADR work
prototype/*   disposable or preserved experiments, not Product authority
integration/* temporary reconciliation work
```

For meaningful capabilities:

1. Issue or documented acceptance criteria.
2. Feature/docs branch.
3. Small purpose-driven commits.
4. Relevant local tests.
5. PR review.
6. Merge only when acceptance criteria and required checks pass.
7. Tag meaningful milestones when useful.

Never leave the latest working implementation only in chat, Downloads, or a local folder.

## 14. Initial design authority

At repository inception:

- `README.md` defines product intent and core concepts.
- `docs/project-plan.md` defines the initial phased implementation plan.
- accepted ADRs under `docs/decisions/` will become authority for durable architectural decisions.

As the project grows, add a `docs/README.md` index and explicit architecture documents rather than allowing design knowledge to fragment across issues and chat.

## 15. Implemented foundation authority (Issue #1)

- `docs/production-architecture.md` defines the implemented architecture; `docs/roadmap.md` is the active delivery order. The older project plan is the long-term vision.
- Use .NET 10, WPF only in `product/Kachinco.App`, and headless Core/Infrastructure. Physical boundaries follow section 3.
- Canonical time is 35,280,000 ticks/second. Use `TimelineTime` for all conversions, reduced rational FPS, and half-open ranges. Never copy 2D's timebase into this product.
- All editing goes through `EditorSession.Execute(EditBatch)`; `ReplaceProject` is explicit Open/New lifecycle, not an editing shortcut. Use immutable snapshots and explicit expected revisions for concurrent clients.
- Keep `ProjectFormatV1` DTOs separate from Core records. Required fields, decimal-string ticks, version dispatch and unknown-field rejection are compatibility contracts. Changes require a schema decision and regression tests.
- Preview, audio planning and export share `TimelineEvaluator`; encoding never interprets clip placement. `FfmpegEncodingBackend` is deliberately not implemented and must not claim successful output.
- Current scope does not implement Clappers/Recipes/Python, decoding/playback, production image rendering or live MCP. Do not infer those capabilities from the product vision.
- Run `dotnet test test/Kachinco.Tests/Kachinco.Tests.csproj -c Release` for headless changes; build `Kachinco.slnx` on Windows for shell changes. CI separates Linux contracts from Windows build/startup and runs once on non-main branch pushes.
- Use `staging/windows-foundation.md` for human interaction/layout acceptance; an automated startup smoke is not visual QA.

## 16. Phase 1 media/timeline authority (Issue #3)

- WPF imports through Infrastructure `IMediaProbe`; never construct ffprobe/FFmpeg arguments or parse probe JSON in UI code.
- Probe/validate before `RegisterMedia` or `RelinkMedia`. Relink preserves `mediaAssetId`, clip IDs and timeline placement; missing files are warnings and never prevent project load.
- `TimelineViewport`, `TimelineSnapping` and `TimelineEditPlanner` are transient authoring projections over `TimelineTime`, not a second timeline model.
- Pointer gestures may preview in pixels, but commit exactly one typed move/trim/split command from the original committed range. Never accumulate per-event pixel deltas into Project state.
- Track labels V1/A1/S1 are presentation only. Address tracks and clips by stable ID.
- Selection, playhead/edit cursor, scroll, zoom, snap toggle, hover and drag state remain non-persistent.
- Playback-looking toolbar buttons stay disabled until Phase 2's shared decoder/clock/evaluator architecture exists. Do not add a WPF timer or fake play state.
- Run the Phase 1 headless media/relink/authoring tests, Windows build and startup smoke. Record physical Windows visual acceptance separately in `staging/windows-phase1.md`.
