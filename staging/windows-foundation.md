# Windows foundation acceptance

Manual checks; not evidence of completion until performed on Windows.
CI separately builds WPF and verifies that its process creates a main window.

1. With .NET 10 SDK, run `dotnet run --project product/Kachinco.App`.
2. Create landscape then portrait projects; verify displayed size and 60 s duration.
3. Register a local MOV and WAV; enter their known durations. This version does not
   probe duration, inspect codecs or play the files. Confirm metadata appears.
4. Select each asset and add it. Each insertion creates one new corresponding
   track, places the clip at zero, and limits its duration to the sequence range.
5. Undo one insertion: both its track and clip disappear. Redo restores them.
6. Delete a selected clip, Undo/Redo, then save `.fkproj` and reopen it. Names,
   placement, source references and IDs in JSON must remain unchanged.
7. Try malformed/future-version JSON: the open project must remain intact and an
   error must be shown. Cancel file dialogs without changing the project.
8. Modify a saved project, then New/Open/Close: confirm the unsaved-change prompt.
9. Resize to minimum window size; test Windows 100%, 150%, 200% DPI. Check Japanese
   labels, splitters, scrolling, keyboard focus, and file dialogs.

No image-level preview/export parity, audio sync, codecs or output videos are
claimed. Those are later milestone acceptance tests, after real media rendering.
