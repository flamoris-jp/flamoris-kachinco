# ADR 0010: one native editing session and project codec

Accepted for #35. The #34 physical A/V gate is deferred until migration completion
by owner direction on 2026-09-30 JST; no physical result is inferred from CI.

The native session owns the project, command application/validation, revision,
bounded history and document generation. Its C ABI takes length-delimited UTF-8
requests and returns owned immutable response buffers. JSON is an internal typed
transport; integer ticks remain exact signed 64-bit values. No STL or exceptions
cross the ABI. Native parses, validates and applies every command; managed records
are immutable projections, never a second session or candidate editor.

The managed EditorSession facade retains one lock over the same native handle for
WPF and MCP. Before a prepared native change is committed, cancellation and
document invalidation observers run under that lock. Native preparation never
changes visible state; commit is guarded by a revision and prepared transaction
token. A cancelled/rejected change preserves the project and both history stacks.
Document tokens change for replacements and project identity changes (including
history travel), and grants are revoked before the new document becomes visible.

Project file v1/v2 schema, decimal-string ticks, required/unknown/duplicate-field
rules and v1 authoring migration remain unchanged. Native owns schema conversion
and validation. Managed ProjectFileStore retains only bounded UTF-8 filesystem I/O
and atomic temporary-file replacement. nlohmann/json 3.12.0 is vendored with its MIT
license; builds require no network dependency download.

Frozen reviewed managed command/session/validation code remains test-only for
conformance. Production has no managed command applier or validator fallback.
Native snapshots are immutable queries; native evaluation snapshots stay derived
from the single committed editor state. Recipes/workers, device/codec async hosts
and desktop/MCP transport retain their existing explicit integration boundaries.

Verify complete command/batch/revision/history/lifecycle diagnostics, malformed
and legacy project files, Clappers/Recipes/provenance, cancellation and shared MCP
history/grants, then full headless, sanitized native and Windows package gates.


## History retention correction (#45)

History entries retain immutable pairs of forward/backward JSON patches between
validated committed candidates, instead of deep-copied whole Project snapshots.
Small scalar edits therefore retain only their changed paths and values; unrelated
caption text, Recipe source and media data are not duplicated per revision. Patches
are internal native state, never serialized project fields or a new public command
contract. Undo/Redo applies the stored direction to the current project during
preparation and transfers the same immutable entry between the bounded stacks.
Null/project transitions, batch grouping and no-op history entries remain supported.
Replacement still clears both stacks and branch edits discard redo.

Full candidates are still copied and validated during preparation; this correction
addresses retained history memory, not the cost of preparing or querying a large
project. Changes to large values (and array shifts) can legitimately retain large
patches. Commit remains an allocation-free swap after response allocation; failed,
dry-run or aborted preparations do not alter visible state or history.
