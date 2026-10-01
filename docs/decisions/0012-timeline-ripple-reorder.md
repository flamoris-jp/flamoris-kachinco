# ADR 0012: bounded same-track ripple reordering

Issue #50 adds `RippleReorderClip(SequenceId, ClipId, BeforeClipId)` to the
ordinary native command path. `BeforeClipId = null` means the end of the moving
clip's contiguous run. A run is a maximal set of clips whose end/start ticks
touch exactly, sorted by timeline start. Inserting before a member of another
run is rejected; gaps, other tracks, captions and sequence duration are preserved.
Overlapping lanes cannot use this operation. Free-space and cross-track moves
continue to use `MoveClip` with their existing placement rules.

Native recomputes only the run's starts from its original start. Identity, source
ranges, durations, properties and provenance remain unchanged. A no-op insertion
is rejected. One command in an `EditBatch` is one revision and one history entry.
No project-file schema or C ABI change is needed. MCP explicitly exposes the same
typed command, including nullable `beforeClipId`, with its ordinary guards.

The Core planner requests a native command projection to obtain preview starts.
WPF paints those immutable derived starts and an insertion marker without editing
the session. A pointer in the first/second half of another clip chooses before/
after it; exact run boundaries are insertion targets too. Real empty space uses
the original free move. Adjacent context-menu reorder uses the same planner.
Escape, lost capture, reload and invalid drop restore committed visuals. The
preview's expected revision must still match at commit.

Duplicate uses a fresh stable ID and the original clip's complete source and
property records. It uses the first free interval after the source clip, extending
the sequence only when needed, in one ordinary command batch. It preserves gaps
and never shifts another clip. Context-menu split uses the current playhead.

Validate native projection, forwards/backwards and unequal-duration permutations,
gap/overlap rejection, preview cancellation, stale revision, persistence, shared
UI/MCP history and Windows menu/geometry. Physical pointer feel is manual Windows
acceptance; automated shell checks are not evidence of that feel.
