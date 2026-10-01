# Issue #50 physical timeline acceptance

Automated Windows smoke checks actual WPF clip positions, insertion markers,
cancelled/committed gesture events, context-menu actions and shared session history.
It does not establish physical pointer feel. Record date, commit and tester here
after using real clips; these checks remain pending.

- [ ] Drag C into A/B in a contiguous A/B/C/D run; neighbors visibly move aside
  and a gold insertion marker appears before drop. Verify both directions and
  unequal clip lengths, including audio-only lanes.
- [ ] Drop and confirm selection stays on the moved clip. One Undo restores the
  original order; one Redo restores the insertion.
- [ ] Escape, lost mouse capture and document reload cancel the preview without
  changing order, source ranges or history. Leave/reenter the lane during a drag.
- [ ] Drag to real free space, another compatible lane and an incompatible lane;
  confirm existing free/cross-track moves and visible invalid placement remain.
- [ ] Preserve real gaps and unrelated audio/video tracks. Try an overlapping lane;
  ripple must be unavailable rather than silently destroying overlap semantics.
- [ ] Right-click an unselected clip. Verify Split at the playhead, Delete,
  Duplicate, Move earlier and Move later; unavailable operations are disabled.
- [ ] Duplicate preserves trim/appearance/audio and uses the first free interval
  after the source. Undo also restores any sequence duration extension.
- [ ] Open a menu with Shift+F10 on a focused clip; confirm keyboard access.
- [ ] Repeat after zoom/scroll and a Japanese/English language switch. Save/reopen;
  verify the reordered placements persist. Query/Undo/Redo through MCP too.
