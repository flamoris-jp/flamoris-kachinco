# Creation workflow Windows acceptance (#84–#91)

Use the current feature build and a copy of a real project (for example the
グエー making-of). CI checks are separate from this physical acceptance.

1. New project -> Ctrl+S asks once. Save again/Ctrl+S does not ask. Open a project
   and repeat. Ctrl+Shift+S chooses another name; subsequent Ctrl+S uses it.
   Cancel and inaccessible destinations preserve filename and dirty status.
2. Import an unused image and choose Remove media. Disk file remains. Undo/Redo
   and save/reopen agree. Referenced media, including selected title images, refuse
   removal with a diagnostic identifying the reference.
3. During preparation, playback, pause and stop: |◀ and Home return to zero;
   double triangles move precisely one frame. Stop stays in place. Verify red
   line, time display and actual preview agree after rapid repeated operations.
   Home in a text input remains the input's editing operation.
4. A long sequence scrolls in pages when the red line reaches about 85%. Ruler
   and all tracks stay aligned. Scroll/drag/scrub manually: follow stays suspended
   until the next Play. Pause/stop never auto-scroll; |◀ still reveals zero.
5. Move/resize/restart; repeat maximized. Move to another monitor, close, remove
   that monitor, reopen. Repeat after DPI/resolution changes. Window remains
   operable. Cancelling dirty close must not be treated as a completed close.
6. Title image -> choose PNG/JPEG/WebP, preview and independently export PNG/JPEG.
   Choose current composited frame after transforms/subtitles/effects; verify it
   includes all visible layers, not just one source. Capture is a frozen image.
   Clear/Undo/Redo, missing source and save/reopen behave consistently.
7. Reserve first frame at 30 and 30000/1001 fps: image/black occupies exactly one
   frame; audio/video/subtitle/Clapper move together with no source-time change.
   Repeated reserve does not double-shift. Replace title later. Release reverses
   the shift; new content at zero or other content on title track causes a clear
   rejection. Export MP4 and inspect its first two frames.
8. Put two clips touching on one video track; select the later clip and apply a
   .5-second crossfade. Outgoing source must have spare handles. Confirm smooth
   preview and export; audio and unrelated edits stay put. Inspect/change the
   opacity points, clear, Undo/Redo and reopen. Try insufficient handles, a gap,
   short incoming media and existing opacity automation: reject atomically.
9. MCP: refresh the producer/catalog for the new tool schemas. Upload image,
   SetTitleImage, ReserveTitleFrame; query title_image. Capture through
   title_image_from_frame. ReadOnly must reject capture/edit. Stale revision,
   save-as, document replacement, cancellation and revocation must not commit
   capture or disclose an obsolete image. Existing edit_batch clients must use
   the new typed alternatives while keeping document/revision guards.
