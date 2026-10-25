# ADR 0029: programmable kinetic expressions

Date: 2026-10-25 · Issue #114

## Decision

Keep Python as an authoring/compiler language and C++ as the only motion evaluator.
The existing bounded AST worker additionally accepts a function
`animate(t, duration, i, n, seed)` returning any of `x`, `y`, `scale`, `rotation`,
`opacity`. Local arithmetic, comparisons, conditional expressions/statements,
small helper functions and literal `range` loops compile to typed expression IR.
No user Python is executed. Time is clip-relative canonical ticks converted to
seconds once at evaluation; `i` and `n` address DirectWrite shaping clusters,
not UTF-8 bytes or code points. Full-line transformations use expressions that
ignore `i`. Translation is added to the clip origin; scale and rotation retain
the existing glyph-local behavior. This does not deform glyph outlines.

Program v2 is `{version:2, outputs:{property:expression}}`. Expressions are bounded
prefix arrays with numeric constants, five named context values, whitelisted
math/comparison operators and lazy select/and/or. Source is retained in the clip.
Math includes sin/cos/tan, abs/sqrt/floor/ceil, min/max, clamp/lerp/smoothstep,
pow and deterministic seeded noise. Defaults are x/y/rotation=0, scale/opacity=1.
Opacity clamps to 0..1; other finite output bounds are validated at evaluation.
Invalid domains/division/overflow fail the frame explicitly, never publish NaNs.

Limits: existing 64 KiB source/worker limits; 4096 AST nodes; at most 16 functions,
8 inline call levels, 64 loop iterations per loop / 512 expanded statements;
1024 expression nodes per program, depth 32, 64 KiB compiled program; 2 million
expression evaluations per rendered frame across all active clips. No imports,
attributes, comprehensions, arbitrary calls, recursion, while, mutable containers,
filesystem, network or process access. Expansion is budgeted before publication.
These are language capabilities, not permission prompts.

Native document validation owns both v1 and v2 program contracts. Existing v1
literal calls/presets remain unchanged. `.ktproj` outer version 1 and conditional
`.fkproj` schema 8 remain; older runtimes reject unknown nested program versions
instead of silently flattening motion. A new native capability and optional
unit-count evaluation ABI make mixed-package failures explicit. The legacy
kn_text_evaluate entry point remains v1-only; it never guesses the cluster count. All paths use the
same evaluator: companion, host preview, mask pair and final export. No baked
second clock, per-frame Python process, or hidden MCP editor.

## Authoring

The companion adds a source editor and explicit compile/apply action. Compilation
is asynchronous and commits one revision-guarded document edit. Selection, pending
fields, history or document changes invalidate the result. Errors retain the source
and the previous committed animation. File import uses the same compiler/action.
MCP exposes pure `compile_script`; ordinary `edit_composition` commits the returned
IR and source with the existing guards/history. No save or filesystem grant is added.

## Acceptance

Compiler boundary tests, direct native evaluation including end-relative fade,
cluster-dependent motion, deterministic noise, lazy domain guards, invalid IR,
Undo/Redo and persistence. Windows WARP compares same-tick rendering and ensures
one transparent cluster cannot suppress visible later clusters. Physical GPU
quality, full-song latency and script editor interaction remain separate checks.
