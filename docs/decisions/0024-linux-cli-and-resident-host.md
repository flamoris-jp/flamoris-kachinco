# ADR 0024: Linux CLI and resident host

Status: proposed (Issue #100)

## Authority

`Kachinco.Cli` is an adapter over the existing native-backed EditorSession,
ProjectFileStore, TimelineEvaluator, SharedFrameRenderer, SharedAudioRenderer and
SnapshotExportService. It has no alternate project codec, timeline or compositor.
No project schema changes. The Windows executable and packaging remain unchanged.

## Commands

`project new|info|validate`, `edit apply`, `render frame`, `export`, `serve` and
`catalog` return JSON on stdout; diagnostics/progress go to stderr. Int64 wire
values remain decimal strings. Exit codes: 0 success, 1 runtime/domain failure,
2 invalid invocation/input, 130 cancellation. Unknown flags fail. SIGINT/SIGTERM
cancel work and join media processes before cleanup.

`edit apply` uses the existing MCP edit_batch decoder/boundary, including stable
IDs, expected revisions and 64-command transactions. Import/relink are explicit
probe-backed CLI commands, not unrestricted MCP file access. Relative media are
resolved against the input project. Saving to another directory normalizes
relative media through ordinary RelinkMedia commands before saving. Windows
absolute paths on Linux are reported missing; no basename guessing or implicit
drive mapping. Users relink explicitly, preserving asset/clip identity.

Outputs are new files by default. Source project/media overwrite is forbidden for
render/export. Persistent sessions take an exclusive process-lifetime lock for
their project; CLI mutations take the same lock. Saves are atomic via the existing
store. Remote edits remain in shared Undo/Redo history; checkpoint saves preserve
committed state and explicitly report save failures. A single resident session
opens one operator-selected project; no remote open/arbitrary path tool.
ADR 0026 adds an optional configured per-project workspace, selected at startup,
with separate local scratch and durable MP4 publication.

## Linux text and images

Pango/Cairo rasterize only evaluated caption layers into straight RGBA, then the
native compositor combines them exactly as on Windows. Preserve normalized
anchors, alignment, width, reference-pixel font/outline/shadow sizes, legacy stack
order and multiline text. Linux uses installed Fontconfig fonts (recommended Noto
Sans CJK JP); it never drops captions. Missing glyphs fail visibly. Pango shaping
and WPF metrics differ: identical pixels/line breaks across OSs are not promised.
Timing, styles and coordinates remain identical; physical cross-platform visual
acceptance is separate. No font binaries are redistributed.

FFmpeg encodes frame PNG/JPEG from bounded RGBA; it does not evaluate timelines.
CPU H.264/AAC is the baseline. ADR 0025 adds optional VAAPI encoding; physical
device support/performance and GPU composition are separate acceptance items.

## Resident host

`serve --project ... --hub-endpoint ... --credential-file ... --permission edit`
explicitly authorizes a single headless session. The common MCP boundary and
outbound WSS provider enforce identity, revisions and permission. There is no new
listening network port. A serialized dispatch lane protects snapshot/edit/save
interleaving. Existing tools plus preview, uploads and bounded export jobs share
that session. ReadOnly cannot save, upload, edit or start/cancel exports.

One export job runs at a time with an immutable revision snapshot, cancellation,
status, bounded chunk reads and operator-selected output root. Generated job IDs
address outputs; clients cannot supply paths. Retain one result; remove it on the
next admitted export or shutdown. Export stops on revocation/shutdown and has a
one-hour deadline. No persistent job replay. Explicitly save project authoring
state; exported media are derived artifacts. A reconnect never replays edits.

Updater owns LIME installation, startup, executable updates and retained versions.
The application supplies an immutable Docker archive and repository-owned catalog;
it does not install a second user systemd service. A non-root container owns the
same headless session and private credential file. A loopback-only `/healthz`
reports loaded-process readiness; actual Hub connectivity is a separate domain
acceptance check. Projects/imported media and temporary exports use separate
Updater-preserved data mounts. Existing media roots are mounted read-only.
The manual user systemd installer is a development fallback only and must never
be run alongside the managed instance. Hub endpoint/catalog and service activation
require actual deployment configuration; prepared files are not deployment evidence.
LIME and Mango need distinct Hub routes if used simultaneously.
