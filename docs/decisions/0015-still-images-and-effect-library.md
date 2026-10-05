# ADR 0015: Still images, visual automation and reusable effects

Status: Accepted for implementation (Issues #62–#64)

## Authority and effects principle

Prefer small deterministic primitives composed by AI through MCP or restricted
Recipes over an expanding hard-coded named-effect catalog. Named presets are
inspectable compositions. UI controls share the same native commands and history.
Ordinary visual motion stays editable; richer Recipe programs compile through the
existing bounded worker to rendering IR and generated ordinary clips.

## Still images and project compatibility

Append `Image` to MediaKind (native/wire value 2); existing values remain unchanged.
PNG/JPEG/WebP must pass IMediaProbe with actual container/codec and dimensions.
Image assets carry a five-second default insertion duration, not a source EOF.
Image clip source ranges remain checked nonnegative Int64 ranges but may extend
beyond that insertion hint. Visual tracks accept Image and Mov. Images decode
the first frame at source time zero through the existing RGBA decoder; alpha,
frame-space fit/pad and the native compositor remain shared with video.

Appearance gains `automation`: at most six property curves, each containing
up to 4096 stable clip-scoped point IDs, signed clip-relative ticks, and finite
absolute values. Properties: X, Y, ScaleX, ScaleY, RotationDegrees, Opacity.
Scales are positive; opacity is [0,1]. Linear interpolation with held endpoints
is a primitive: AI can author arbitrary sampled curves instead of choosing from
a closed ease/effect menu. Empty curves use constant appearance values.
Native add/update/delete-point commands preserve constant edits, shared history
and dry-run/revision behavior. Trim/split shifts curve ticks with checked math
exactly like volume curves; moving a clip does not retime its curves.

Schema v4 is selected only for Image assets or visual automation. v1/v2/v3 inputs
migrate to empty appearance automation; existing projects retain v2/v3 output.
v4 requires appearance automation and volume arrays, strict known fields and
decimal-string ticks. Older schemas reject Image instead of silently accepting
a new token. Old applications reject v4 explicitly; users should keep backups.
Existing native timeline ABI structures stay unchanged. Additive curve setters
and signed-parameter sampling share native interpolation for preview/export and
visible-interval capture. Capability 8192 raises the managed requirement to 16383
so an older DLL is rejected during startup, before calling new exports.

## Effect Library

Application preference `Effect Library Path` defaults to `AppContext.BaseDirectory/library`.
Relative paths resolve against that executable directory, never cwd. Preference
is outside project/history, persisted in the user settings directory. Switching
roots never moves/deletes items. Updates replace application binaries only;
library backup/copy remains explicit. Do not silently fall back from an unwritable
configured root. NAS coordination uses optimistic item version checks and atomic
same-directory publication on filesystems supporting rename and locking; root is user-selected, never supplied by MCP calls.

Versioned JSON items contain a stable ID, name/description, normalized automation
templates (time fractions [0,1]), parameter defaults, and optional restricted
Recipe source/seed. Parameters duration and intensity adapt composition to a
compatible target. Intensity blends sampled absolute values from the target's
constant property; generation validates all resulting ordinary commands.
Applying replaces only properties present in the template, with fresh point IDs,
and commits one ordinary native transaction. Recipe items use the existing
compiler/RecipeGenerationService and explicit Clapper/track/output contracts.
No saved code executes during list/get/load. Recipe library apply binds a copied
program to an existing Clapper; the ordinary explicit Generate/Regenerate action
renders its output. Current Recipe API remains the literal-only text/particles slice. Validate definitions and revalidate
programs on save/apply. Unknown schema/API versions fail explicitly; never perform
destructive implicit migration. Per-clip Recipe/provenance remains independent
of mutable library assets; applied curves/programs are copied into project state.

Local library list/get/save/update/delete tools use explicit MCP operation kinds;
apply uses the shared edit transaction permission, expected revision and history.
Library tools require an explicit per-launch desktop opt-in plus existing host
envelope/revision and ReadOnly/Edit permissions; no arbitrary path grant is added. Safe filenames
derive solely from stable GUIDs; use bounded JSON, reject links/traversal and
surface missing, invalid and unwritable root errors. No marketplace/cloud required.
