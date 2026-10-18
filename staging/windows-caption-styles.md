# Caption style acceptance (#95)

1. Import the Who Named You? SRT. Select a magenta caption on the timeline.
   The right inspector shows caption style controls, installed fonts, current values.
2. Change family, size, text/outline color using HEX and color picker, outline width,
   preset and detailed X/Y/alignment/width, shadow color/offset/enabled. Each change
   refreshes the preview. Size/offset units scale with the short canvas dimension.
3. Make one caption green and move it. Choose same subtitle track, then change
   only font. Both captions adopt the font; individual color and position survive.
   Repeat with sequence scope and another subtitle track. One Undo restores the
   entire operation. Select a different caption: scope returns to selected captions.
4. In the caption editor, Ctrl/Shift-select multiple rows and change style.
   Mixed styles show a notice with first-caption values. Merely focusing/blurring
   a field does not mutate anything. Apply this field explicitly copies a displayed
   value to the chosen scope even when that value already matches the first row.
5. Save/reopen; compare preview at Full/Half/Quarter and MP4 export for Japanese,
   English, multiline and long wrapping text on dark/light backgrounds, both 16:9
   and 9:16. Missing installed font families show a warning/use Yu Gothic. Existing
   unstyled captions retain their appearance and legacy project schema.
6. After coordinated Hub descriptor/desktop rollout, use get_project/edit_batch
   with PatchCaptionStyles and SetCaptionStyle. Check guards, Undo/Redo and frame
   capture. No styling is retained in SRT; use the project or rendered video.

Automated Windows check: `dotnet run --project test/Kachinco.WindowsSmoke -c Release -- --caption-styles <absolute-screenshot.png>`.
This verifies raster fallback/scaling/wrapping and UI partial/multi-select/scope
behavior, and produces the inspector image for visual review. Physical user
interaction and the deployed Hub route remain separately reported.
