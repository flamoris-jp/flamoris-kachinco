# Linux CLI and LIME resident editor (Issue #100)

The Linux CLI uses Kachinco's shared native editing/evaluation/composition and
FFmpeg export engine. It adds a Pango/Cairo caption adapter and Linux VAAPI H.264
encoding. Native composition, captions, decoding and audio remain CPU work.
Physical AMD device/driver compatibility and performance remain unverified.
See ADR 0024 for lifecycle, output, caption and project compatibility boundaries.

## LIME deployment through Updater

**Updater is the normal installation/update path.** The application owns its
Docker image, immutable `image.tar`, release catalog and domain setup. Updater
verifies digests, stages/starts the container, preserves settings/projects/data,
checks local process health, and manages later compatible executable updates.
Do not install a separate user systemd instance on the same host/project.

`product/packaging/Dockerfile.linux` builds Linux amd64 with FFmpeg, Pango/Cairo,
Japanese fonts and the shared native runtime. The non-root application entrypoint
creates a project only if absent and starts the existing CLI host. The catalog
generator `product/packaging/package-updater.py` produces the recipe and binds
archive SHA-256, exact image ID and source revision. No credentials are published.
`release/updater.json` records the proposed CLI release identity; this source file
does not establish that a release has been published.

After a reviewed release is published, register its `catalog.json` URL with the
LIME Updater, select `flamoris-kachinco-cli`, and supply:

- a separate Hub WSS endpoint and connector credential;
- permission `edit` or `readOnly`;
- encoder `vaapi` (required GPU), `auto` (GPU with reported CPU fallback), or `cpu`;
- the actual `/dev/dri/renderDNNN` node (`DRM_RENDER_NODE`, empty for CPU);
- the existing media directory to mount read-only at `/media`;
- an unused local health port (recipe default 8772).

Without a workspace configuration, the project lives in `/data/projects/resident.fkproj` and temporary export jobs in
`/data/exports`; both are persistent application data mounts, outside executable
cleanup. Uploaded media live beside the project. Source media from the configured
host directory are addressed through `/media`. Download completed export artifacts
to the operator's chosen permanent media destination before starting the next job.
The health listener binds only `127.0.0.1` through host networking. HTTP 200 means
the project/CLI process is ready; it does not certify Hub registration or GPU export.
Complete Updater's setup/start/verify flow, then verify Hub tools and an actual export.

### Configured per-project storage (CLI 0.1.1)

Create the optional `workspace.json` in the existing retained project data mount:
host `${application-root}/projects/workspace.json`, container
`/data/projects/workspace.json`. Updater preserves that directory on compatible
executable updates. The file contains storage settings, **no credentials**:

```json
{
  "projectsRoot": "/data/projects/workspace/projects",
  "projectName": "my-movie",
  "temporaryExportRoot": "/data/exports",
  "requiredMount": "/data/projects/workspace"
}
```

Paths in this file are visible **inside the container**. The example requires an
administrator-provided CIFS/NFS mount at the named location and an existing
`projects` directory, accessible for read/write/traversal by UID/GID 10001.
Mount the intended share beneath the existing host project-data directory before
Updater creates the container. Verify `/proc/self/mountinfo` in the container:
Docker's private mount propagation means a later host mount is not sufficient;
restart of an existing container is not proof that a newly mounted share is
visible. A missing configured network mount blocks startup/save/publication.
Updater does not mount SMB or change NAS ownership/ACLs. Credential handling,
host mount units and the exact server/share path belong in deployment operations.
For ordinary local storage, omit `requiredMount`; do not omit it merely to get
past a missing NAS mount. Symlinked paths and overlapping scratch/project roots
are rejected. Create the configured projects root explicitly first.

The selected folder contains `project.fkproj`, `media/` and `exports/`. A new
project is created only when absent and permission is `edit`; existing invalid
projects fail rather than being reset. New uploads register relative `media/`
references. External `/media` assets are not copied or automatically relinked.
Change `projectName` or `projectsRoot` in the file, then stop/restart gracefully
to switch projects; the previous project and all its media remain untouched.
There is no remote arbitrary-path open tool or automatic data migration.

Completed sequence exports are copied from local scratch to `exports/` using
exclusive staging, flush and non-overwriting rename. File names contain the
sequence UUID, revision and Job UUID. `export_status` adds `publicationStage`,
`publishedPath` and `downloadAvailable`. `Completed` with publication configured
means the durable copy succeeded; status may show `Publishing` while copying.
Later Jobs and shutdown remove only normal local Job files, never durable MP4s.
On publication failure/cancellation after encoding, the Job fails/cancels visibly
and keeps its encoded scratch `<job-id-without-hyphens>.mp4`, including at shutdown.
Recover it through `export_read` after restoring the mount or through operator
access to scratch. Failed-publication files require explicit operator cleanup;
they are not automatically replayed or deleted by later Jobs.

Manual CLI service users can instead supply `serve --workspace-config <file>`
with the usual endpoint/credential/permission arguments, replacing `--project`
and `--output-root`. Storage settings are read once at startup. See ADR 0026.

GPU installation requires the matching Updater implementation with typed
`drm_render_node` support. Earlier Updater releases cannot pass this device merely
by accepting a new app catalog. The installer passes only that verified DRM render
node (`rw`) and its observed non-root device group; it does not use privileged
mode, pass display/control devices or alter host driver permissions. Kernel/firmware
and Mesa/libva compatibility are physical-host prerequisites. A required `vaapi`
service performs a real bounded initialization encode before becoming healthy.

Repository CI prepares and verifies release candidates; publication and physical
LIME acceptance remain separate. No manual SSH deployment is required once the
catalog and configuration are available to Updater.

## Developer build on Ubuntu x64

Install .NET 10 SDK, CMake >=3.20 and a C++17 compiler. Runtime prerequisites:

```bash
sudo apt-get install build-essential cmake ffmpeg libpango-1.0-0 libpangocairo-1.0-0 libcairo2 fonts-noto-cjk python3
gh auth status
gh repo clone flamoris-jp/flamoris-kachinco
cd flamoris-kachinco
bash product/packaging/publish-linux.sh
```

For later authenticated Git pull/push, run `gh auth setup-git`. If authentication
is missing, run `gh auth login` as the normal user. Do not clone using sudo.
Publish is self-contained; running the published bundle does not require .NET SDK.
FFmpeg/ffprobe, Pango/Cairo, Fontconfig and installed fonts remain system dependencies.

## CLI

```bash
K=./artifacts/linux/FLAMORIS-Kachinco-linux-x64/kachinco
"$K" project new --output movie.fkproj --name Movie
"$K" project info movie.fkproj --json
"$K" project validate movie.fkproj --json
"$K" media import movie.fkproj --source /srv/lime/generated/image.png --output imported.fkproj
"$K" edit apply imported.fkproj --commands edits.json --output edited.fkproj
"$K" render frame edited.fkproj --time 1.5 --output frame.png
"$K" gpu check --vaapi-device /dev/dri/renderD128
"$K" export edited.fkproj --output movie.mp4 --encoder vaapi --vaapi-device /dev/dri/renderD128
```

Use `--sequence UUID` for a multi-sequence project. `project new` also accepts
`--width`, `--height`, `--duration` (decimal seconds); dimensions use the existing
1920x1080 or 1080x1920 domain presets and FPS defaults to 30/1.
Image output is PNG/JPEG, limited to 1 MiB and 1920-pixel width/height. Composition
is bounded to 4096 per edge and 8,294,400 pixels. Outputs must be new files. Writes
publish atomically. Ctrl+C/SIGTERM cancel export and clean unpublished output.
stdout carries JSON results, stderr carries diagnostics/progress. Exit 0=success,
1=domain/runtime error, 2=invalid invocation/input, 130=cancelled.

`vaapi` requires real initialization and never silently changes to CPU. `auto`
may retry a failed hardware video once from frame zero through libx264. Progress
and result metrics report the actual encoder, device, fallback reason and CPU
composition counts. This is GPU compression; it does not claim GPU composition,
zero-copy rendering or measured speedup. GPU composition remains separate work.

`edits.json` is the existing MCP edit_batch input: `{ "commands": [ ... ] }` with
typed command names, stable UUIDs, camelCase fields and decimal-string Int64 ticks.
The same schema is in `kachinco catalog`. For example:

```json
{"commands":[{"type":"SetTrackEnabled","sequenceId":"SEQUENCE_UUID","trackId":"TRACK_UUID","enabled":false}]}
```

Media imports/relinks probe files before native commands. For a Windows project,
copy assets to LIME, inspect missing asset IDs, then explicitly relink each asset:

```bash
"$K" media relink windows.fkproj --asset ASSET_UUID --source /srv/lime/generated/source.mp4 --output relinked.fkproj
```

There is no automatic drive-prefix or basename guessing. Save As preserves relative
media meaning via ordinary RelinkMedia commands. Windows can reopen the resulting
project but needs paths accessible there or explicit relinking. Linux text uses
installed fonts, falling back through Fontconfig; Yu Gothic maps to Noto Sans CJK
JP. Missing glyphs fail visibly. WPF/Pango line metrics and antialiasing differ;
cross-platform pixel equality and physical visual acceptance remain unverified.

## Manual resident installation (developer fallback only)

The following is for local development outside Updater. It is not the LIME rollout
procedure; do not use it alongside an Updater-managed instance.

```bash
./artifacts/linux/FLAMORIS-Kachinco-linux-x64/install-linux-user.sh
sudo install -d -o "$(id -un)" -g "$(id -gn)" /srv/lime/generated/kachinco
```

The installer stages a versioned executable and user systemd unit; it preserves
existing project/configuration and does not start an unconfigured connector.
Edit `~/.config/FLAMORIS/Kachinco/service.json` for the project, output directory,
permission and real Hub endpoint. Put the connector credential in `hub-token`
using an editor; keep both files mode 0600. Never put the token in shell history,
Git, screenshots or chat. The default endpoint is a deployment convention, not
evidence that the route exists. An empty token cannot connect.

```bash
systemctl --user enable --now flamoris-kachinco
sudo loginctl enable-linger "$(id -un)"
systemctl --user status flamoris-kachinco
journalctl --user -u flamoris-kachinco -n 50 --no-pager
```

One service holds one project with an exclusive lock. Successful edits checkpoint
automatically, with shared Undo/Redo retained in memory. `project_save` explicitly
confirms saving; a failed automatic save reports `checkpoint_failed` and dirty
state. Graceful shutdown saves dirty state and cancels/joins export cleanup. Host
restart reloads the last saved project; history/jobs are transient. ReadOnly
cannot edit/save/upload/start or cancel jobs. Remove the `.lock` *only after*
confirming no process owns the project; normally it can remain as an empty file.

## Separate Hub route

Keep Mango's `kachinco` and LIME's `kachinco-lime` descriptors distinct. Export the
reviewed tools with `kachinco catalog` (also included in the Linux bundle). On the
Hub host, generate a separate reverse-WebSocket descriptor using
`product/packaging/create-linux-hub-descriptor.py` and that catalog. It uses product
ID `flamoris.kachinco`, namespace `kachinco_lime`, connector credential environment
variable `KACHINCO_LIME_CONNECTOR_TOKEN`, and route `/desktop/kachinco-lime`.
Configure the same token locally and in the Hub's existing protected environment,
then reload the Hub through its normal deployment procedure. Do not overwrite
Mango's descriptor. Actual Hub acceptance and tool refresh are deployment checks.

New host tools: `project_save`, `export_begin`, `export_status`, `export_cancel`,
`export_read`. Existing project/edit/history/preview/upload contracts are reused.
Every mutation requires the ordinary identity/revision guard. Export jobs pin an
immutable snapshot and report its revision. One job at a time; one retained result;
one-hour timeout. Read in <=196608-byte chunks with decimal-string offsets. The
next export/shutdown deletes the prior result: download it first for durable use.
With a workspace configured, the durable published copy is retained independently;
only its normal scratch copy follows that cleanup rule.
Source projects and media are never cleanup targets. There is no inbound MCP
port; the Updater container has only its loopback health listener.

## Deployment acceptance

Run `python3 test/cli/smoke.py <published-kachinco>` and the headless tests, then
verify service restart, live Hub edit/preview/export/download, Windows reopen and
styled Japanese captions on real LIME material. GPU benchmarks and font/pixel
parity remain separate gates. A successful development-container test does not
mean the LIME service has been installed or connected.
