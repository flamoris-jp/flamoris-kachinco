# ADR 0027: KineticTypo (キネタイ) as a companion text editor

Date: 2026-10-24 (JST)

Status: Proposed implementation design. Product requirements below were requested
by the owner; this document does not claim an implemented executable or renderer.

Implementation tracking: [#109](https://github.com/flamoris-jp/flamoris-kachinco/issues/109).

## Product decision

KineticTypo lives in the Kachinco repository as a companion feature with a separate
Windows executable, provisionally `KineticTypo.App.exe`. Kachinco can launch it to
edit animated text; it can also open a standalone text composition.

The UI is C# / WPF on the repository's .NET 10 baseline. Text layout, motion
evaluation and drawing are native C++. GPU drawing is a delivery requirement.
The existing native editing/time/composition foundations are reused instead of
forking Kachinco or introducing another production evaluator.

PONPON and AudioAnalyzer belong to
[StudioClient](https://github.com/flamoris-jp/flamoris-studio-client).
Semantic analysis is not required for any initial workflow.

## Current baseline and the work it does not provide

- Native D3D11 composition and independent encoding choices already exist
  (ADRs 0016 and 0020). Existing presentation includes CPU readback/transfers.
- Windows captions use a managed WPF raster adapter. Adding native GPU text is
  new work; GPU composition alone does not satisfy that requirement.
- Restricted Recipes currently support a bounded literal-only text/particle
  proof, limited to 10 seconds. General per-character motion scripting and
  whole-song typography are not implemented by that proof.
- Existing composition starts opaque black. A reusable transparent text surface
  requires an explicit alpha contract; changing existing pixel rules globally
  would be a regression.

## Native GPU text boundary

Default implementation direction: native DirectWrite layout/shaping with
Direct2D device-context drawing interoperating with D3D11. This supports the
requested C++ drawing boundary while retaining the existing D3D11 foundation.
Resource creation, BGRA compatibility, alpha conversion and thread ownership
must be proven in the first native slice before wiring a complete editor.

Native text surfaces are transparent, with a documented premultiplied-BGRA
internal contract and an explicit conversion at the existing straight-RGBA
composition boundary. Tests cover zero alpha, translucent edges, stroke/shadow
and color conversion; never apply alpha twice. Layout, shaping, glyph runs,
clusters and animation evaluation belong to native code, not WPF TextBlocks.
WPF owns editor controls and presentation only.

Use bounded reusable resources; invalidate layout/glyph caches on text, font,
size, style, DPI/canvas or font-resolution changes. Missing fonts produce visible
diagnostics with the actual resolved face. Do not split UTF-16 code units to
animate characters: preserve shaping clusters, combining marks and surrogate
pairs, with an explicit mapping from source text to animated units.

Preview, specified-time capture and export consume the same native text
evaluation and raster semantics. GPU failure is reported with actual backend
diagnostics. Existing CPU recovery for ordinary Kachinco must survive; a software
text recovery path is acceptable but does not count as GPU acceptance. WARP is
test evidence, not physical GPU acceleration evidence. Avoid zero-copy or
cross-driver pixel-identity claims without measurements.

## Script and editable clip model

Use the existing restricted Recipe direction: validated authoring source becomes
typed/versioned motion IR and native evaluation. Extend that language explicitly;
do not describe the current literal-only worker as a general Python runtime.
No arbitrary Python, C#, shader code or OS access executes in the UI/render loop.

Initial expressive primitives cover transform, opacity, stagger and bounded
seeded shake/flight over text units. Presets such as ブルブル, 飛来 and 散る are
inspectable compositions of those primitives, not separate rendering engines.
Manual parameters and AI-generated programs produce the same editable state.

Durable state retains stable text/clip IDs, text, timing, font/style references,
normalized program, IR/API version, parameters, explicit seed and provenance.
Changing a library preset never changes already-applied work. At any requested
canonical tick the result is independent of playback order and wall-clock time.

Use Kachinco's native TimelineTime (35,280,000 ticks/second), rational FPS and
half-open ranges. PONPON JSON/SRT import converts once at the boundary and retains
source IDs/provenance. SRT is an exchange format, not editable project authority.
Do not impose the old Recipe proof's 10-second limit on a song: bound active
clips, compile work and caches rather than loading every frame into memory.

## Launch, edit and return

Kachinco launches an installed companion from its own validated package location,
passing a bounded same-user session/handoff reference. Do not launch an arbitrary
executable supplied by a project or accept a remote command string.

The companion edits an explicit working snapshot. Returning changes binds the
source document, stable target IDs, expected revision and handoff identity.
The host applies one validated command transaction with ordinary Undo/Redo.
If the document changed or closed, retain the companion draft and report a
conflict; never overwrite the project file behind a running editor. Cancel and
crash leave the host project unchanged. Imports create fresh stable IDs.

Standalone work owns one explicit native document/session. Attached mode has no
independent authority over the host project. Closing the companion does not
prevent Kachinco from rendering saved text clips.

Native project schema/ABI changes need conditional versioning, legacy migration,
unknown-version rejection and capabilities before shipping editable text state.
This ADR does not allocate a schema number or reinterpret existing Recipe fields.

## Output and integration

The primary Kachinco handoff remains editable text/motion state, so ordinary
same-engine editing need not generate intermediate videos. When a rendered
interchange/cache is requested, the owner-selected default is a synchronized
**RGB video on black plus a separate grayscale opacity-mask video**, not an
alpha-channel MOV requirement.

- Proposed package: `text_rgb.mp4`, `text_mask.mp4` and a versioned manifest
  retaining source/recipe/seed, frame rate, dimensions, origin, duration, exact
  frame count, pair identity/checksums, color/range and RGB association.
- White mask pixels reveal the text; black hides it; gray carries antialiasing,
  translucency, blur/shadow and fades. Generate the mask from the native opacity
  surface, never from the RGB video's brightness. Black letters and shadows must
  remain valid foreground content. This is not black-color keying.
- Produce both streams from the exact same native evaluation/frame requests,
  with matched CFR timestamps and frame count. Import as one paired logical
  asset/clip. Move, trim, speed, relink, cache and export must use the same source
  mapping for both; reject mismatched or incomplete pairs before committing.
- The black-matted RGB stream is associated/premultiplied color. With normalized
  decoded mask M, normal composition is RGB + (1-M)*background; do not multiply
  RGB by M again. Store that association explicitly. Define/retest byte rounding,
  mask range normalization and color handling at the native boundary. Other
  blend modes need equivalent association-aware semantics before being enabled.
- H.264 MP4 is the first compatibility/size candidate. Set RGB and mask quality
  independently, preserving mask edges and exact black/white normalization.
  Measure the **combined** size and two-decoder cost against alpha alternatives
  at comparable visual quality; no fixed size reduction is promised. Chroma
  subsampling/ringing and mask compression must pass the edge/halo tests.
- Existing Kachinco has no paired-mask asset/track-matte implementation. Add its
  native command/schema, evaluation, CPU/D3D11 composition and packaging contracts
  in an explicit implementation slice. Do not insert masks as visible timeline
  layers or implement mask evaluation solely in FFmpeg/UI/MCP.
- Preserve the internal native alpha surface contract above; separating alpha at
  the file boundary does not eliminate opacity evaluation inside the renderer.
  A self-contained alpha video or PNG sequence is optional future interchange,
  not a prerequisite for this RGB/mask workflow.

GPU drawing and video encoding remain independent policies. Reuse available
hardware encoding after actual initialization checks, with observable fallback.
The pair contains no duplicated soundtrack by default; audio stays in the host
timeline. The supplied legacy KineticTypo already documents RGB/mask generation;
reuse its intent and fixtures without importing its ASS renderer as native GPU
implementation authority.

UI and MCP query/edit the same native commands, selection and playhead projections.
MCP can inspect text, program/parameters, target IDs, revision and render backend,
validate changes, request a frame, and apply one revision-checked transaction.
Reuse the shared desktop connection foundation after inspecting its current
contract. Capture follows the visible sharing setting; it is not final rendering.
Hub descriptors change only with implemented producer tools.

## Delivery slices and acceptance

1. Native GPU text proof: a Japanese/English line with native font layout, alpha
   and seeded motion. Same requested ticks reproduce during seek and export;
   actual hardware D3D11 is reported. No WPF text rasterization in this slice.
2. Separate C# shell: open/save a text composition, scrub, edit one clip and
   parameters, Undo/Redo; preserve font/script/seed through reopen. Package a
   matched executable and native DLL together.
3. Kachinco launch/return: selected text opens, explicit Apply is one undoable
   host edit, conflicts/cancel/crash preserve the host project and draft. Add
   paired RGB/mask export/import with a native mask evaluator/compositor; verify
   synchronized trimming/speed and reject incomplete or mismatched pairs.
4. PONPON JSON/SRT import and optional audio-feature binding. Missing audio,
   Analyzer or semantic data does not prevent ordinary text animation.
5. Versioned script extension, presets, MCP and full-song resource tests.

Deterministic tests cover native motion, time/trim behavior, serialization,
schema/capability rejection and revision conflicts. Windows tests cover native
glyph/alpha rendering, package startup and GPU device loss. Physical acceptance
compares preview/capture/export, Japanese fonts, black/colored glyphs, gray mask
edges/shadows, halo-free association and range handling, synchronized RGB/mask
trim/speed/relink, combined file size, two-decoder cost, seek latency, frame
preparation and VRAM on real hardware. Measure; do not promise a frame rate.
Existing Kachinco projects and caption rendering remain compatible.

## References

- [Native evaluation](0009-native-evaluation-playback.md)
- [Native editing](0010-native-editing-authority.md)
- [Effects](0015-still-images-and-effect-library.md)
- [GPU composition](0016-d3d11-preview.md) / [GPU export](0020-gpu-export.md)
- [Microsoft: Direct2D device contexts and D3D11 interoperability](https://learn.microsoft.com/en-us/windows/win32/direct2d/devices-and-device-contexts)
- [Microsoft: DirectWrite and Direct2D text](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)

- [FFmpeg: alphaextract / alphamerge](https://ffmpeg.org/ffmpeg-filters.html#alphaextract)
