# FLAMORIS Kachinco

**AI-native timeline editor and programmable compositor for reproducible video creation.**

FLAMORIS Kachinco is being built to combine timeline editing, compositing, subtitles, named scene regions, MCP control, and restricted Python authoring with deterministic rendering in one workflow.

The core idea is simple:

> A human points to **where** and **when** something should happen. AI writes **how** it should happen. Kachinco renders the result as an ordinary editable clip.

Kachinco is not intended to reproduce Premiere Pro or After Effects feature-for-feature. Its design starts from a different assumption: **AI is a first-class editor from day one.**

## Initial product constraints

The first product is deliberately narrow.

- Primary implementation language: **C#**.
- Initial desktop target: **Windows**.
- Project/output formats are initially limited to:
  - **1920x1080, 16:9** for normal YouTube video.
  - **1080x1920, 9:16** for YouTube Shorts / vertical video.
- Initial media import is intentionally limited to:
  - **MOV** video.
  - **WAV** audio.
- The manual editor only needs the practical **basic NLE feature set** expected from a simple Premiere-like workflow: media import, timeline tracks, playback/scrubbing, clip insert/move/trim/split/delete, basic transforms/opacity, audio placement, captions/subtitles, Undo/Redo, save/reopen, and export.
- **MCP is a foundational product surface**, not an optional automation layer added later. UI and MCP operate on the same project/domain model and command/history system.
- The compositor must support at least **Normal, Screen, Add, Multiply, Alpha and opacity** workflows. **Screen blend mode is required from the early product** so black-background light/fire/spark assets can be composited naturally.
- Python is used for programmable, open-ended effect authoring. Generated results are rendered into ordinary timeline clips and remain reproducible through their Recipe provenance.
- Future integrations such as **Kinetai** and **AudioAnalyzer** are expected, but they must connect through explicit integration boundaries rather than becoming dependencies of the editor core.

The narrow codec/resolution scope is intentional. Kachinco should first prove fast, reliable AI-assisted editing before expanding into a broad compatibility matrix.

## Core workflow

1. Import MOV video and WAV audio.
2. Arrange normal clips on a timeline.
3. Create a named **Clapper** that binds a time range and, optionally, a canvas region/path and target track.
4. Ask an AI assistant for an edit using that name.
5. The AI queries the Clapper through MCP.
6. The AI produces a Python **Recipe** using the Kachinco rendering API.
7. Kachinco validates and renders the Recipe deterministically.
8. The rendered result is inserted as a normal timeline **Clip**.
9. The Recipe remains attached so the clip can be reproduced, adjusted, or regenerated later.

Example:

> `A-1 に火の粉とキャプション「グエー」を左から右へ飛ばして。`

Kachinco resolves `A-1`, renders the requested visual effect, and places the resulting clip at the named time and region.

## The three core objects

### Clapper

A human-authored named reference to editing context.

A Clapper may contain:

- stable ID and human-readable name such as `A-1`, `Chorus-Impact`, or `Akino-Closeup`
- timeline start/end
- canvas point, rectangle, polygon, or path
- preferred target track/layer
- source clip references
- notes or intent

Clappers are the bridge between natural language such as “here” and deterministic timeline/canvas coordinates.

### Recipe

A reproducible program describing an effect or generated element.

Recipes are authored as restricted Python against the Kachinco rendering API. They must not depend on unrestricted filesystem, process, or network access.

A Recipe records enough information to reproduce its result, including:

- source code or normalized program representation
- input asset references
- Clapper references
- render settings
- deterministic random seed where randomness is used
- renderer/API version metadata

### Clip

The rendered result placed on the timeline.

Generated output remains a normal clip that a human can trim, move, duplicate, stack, mute, delete, or replace. Generated clips additionally retain provenance back to their Recipe so edits can be regenerated instead of becoming one-way baked output.

## Why Python

Kachinco does not need a fixed catalog of hundreds of effects.

Instead, it exposes a small set of powerful rendering primitives such as:

- image/layer composition
- text and captions
- transforms and opacity
- blend modes including Screen
- masks and mattes
- blur and filtering
- color operations
- noise
- particles
- warp/distortion
- drawing primitives
- temporal sampling
- deterministic random generators

AI can compose these primitives into new effects at request time. Successful Recipes can later be saved as reusable presets without limiting the system to predefined effects.

The goal is **open-ended effects with reproducible results**.

## MCP-first editing

MCP is the primary machine-facing control surface, not a hidden automation path.

Initial capability groups are expected to include:

### Timeline queries

- inspect projects, timelines, tracks, clips, playhead, selections, and media
- inspect clip timing and media properties
- inspect subtitle/caption tracks

### Clapper queries and commands

- create/update/delete/list Clappers
- resolve a Clapper by stable ID or name
- read its time range, canvas geometry, source context, and preferred insertion target

### Recipe/render commands

- validate a Python Recipe
- render a still or short preview
- render a full Recipe
- regenerate a previously generated clip

### Timeline mutation commands

- import/register MOV and WAV media
- insert/replace/move/trim/split clips
- create subtitle tracks and captions
- attach generated output to a Recipe and Clapper
- perform compound edits transactionally with Undo/Redo support

## Example AI-assisted jobs

Kachinco should eventually support requests such as:

- “Put the MOV and WAV on the timeline and align them.”
- “Create an SRT from these lyrics and place the captions on the vocal timing.”
- “In A-1, emit sparks and fly the caption ‘グエー’ from left to right.”
- “Make the caption in B-3 larger only during the chorus.”
- “Reuse the same effect from A-1 in C-2, but reduce particle density to 40%.”
- “Regenerate this generated clip with less glow while preserving its timing.”

## Product principles

1. **AI-native, human-visible**  
   AI edits the same project model the human sees. There is no parallel hidden AI timeline.

2. **Human names intent; machines use stable coordinates**  
   A Clapper name is convenient UI, while stable IDs and domain coordinates remain authoritative internally.

3. **Generated does not mean ephemeral**  
   Every generated effect should be reproducible from its Recipe, inputs, settings, and deterministic seed.

4. **Generated output is ordinary media**  
   Once rendered, the result behaves like a normal clip in the editor.

5. **Small rendering primitives, open-ended composition**  
   Prefer expressive primitives over a giant hard-coded effect catalog.

6. **Preview and final render share semantics**  
   Preview must not be a screen-capture approximation of final output.

7. **Deterministic core, explicit nondeterminism**  
   Randomness must be seeded; external AI-generated assets are explicit inputs rather than invisible runtime dependencies.

8. **Safe programmable rendering**  
   Python Recipes execute in a restricted environment with explicit APIs, resource limits, and no arbitrary OS access. Prefer a separate worker/process boundary from the C# editor runtime.

9. **Undo/Redo and transactions from the beginning**  
   MCP and UI mutations share the same command/history model.

10. **Start with useful editing, not framework grandeur**  
    Build the smallest vertical slice that lets a human point, name, ask, render, and see a clip appear.

## Initial architecture sketch

```text
Human / ChatGPT
      |
      | natural-language instruction
      v
     MCP
      |
      +----------------------+----------------------+
      |                      |                      |
 Timeline / Clapper      Recipe API            Project Queries
 Commands                   |                      |
      |                      v                      |
      |              Restricted Python Worker       |
      |                      |                      |
      |                Render Commands / IR         |
      |                      |                      |
      +----------------------v----------------------+
                             |
                    Deterministic Renderer
                             |
                   video / image / alpha
                             |
                             v
                     Timeline Clip
```

The editor/application core is C#. Python is an authoring language, not the renderer itself. The renderer owns deterministic evaluation, composition, timing, blend behavior, and output.

Future systems such as Kinetai or AudioAnalyzer should connect through explicit integration adapters and feed normal project media/metadata/commands into Kachinco rather than bypassing the domain model.

## Repository status

**The Issue #5 production slice is implemented on the feature branch for review.**
It extends the reviewed Phase 0/1 foundation with:

- full-length media-bin drops, atomic sequence extension, timeline-local navigation;
- shared MOV/WAV decoding, Normal/Screen RGBA composition and stereo audio mixing;
- render-ahead Windows playback, seek and cancellable H.264/AAC MP4 export;
- SRT round trips, caption editing and shared outlined Japanese text rendering;
- stdio MCP bridge to the same running editor session, shared history and jobs;
- named Clappers, v1 → v2 project migration, literal-only Python Recipe validation;
- alpha MOV Recipe clips with source/output hashes and explicit regeneration;
- self-contained Windows portable publishing and automated Windows raster checks.

Preview preparation uses the same snapshot renderer as export. It is not a claim
of real-time full-resolution composition: independent per-frame decoding is an
initial implementation and can be slow. Recipe proof supports text and particles,
up to 10 seconds, not arbitrary Python or a complete effects system. FFmpeg and
Python are external prerequisites. MOV embedded audio is not mixed; use WAV.
Human Windows visual/playback acceptance remains separate from CI.

See [Windows production workflow and limitations](staging/windows-production.md).

### Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
Install FFmpeg/ffprobe and place both executables on `PATH`; media import reports a
structured `FFPROBE_NOT_FOUND` diagnostic when it is unavailable.
On Windows:

```powershell
dotnet build Kachinco.slnx -c Release
dotnet test test/Kachinco.Tests/Kachinco.Tests.csproj -c Release
dotnet run --project product/Kachinco.App/Kachinco.App.csproj
```

The headless test project runs without WPF or private media; real codec tests
require FFmpeg/ffprobe and Recipe tests require Python 3.
The two CI jobs protect distinct risks: headless contracts on Linux and WPF
build/startup, restricted worker, repeatable rasterization and portable publish on Windows. CI runs on non-main branch pushes touching Product/Test
or build files; it does not duplicate the same suite on PR creation or main.

Current documentation:

- [Production architecture](docs/production-architecture.md)
- [Active roadmap](docs/roadmap.md)
- [Typed editing / live MCP contract](docs/api-contract.md)
- [Project file v1](docs/project-format-v1.md) and [v2 migration](docs/decisions/0004-authoring-v2.md)
- [Manual Windows acceptance](staging/windows-foundation.md)
- [Phase 1 Windows acceptance](staging/windows-phase1.md)
- [Production Windows acceptance](staging/windows-production.md)
- [Original product vision](docs/project-plan.md)

## Working name

**FLAMORIS Kachinco** 🎬

“Kachinco” comes from the Japanese name for a film clapperboard. The name reflects the central idea that a human can mark and name a place in time/space, then let an AI act on that named editing context.
