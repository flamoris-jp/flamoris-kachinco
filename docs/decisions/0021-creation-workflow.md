# ADR 0021 — Creation workflow: media, transport, titles and crossfades

Status: accepted implementation contract for Issues #84–#91.

## Authority

Native EditorSession remains the only mutation/history/validation authority.
The WPF shell and MCP edit_batch share UnregisterMedia, SetTitleImage,
ReserveTitleFrame, ReleaseTitleFrame and ApplyCrossfade. No renderer, clock,
sandbox permission or Python capability is added. Required native capability is
now 65535 (new TITLE_EDITING bit).

## Titles and conditional schema v6

Sequence has nullable TitleImage { mediaAssetId, sourceFrameTicks } and
TitleSlot { trackId, clipId }. TitleImage references a registered Image asset.
sourceFrameTicks is null for external images, or the resolved canonical tick of
a composited frame captured from this sequence. Captures are immutable PNG files;
later edits do not regenerate them. Image pixels, aspect ratio and output follow
the same native evaluator/compositor and image-fit semantics as still clips.

v6 is written only while either title field is non-null. v6 sequences always
contain both fields; sourceFrameTicks is a nullable decimal-string Int64. Clip
encoding includes v5 speed and v4/v3 automation fields. v1–v5 remain strict
migration inputs, with unchanged conditional v2/v3/v4/v5 output when no title
metadata remains. Unknown/duplicate fields and numeric tick values remain invalid.
The source tick is provenance, not a live pointer or second timebase.

ReserveTitleFrame is one native command: add FrameToTicks(1, fps) to sequence
duration and every ordinary clip/caption/Clapper start, then create a dedicated
top video track. Source in, source spans, clip-relative curves and Recipe source
remain unchanged. Recipes use shifted Clappers. Existing slots are idempotent.
If a title is selected, an ordinary still-image clip occupies [0, one-frame).
Without a selection the region is empty/black. SetTitleImage replaces/removes
only this stable slot clip. Ordinary edits cannot invalidate the slot's identity,
image or one-frame range; use the dedicated commands to clear/release it.

Release removes only the owned title track and reverses the global shift.
Additional content on that track, content newly placed before one-frame, dangling
references or arithmetic overflow reject the entire transaction. No silent
destruction. Title selection remains available for independent export.
Reserved IDs are caller-generated UUIDs. No physical source is deleted by edits.

UI: Title image menu imports an image, captures the current composited frame,
previews/clears it, exports PNG/JPEG, reserves/releases the first frame. A separate
thumbnail upload to YouTube is still a separate operation; MP4's first frame
does not guarantee platform thumbnail selection.

MCP: existing upload tools register external PNG/JPEG/WebP; SetTitleImage selects
the returned asset. title_image_from_frame captures/selects a full-resolution
owned PNG through the same service as UI. title_image returns independent JPEG
image bytes (1 MiB response limit). Capture is an Edit transaction; image export
is a ReadOnly query. Both pin document/revision/project-path, respect revocation,
and admit one title request at a time. Failed/uncommitted captures are removed;
committed assets remain for Undo/Redo, including after grant revocation.

## Crossfade without a second effect engine

ApplyCrossfade accepts outgoing/incoming clip IDs, positive durationTicks and
two stable opacity-point UUIDs. The incoming Normal video layer must start later
on the same or a higher enabled video track. Clips must touch or overlap.
The incoming duration must cover the fade. If needed the outgoing clip extends
forward through the fade; existing source handles must permit that extension.
No other clip, track, subtitle or audio timing moves. Gaps and insufficient source
handles fail atomically. This is a visual crossfade; audio is unchanged.

The operation creates an ordinary incoming Opacity curve: (0,0), (duration,
baseOpacity). Outgoing pixels remain beneath the incoming source-over layer.
Preview and MP4 export already evaluate these exact native curves. Existing
opacity automation is rejected rather than overwritten; transform/other curves
remain. UI exposes Apply and Clear opacity curve. AI updates/removes the fade
through existing Update/DeleteClipPropertyPoint or SetClipPropertyCurve commands.
Clearing the curve does not shorten the explicitly extended outgoing source.
There is no hidden transition object, fixed preset authority or unrestricted
script execution. AI can compose additional deterministic motion from existing
curves and bounded Recipes; spatial masks/wipes are outside this slice.

## Editor preferences and playback

Ctrl+S writes the current known .fkproj path, opening a picker only on first save.
Ctrl+Shift+S always chooses another path. Current filename/dirty baseline update
only after the existing atomic file writer succeeds.

Window normal bounds/maximized state live in per-user window.json, outside
projects/history. Bounded malformed/unreadable files fall back to defaults.
After HWND creation, the closest monitor's physical work area clamps placement
before restoring maximization; Windows supplies per-monitor DPI. Cancelled close
does not save placement.

|◀ / Home explicitly scrub to zero and reveal it, even if the UI cursor was
already zero. Double triangles step one canonical frame. Stop freezes the device
and retains consumed-clock position, cancelling/joining stale requests.
Timeline follow is presentation only: cross 85% -> re-anchor at 20%, clamped to
scroll range. No per-frame rebuild; only threshold crossings scroll. Pointer
gestures/manual scroll suspend following until the next Play.

## Acceptance

Headless native/editor/history/strict-codec/MCP tests and Windows shell/build
checks protect the contract. Physical Windows interaction, multi-monitor/DPI,
real-media source handles and MP4 visual acceptance use
staging/windows-creation-workflow.md; CI is not physical visual acceptance.
