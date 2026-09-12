# FLAMORIS Kachinco Project Plan

Status: **Initial proposal**  
Repository: `flamoris-jp/flamoris-kachinco`

## 1. Product goal

Build an AI-native timeline editor and programmable compositor in which a human can mark and name a place in time/space, then ask an AI to create or modify content there.

The defining interaction is:

```text
Human marks context -> Clapper
Human asks in natural language
AI resolves Clapper through MCP
AI writes a restricted Python Recipe
Kachinco validates and renders it
Rendered media becomes an ordinary timeline Clip
Recipe remains attached for reproducible regeneration
```

The product should support normal manual editing without AI, while making AI-assisted editing a first-class workflow rather than an after-the-fact macro layer.

## 2. Non-goals for the first milestones

Do not attempt to build all of Premiere Pro and After Effects at once.

Initially out of scope:

- large hard-coded effect catalog
- professional NLE feature parity
- unrestricted Python execution
- arbitrary plugin hosting
- collaborative cloud editing
- distributed rendering
- advanced color-management suite
- full 3D compositor
- generative video model hosting
- complex motion-graphics node editor

The first success criterion is a small end-to-end workflow that feels unusually direct.

## 3. Core product concepts

### Clapper

A persistent named editing anchor binding:

- a stable ID
- a human-readable name
- a timeline time/range
- optional point/rectangle/polygon/path canvas geometry
- optional target track/layer
- optional source context and notes

### Recipe

A restricted Python program that produces typed rendering operations using Kachinco APIs.

A Recipe is reproducible authoring state, not disposable generated text.

### Clip

Rendered output inserted into the normal timeline. Generated clips retain provenance so they can be regenerated or revised.

## 4. Architecture direction

```text
Editor UI
  |
  +-- Timeline / Media / Captions / Clappers
  |
  +-- Command + Transaction + Undo/Redo
            |
            +--------------------+
            |                    |
           MCP              Manual UI
            |
            v
      Recipe authoring
            |
      validation/sandbox
            |
      Render Command IR
            |
      deterministic renderer
            |
         media output
            |
            v
         timeline clip
```

Important boundary: Python is an authoring language. Rendering semantics belong to the Kachinco renderer and its typed command/IR layer.

## 5. Proposed implementation phases

## Phase 0: Repository and design foundation

Goal: establish enough written authority to avoid accidental parallel architectures.

Deliverables:

- README and AGENTS
- project plan
- repository boundaries
- initial ADRs for:
  - canonical timebase / rational FPS
  - project/domain identity model
  - Clapper coordinate model
  - Recipe reproducibility contract
  - restricted Python execution strategy
  - renderer boundary
- minimal project skeleton
- local deterministic test runner

Acceptance:

- architecture responsibilities are documented
- no implementation choice silently creates a second time model or hidden AI state

## Phase 1: Timeline Core

Goal: open media, put it on tracks, save/reload, and edit deterministically.

Scope:

- Project
- MediaAsset registry
- Sequence / Timeline
- video tracks
- audio tracks
- Clip / ClipInstance domain
- canonical tick timebase
- rational FPS
- playhead and selection state
- trim / move / insert / delete
- Command / Transaction
- Undo / Redo
- persistence
- basic preview shell

Initial codecs may be intentionally narrow.

Acceptance examples:

- import one MOV and one WAV
- place both on a sequence
- move/trim them
- save, reopen, and get exactly the same timeline state
- Undo/Redo all meaningful mutations

## Phase 2: Clapper System

Goal: let the human define named editing context that AI can resolve reliably.

Scope:

- create/delete/rename Clapper
- timeline range binding
- point and rectangle canvas geometry first
- optional target track
- stable IDs
- visual overlay in the program monitor/timeline
- persistence
- queries/commands
- Undo/Redo

Later geometry:

- polygon
- path/stroke
- source-object/clip attachment

Acceptance example:

The user marks 00:12.4–00:15.1, draws a rectangle in the monitor, calls it `A-1`, saves/reopens, and `A-1` resolves to the same stable domain data.

## Phase 3: MCP Editing Surface

Goal: make the existing Product model controllable by AI without creating a separate editor.

Read/query capabilities:

- project
- sequence
- tracks
- clips
- media
- playhead
- current selection
- Clappers

Mutation capabilities:

- timeline insert/move/trim/delete
- media registration
- Clapper commands
- transaction begin/commit/rollback or an equivalent compound command boundary

Acceptance example:

An MCP client can resolve `A-1`, inspect its range and geometry, and insert an existing media clip into the intended timeline context.

## Phase 4: Python Render Spike

Goal: prove that AI-authored code can safely and reproducibly create visual media.

Keep this intentionally small.

Initial Kachinco Python API primitives:

- canvas/frame creation
- draw image
- draw text
- basic shape drawing
- transform
- opacity
- simple compositing
- deterministic random seed

Sandbox restrictions:

- no unrestricted imports
- no arbitrary filesystem access
- no network
- no subprocess/shell
- CPU timeout
- memory limit
- explicit input/output handles

Outputs:

- still PNG first
- short image sequence or video once frame evaluation is proven

Acceptance example:

The MCP client submits a Recipe that draws `グエー` moving from left to right inside the geometry of `A-1`. Re-running with identical inputs produces an equivalent deterministic result.

## Phase 5: Recipe -> Clip Vertical Slice

Goal: complete the first signature Kachinco workflow.

Flow:

1. resolve Clapper
2. validate Recipe
3. render media
4. register generated media
5. insert generated Clip at Clapper time/target
6. attach Recipe provenance
7. support one-command Undo

Add:

- generated-media metadata
- Recipe ID
- source Clapper references
- renderer/API version metadata
- regeneration command

Acceptance example:

User says:

> `A-1 に「グエー」を左から右へ飛ばして。`

AI resolves `A-1`, writes a Recipe, Kachinco renders it, and a generated clip appears in the editor at the intended place without manual import/dragging.

## Phase 6: Regeneration and Parameter Editing

Goal: make generated content genuinely editable rather than a one-shot bake.

Scope:

- select generated clip
- inspect linked Recipe
- replace/regenerate output while preserving intended timeline placement
- Recipe revisions
- compare old/new output
- deterministic seed control
- optional named parameters exposed to Inspector

Acceptance examples:

- “Make the text twice as large.”
- “Reduce sparks to 40%.”
- “Use the same effect in B-2.”

The edit should modify/reuse the existing Recipe rather than inventing an unrelated hidden effect.

## Phase 7: Rendering Primitive Expansion

Goal: increase expressive range without turning the project into a giant fixed-effect catalog.

Candidate primitives:

- blur
- color transforms
- masks/mattes
- blend modes
- noise fields
- particle emitter
- warp/distortion
- path animation
- temporal sampling
- image feedback/trails
- basic camera transforms
- alpha output

Add primitives based on actual requested effects and composability value.

Acceptance target:

A meaningful collection of anime/MV effects can be synthesized from primitives instead of needing one Product feature per visual effect.

## Phase 8: Audio and Subtitle Workflow

Goal: automate the boring-but-common MV assembly work.

Scope:

- waveform generation/cache
- audio/video alignment helpers
- caption domain model
- SRT import/export
- subtitle track editing
- lyric text ingestion
- AI-assisted caption timing hooks
- styled caption rendering path

Acceptance example:

The user imports MOV + WAV + lyrics and asks:

> “Line these up, make an SRT from the lyrics, and put the captions on the timeline.”

Kachinco performs the operation through ordinary inspectable project state.

Important: AI-assisted alignment may be approximate, but committed caption entries and timing are normal deterministic project objects.

## Phase 9: Anime/MV Composition Workflow

Goal: make the product genuinely useful for FLAMORIS-style short clips and music videos.

Candidate additions:

- transparency/alpha workflows
- overlay track conveniences
- reusable Recipe library
- user-saved Recipe presets
- caption motion recipes
- particles/trails/glow-like compositing recipes
- region/path-driven effects
- camera-shake recipe helpers
- rapid preview rendering
- proxy/cache management

Acceptance target:

A short FLAMORIS MV segment can be assembled, captioned, and given custom procedural effects without leaving Kachinco for routine operations.

## Phase 10: Production hardening

Goal: make repeatability, portability, and performance trustworthy.

Scope as justified by prior phases:

- codec/export matrix
- color-management decisions
- cache invalidation
- renderer version/migration strategy
- missing media/relink
- crash recovery/autosave
- large project performance
- background render queue
- Windows packaging
- project compatibility tests
- release/build boundaries

Only automate expensive end-to-end checks when stable product risks justify them.

## 6. MVP definition

A useful v0.x MVP does **not** require the later phases in full.

The smallest compelling demonstration is:

1. import MOV/WAV
2. arrange clips on a timeline
3. create `A-1` with a time range + rectangle
4. expose `A-1` through MCP
5. submit a restricted Python Recipe
6. render text/graphics into a short clip
7. automatically insert it at `A-1`
8. save/reopen project
9. regenerate the generated clip from its Recipe
10. Undo the full AI insertion cleanly

If this feels good, the product thesis is validated.

## 7. Recommended first demonstration

Use the exact silly example because it tests the whole concept clearly:

> `A-1 に火の粉とキャプション「グエー」を左から右に飛ばして。`

The demo should show:

- `A-1` visibly marked in time and screen space
- MCP resolving it
- Recipe generation/validation
- text and simple particles rendered
- output appearing as a clip in the timeline
- a second request such as “火の粉を半分にして” regenerating the same clip lineage

If that works, the core product interaction is alive. 🎬🐹

## 8. Near-term decisions to settle before heavy implementation

1. **Desktop/runtime stack**  
   Reuse the strongest practical lessons from FLAMORIS 2D/Cutwork, but choose based on video/audio/render requirements rather than habit.

2. **Canonical timebase**  
   Consider reusing FLAMORIS 2D's 120000 ticks/sec convention if it covers Kachinco's audio/video needs cleanly. Record the decision in an ADR before implementation.

3. **Video decode/encode backend**  
   Evaluate FFmpeg or an equivalent backend behind a narrow Product abstraction.

4. **Preview renderer**  
   Decide CPU/GPU boundary after the first vertical slice; do not build a giant renderer framework before requirements prove it necessary.

5. **Python sandbox**  
   Decide whether the first spike uses a separate process, AST-restricted interpreter, containerized worker, or another explicit boundary. Treat arbitrary Python execution as hostile input.

6. **Recipe IR**  
   Prefer typed render commands/IR so Python is not permanently welded to the renderer implementation.

7. **Generated media storage**  
   Define project-relative/cache/generated-output policy and what is required for portability.

## 9. Development philosophy

- Vertical slices before broad subsystems.
- Deterministic state before clever automation.
- One project model for human and AI.
- Stable IDs and explicit coordinates.
- Recipes remain inspectable.
- Generated results remain editable.
- Add rendering primitives because they unlock families of effects.
- Keep the editor useful when ChatGPT is disconnected.

The product should feel less like “AI driving an old editor UI” and more like **a shared editing desk where the human points and the AI can actually reach the same objects.**
