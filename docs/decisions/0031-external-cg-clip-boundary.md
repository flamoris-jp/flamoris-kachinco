# ADR 0031 — External CG clips and legacy Scene Engine scope

Status: documentation reconciliation, 2026-11-01. No runtime, project format, dependency or release change.

## Context

[#124](https://github.com/flamoris-jp/flamoris-kachinco/issues/124) proposed an independent scene/physics engine before Cinema was paused and [flamoris-CG](https://github.com/flamoris-jp/flamoris-CG) was established. [Glyph #3](https://github.com/flamoris-jp/flamoris-glyph/issues/3) waited on its scene schema. Both issues retain their proposals as history; neither is closed as completed or abandoned.

The item-by-item classification lives in [CG's scope document](https://github.com/flamoris-jp/flamoris-CG/blob/main/docs/scene-engine-scope.md). Producing-side research is tracked in [CG #8](https://github.com/flamoris-jp/flamoris-CG/issues/8).

## Responsibility

- CG owns individual object-animation scenes, motion/time, render requests and clip output/provenance. Today it is a Blender lab; physics, Glyph ingestion, encoding and automated handoff remain unimplemented.
- Glyph owns immutable shape/style/provenance and its asset contract. CG placement/unit conversion and optional collision proxies are consumer-side work.
- Blender executes native modeling/physics/rendering behind CG's bounded experimental interface.
- Kachinco owns media registration, timeline placement, audio, composition, project/history and final export. KineticTypo owns text animation; its existing Glyph import is independent of the proposed CG actor adapter.
- Cinema's future project-wide intent/planning stays paused. Its historical implementation and acceptance evidence stay preserved.

## Decision for #124

Retain #124 as the receiving-side tracker and historical umbrella. Move clip-level production research responsibility to CG #8. Do not implement physics or a duplicate scene editor in Kachinco.

A new independent engine repository, universal scene schema, independent solver/renderer and Cinema resumption are not prerequisites. This removes obsolete sequencing assumptions, not the history or all future research.

Keep fixed-step determinism/tolerances, alternate-camera replay, 2D collider fallback and walk/trip/fall/recovery as future research. The combined 720p five-second demo is historical, not the current mandatory first milestone.

## Dependencies and receiving-side acceptance (not yet delivered)

1. CG selects and validates real media plus fps, frame count/duration, alpha semantics, asset provenance and producing tool/version data. This does not prescribe a new manifest schema now.
2. Check the actual codec/container/alpha route against supported Kachinco interfaces. CG PNG sequences are not assumed to be directly importable video; select a supported encoded or converted asset explicitly.
3. Convert rational fps and duration through Kachinco's canonical 35,280,000 ticks/second utilities. CG's 1-based frames and Cinema samples are not persistent timeline time.
4. Probe/register/import through existing commands with revision/history safeguards where applicable; preserve manual placement, unrelated tracks, source files and producer scene editability. No external direct project mutation.
5. Verify import, placement, preview, save/reopen and export for the chosen fixture. Record platform/codec/alpha limits separately from source/CI evidence.

Ordinary supported-media import needs no shared scene schema. Automated handoff depends on the chosen output contract and fixture, not Cinema or every physics/Glyph research gate. The Glyph → KineticTypo path in [ADR 0030](0030-glyph-and-linux-kinetai.md) is separate and unchanged.

## Preserved constraints

Keep bounded validated inputs, no arbitrary code/remote assets/path escapes, provenance, explicit regeneration and portable contracts. Cinema acceptance does not certify CG; CG capabilities need independent acceptance. This ADR initiates no runtime execution, deployment, engine repository or Cinema feature work.
