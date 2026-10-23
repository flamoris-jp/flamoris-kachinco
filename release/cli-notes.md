# Kachinco Linux CLI 0.1.0

Initial Linux amd64 headless CLI and resident MCP Hub host, installed through FLAMORIS Updater. Includes the immutable Docker image, digest-bound catalog, reviewed tool catalog and independent Hub descriptor generator.

Requires an Updater release containing typed DRM render-node support (1.0.15 or a compatible successor) for VAAPI GPU compression. Explicit VAAPI performs a real initialization encode and fails instead of silently using CPU. Composition, captions, decoding and audio remain CPU work; physical device compatibility and measured speed are deployment acceptance.

Use a distinct Hub descriptor, namespace and connector credential for Linux. Preserve the Windows registration. Configure the read-only source-media mount, project storage, selected GPU render node and loopback health port in Updater. No service is installed merely by downloading this release.

Product gates and real Docker/Updater CPU install/start/export acceptance gate publication. CI does not certify physical VAAPI or live Hub connectivity. Verify the published SHA256SUMS and follow the supplied linux-cli.md. Temporary export results are removed by the next admitted export or shutdown; download completed output first.
