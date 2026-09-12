# FLAMORIS Kachinco Project Plan

Status: **Initial proposal / v0.1 requirements captured**  
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

## 2. v0.1 product constraints

The first implementation is intentionally narrow.

### Platform and language

- Primary implementation language: **C#**.
- Initial desktop target: **Windows**.
- Keep the editor core independent from Python execution details.

### Project/output sizes

Only two initial canvas presets are required:

- **YouTube landscape:** `1920x1080`, `16:9`.
- **YouTube Shorts / vertical:** `1080x1920`, `9:16`.

Do not add a general arbitrary-resolution product surface until real use requires it.

### Initial media import

Only these source types are required initially:

- **MOV** video.
- **WAV** audio.

A broad codec/container matrix is not a v0.1 goal.

### Manual editing scope

Kachinco needs a practical **basic NLE feature set**, comparable to the essential day-to-day subset of Premiere rather than Premiere feature parity.

Required baseline capabilities include:

- media import/register
- video/audio tracks
- clip insert and placement
- playback, pause, seek and scrubbing
- move
- trim
- split
- delete
- duplicate where useful
- mute/visibility controls
- basic transform and opacity
- simple stacking/compositing
- captions/subtitles
- Undo/Redo
- project save/reopen
- final export

### Compositing baseline

The compositor must treat blend behavior as a first-class clip/layer property.

Required early blend/compositing behavior:

- **Normal**
- **Screen**
- **Add**
- **Multiply**
- **Alpha**
- opacity

**Screen is required early**, not a later polish feature, because black-background fire, sparks, glow and light assets are expected in the FLAMORIS workflow.

### MCP-first requirement

MCP is a foundational control surface from the architecture root.

- UI and MCP operate on the same project/domain model.
- UI and MCP mutations use the same Command/Transaction/Undo-Redo path.
- There is no hidden AI-only timeline, clip type, edit state or mutation route.
- Stable IDs and domain coordinates are authoritative; display names are convenience references.

### Programmable effects

Effects are not limited to a predefined catalog.

- AI authors restricted Python **Recipes**.
- Recipes call Kachinco rendering primitives.
- Recipes compile/translate into typed render commands/IR.
- The deterministic renderer owns actual rendering semantics.
- Generated output becomes a normal Clip.
- Recipe provenance remains attached so results can be regenerated and modified reproducibly.

### Future integration expectation

Future integration with systems such as **Kinetai** and **AudioAnalyzer** is expected.

These integrations must live behind explicit adapters/boundaries so they can provide media, analysis metadata, markers, timing, generated content or commands without becoming dependencies of the Kachinco editor core.

Examples:

```text
AudioAnalyzer
  -> beat / onset / section / loudness metadata
  -> normal Kachinco markers, Clappers, automation or commands

Kinetai
  -> generated motion/media/metadata
  -> normal Kachinco media assets and Clips
```

## 3. Non-goals for the first milestones

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
- broad container/codec support beyond the initial MOV/WAV scope
- arbitrary project resolutions beyond the two initial output presets

The first success criterion is a small end-to-end workflow that feels unusually direct.

## 4. Core product concepts

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

## 5. Architecture direction

```text
C# Editor UI
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
     Restricted Python Worker
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

Important boundaries:

- The application/editor core is C#.
- Python is an authoring language, not the renderer itself.
- Prefer a **separate Python worker/process boundary** so failure, timeout, memory limits and sandbox policy can be isolated from the editor process.
- Rendering semantics belong to the Kachinco renderer and typed command/IR layer.
- Future Kinetai/AudioAnalyzer support belongs under integration adapters rather than Core.

A likely responsibility split is:

```text
Kachinco.App            UI / Timeline / Preview
Kachinco.Core           Project / Sequence / Track / Clip / Clapper / Commands
Kachinco.Media          MOV / WAV decode, playback, cache
Kachinco.Compositor     transforms / opacity / blend modes / masks
Kachinco.Render         frame evaluation / final render / export
Kachinco.Mcp            queries / commands / AI-facing contracts
Kachinco.Recipe         Recipe model / Python worker bridge / Render IR
Kachinco.Integrations   Kinetai / AudioAnalyzer / future FLAMORIS adapters
```

Names are provisional, but responsibility boundaries should remain explicit.

## 6. Proposed implementation phases

## Phase 0: Repository and design foundation

Goal: establish enough written authority to avoid accidental parallel architectures.

Deliverables:

- README and AGENTS
- project plan
- repository boundaries
- initial ADRs for:
  - C# desktop/runtime stack
  - canonical timebase / rational FPS
  - fixed initial canvas presets
  - MOV/WAV media boundary
  - project/domain identity model
  - Clapper coordinate model
  - Recipe reproducibility contract
  - restricted Python worker/execution strategy
  - renderer boundary
  - blend/compositor semantics including Screen
  - future integration boundary
- minimal project skeleton
- local deterministic test runner

Acceptance:

- architecture responsibilities are documented
- no implementation choice silently creates a second time model or hidden AI state

## Phase 1: Timeline Core

Goal: open MOV/WAV media, put it on tracks, save/reload, and edit deterministically.

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
- insert / move / trim / split / delete
- basic transforms and opacity
- Command / Transaction
- Undo / Redo
- persistence
- basic preview shell
- project preset: 1920x1080 or 1080x1920

Acceptance examples:

- import one MOV and one WAV
- place both on a sequence
- move/trim/split them
- save, reopen, and get exactly the same timeline state
- Undo/Redo all meaningful mutations

## Phase 2: Playback and Basic Compositor

Goal: make the editor immediately useful as a small NLE before programmable FX arrive.

Scope:

- reliable MOV/WAV decode
- audio playback
- play/pause/seek/scrub
- track stacking
- clip transform and opacity
- blend mode domain and renderer support
- Normal
- Screen
- Add
- Multiply
- Alpha
- preview/final-render semantic alignment

Acceptance examples:

- put a black-background spark MOV above another clip
- set blend mode to Screen
- preview and final render show equivalent transparency/compositing behavior

## Phase 3: Clapper System

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

## Phase 4: MCP Editing Surface

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
- project canvas/output preset
- blend/compositor state where relevant

Mutation capabilities:

- timeline insert/move/trim/split/delete
- media registration
- blend/opacity/transform changes
- Clapper commands
- subtitle/caption commands as they become available
- transaction begin/commit/rollback or an equivalent compound command boundary

Acceptance example:

An MCP client can resolve `A-1`, inspect its range and geometry, and insert an existing media clip into the intended timeline context.

## Phase 5: Python Render Spike

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

- separate worker/process boundary preferred
- no unrestricted imports
- no arbitrary filesystem access
- no network
- no subprocess/shell
- CPU timeout
- memory limit
- explicit input/output handles

Outputs:

- still PNG first if useful for the spike
- short image sequence or video once frame evaluation is proven

Acceptance example:

The MCP client submits a Recipe that draws `グエー` moving from left to right inside the geometry of `A-1`. Re-running with identical inputs produces an equivalent deterministic result.

## Phase 6: Recipe -> Clip Vertical Slice

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

## Phase 7: Regeneration and Parameter Editing

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

## Phase 8: Rendering Primitive Expansion

Goal: increase expressive range without turning the project into a giant fixed-effect catalog.

Candidate primitives:

- blur
- color transforms
- masks/mattes
- richer blend/composite operations
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

## Phase 9: Audio and Subtitle Workflow

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

## Phase 10: Integration Adapters

Goal: accept useful outputs from other FLAMORIS tools without contaminating Core responsibilities.

Initial expected future integrations:

- Kinetai
- AudioAnalyzer

Possible AudioAnalyzer contributions:

- beat markers
- onset events
- song sections
- loudness/envelope data
- suggested Clapper ranges

Possible Kinetai contributions:

- generated motion assets
- generated video/media
- timing/scene metadata
- normal Clip insertion requests

Acceptance principle:

External tools feed ordinary Kachinco project objects, metadata or commands. Core project behavior must not require those integrations to be present.

## Phase 11: Anime/MV Composition Workflow

Goal: make the product genuinely useful for FLAMORIS-style short clips and music videos.

Candidate additions:

- transparency/alpha workflows
- Screen-overlay conveniences
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

## Phase 12: Production hardening

Goal: make repeatability, portability, and performance trustworthy.

Scope as justified by prior phases:

- export/backend hardening within the product's chosen format policy
- color-management decisions
- cache invalidation
- renderer version/migration strategy
- missing media/relink
- crash recovery/autosave
- larger project performance
- background render queue
- Windows packaging
- project compatibility tests
- release/build boundaries

Only expand media-format compatibility when real product needs justify it.
Only automate expensive end-to-end checks when stable product risks justify them.

## 7. MVP definition

A useful v0.x MVP does **not** require the later phases in full.

The smallest compelling demonstration is:

1. start a 1920x1080 or 1080x1920 project
2. import MOV/WAV
3. arrange clips on a timeline
4. play/scrub and perform basic trim/move/split/delete
5. prove Screen compositing with an overlay clip
6. create `A-1` with a time range + rectangle
7. expose `A-1` through MCP
8. submit a restricted Python Recipe
9. render text/graphics into a short clip
10. automatically insert it at `A-1`
11. save/reopen project
12. regenerate the generated clip from its Recipe
13. Undo the full AI insertion cleanly

If this feels good, the product thesis is validated.

## 8. Recommended first demonstration

Use the exact silly example because it tests the whole concept clearly:

> `A-1 に火の粉とキャプション「グエー」を左から右に飛ばして。`

The demo should show:

- `A-1` visibly marked in time and screen space
- MCP resolving it
- Recipe generation/validation
- text and simple particles rendered
- output appearing as a clip in the timeline
- Screen/alpha compositing where applicable
- a second request such as “火の粉を半分にして” regenerating the same clip lineage

If that works, the core product interaction is alive. 🎬🐹

## 9. Near-term decisions to settle before heavy implementation

1. **C# desktop/runtime stack**  
   Choose the Windows UI/runtime approach based on video/audio/preview requirements and maintainable native integration.

2. **Canonical timebase**  
   Consider reusing FLAMORIS 2D's 120000 ticks/sec convention if it covers Kachinco's audio/video needs cleanly. Record the decision in an ADR before implementation.

3. **MOV/WAV decode and export backend**  
   Evaluate FFmpeg or an equivalent backend behind a narrow Product abstraction. The initial product surface remains MOV/WAV-focused even if the backend technically supports more formats.

4. **Preview compositor**  
   Define how C# editor preview and final render share blend/transform/timing semantics. Screen must not behave differently between preview and final output.

5. **Python sandbox/worker**  
   Prefer a separate process boundary. Treat arbitrary Python code as hostile input and expose only the Kachinco Recipe API plus explicit asset handles.

6. **Recipe IR**  
   Prefer typed render commands/IR so Python is not permanently welded to the renderer implementation.

7. **Generated media storage**  
   Define project-relative/cache/generated-output policy and what is required for portability and deterministic regeneration.

8. **Integration boundary**  
   Define a small adapter contract for Kinetai, AudioAnalyzer and future FLAMORIS tools before any one integration becomes a Core dependency.

## 10. Development philosophy

- Vertical slices before broad subsystems.
- Deterministic state before clever automation.
- One project model for human and AI.
- Stable IDs and explicit coordinates.
- MCP from the root, not bolted on later.
- Recipes remain inspectable.
- Generated results remain editable.
- Add rendering primitives because they unlock families of effects.
- Keep the editor useful when ChatGPT is disconnected.
- Keep initial media/resolution scope deliberately narrow.
- Integrations feed the editor; they do not redefine the editor core.

The product should feel less like “AI driving an old editor UI” and more like **a shared editing desk where the human points and the AI can actually reach the same objects.**
