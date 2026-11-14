# Kachinco Linux CLI 0.1.3

Adds timed editing notes from PR #131. EditNote tracks reuse ordinary guarded caption commands, shared history and native evaluation. Notes persist in projects but are excluded from video, frame/title capture and SRT export. Windows main also provides gold M tracks, text/list editing and a switchable preview overlay; this release asset is the headless Linux CLI.

Projects containing editing-note tracks use conditional schema v9, including empty or disabled note tracks. Older applications cannot open v9; update each reader before opening these projects. Projects without note tracks retain their prior conditional v2–v8 format. The updated managed host requires the matching native runtime packaged in this release.

Hub PR #62 provides the matched EditNote enum descriptor. After updating each producer, refresh only edit_batch in its existing deployed Hub catalog, preserving all other tools, namespace, connection settings and credentials. Merging Hub source alone does not refresh a live catalog.

Updater updates from CLI 0.1.0, 0.1.1 or 0.1.2 preserve existing settings, workspace, media, exports and mounts. This release does not relocate data or rewrite projects automatically. Retain the documented workspace.json and NAS mount guards. VAAPI requires typed DRM render-node support in Updater 1.0.15 or a compatible successor; actual hardware performance remains deployment acceptance.

Publication requires product gates and real Docker/Updater CPU install/start/export acceptance. Verify SHA256SUMS and follow the supplied linux-cli.md. Downloading or publishing does not install the service or update the live Hub catalog. Physical Windows interaction, VAAPI hardware and NAS permissions remain separate acceptance.
