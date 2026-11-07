# Kachinco Linux CLI 0.1.2

Aligns the LIME distribution with reviewed main through PR #127. Since CLI 0.1.1,
this includes headless native Kinetai commands and embedded KineticText/Glyph
composition rendering, bounded animation scripts, shared renderer/MCP updates,
and the shared subtitle import/audio preparation helpers. The caption inspector,
playhead context menus and automatic MP4 audio extraction UI in PRs #126–#127 are
Windows editor features; Linux remains headless and edits captions through the
existing ordinary guarded commands.

No workspace relocation, credential rotation or automatic project rewrite is
part of this update. Existing projects, media, exports, mounts and runtime
settings are retained by Updater. New Kinetai content uses the documented
conditional project format; older builds cannot read newly introduced content.


Adds optional `workspace.json` for a selected per-project directory containing `project.fkproj`, portable uploaded `media/`, and durable completed `exports/`. Switch projects by changing configuration and gracefully restarting. CIFS/NFS mount guards prevent local fallback when the configured NAS mount is absent. Failed publication is reported and retains the encoded local file for recovery.

Compatible Updater update from CLI 0.1.0 or 0.1.1 preserves existing mount/settings/environment and project data. Existing deployments retain the legacy behavior until explicitly configured; no data is relocated. NAS mounting and container visibility require separate operator preparation. The release includes the immutable Docker image, digest-bound catalog, reviewed tool catalog and independent Hub descriptor generator.

Requires an Updater release containing typed DRM render-node support (1.0.15 or a compatible successor) for VAAPI GPU compression. Explicit VAAPI performs a real initialization encode and fails instead of silently using CPU. Composition, captions, decoding and audio remain CPU work; physical device compatibility and measured speed are deployment acceptance.

Use a distinct Hub descriptor, namespace and connector credential for Linux. Preserve the Windows registration. Configure the read-only source-media mount, project storage, selected GPU render node and loopback health port in Updater. No service is installed merely by downloading this release.

Product gates and real Docker/Updater CPU install/start/export acceptance gate publication. CI does not certify physical VAAPI, NAS permissions/locking, or live Hub connectivity. Verify the published SHA256SUMS and follow the supplied linux-cli.md. Without workspace configuration, temporary export results are removed by the next admitted export or shutdown; download completed output first.
