# ADR 0028: first KineticTypo runtime contracts

2026-10-24. Implements ADR 0027 in a separate C# WPF companion and the existing
native runtime. WPF may also live in `product/KineticTypo.App`; it owns controls,
not glyph shaping, motion evaluation or document mutations.

Kinetic composition v1 is a bounded native JSON document (16 MiB, 10000 clips,
4096 UTF-8 bytes/clip, 32 motion operations/clip, 128 active clips). Ticks are
canonical decimal strings; FPS is rational. Native document commands are atomic,
revision checked, with native undo/redo. Standalone documents are `.ktproj`.
Typed motion IR v1 is directly editable JSON: translate, scale, rotate, opacity,
shake, flight and stagger are bounded native primitives, with explicit seed.
The existing bounded Recipe AST worker has an explicit kinetic mode compiling
literal-only Python primitive calls to this IR. Source and normalized IR are both
saved; no arbitrary source executes. Ordinary Recipe compilation is unchanged.
DirectWrite shaping clusters are the motion units. Combining marks/ligatures and
surrogate pairs remain attached. Native D2D/D3D11 draws premultiplied BGRA; native
conversion supplies straight RGBA to the existing compositor and associated RGB
plus grayscale opacity to paired exports. Grayscale text antialiasing is used.
Hardware creation never silently selects WARP; WARP is explicit test-only evidence.
Linux rejects native text rendering with an unsupported-backend diagnostic.

Conditional project schema v8 adds `textComposition` and `maskSourcePath` to
assets and media kinds KineticText / MaskedVideo. Plain v1-v7 projects continue
conditional v2-v7 writes. Text state is embedded, so reopening/rendering does not
require the companion or draft file. `SetTextComposition` replaces an embedded
composition through the same native command/history as UI and MCP. Source duration
must still cover every existing clip. Unknown schemas and IR versions fail closed.
Paired video is one asset: source ticks, trim, speed and transforms are evaluated
once for both decoders. Native mask conversion feeds existing CPU/D3D11 composition;
no visible mask track. Mask is full-range decoded gray, RGB is associated over black.

Companion launch resolves only the adjacent packaged executable; a per-launch
snapshot contains document ID, expected host revision, asset ID and random handoff
ID. Apply writes an atomic response; the host validates the entire identity and
executes one native command batch at the captured revision. Cancel/close/crash does
not apply. Host conflict retains working draft and response. Export is a separate,
cancellable file operation; publish the manifest only after both streams complete.
