# ADR 0025: Linux VAAPI encoding and independent Hub identity

Status: proposed (Issue #100).

Linux `auto` probes H.264 VAAPI with a real bounded initialization encode on the
operator-selected `/dev/dri/renderDNNN` node. `vaapi` is explicit and requires
hardware: initialization or runtime failure reports failure rather than silently
using the weak host CPU. `auto` may recover once from video encoder failure by
replaying the immutable source through libx264; cancellation, renderer/audio/mux
failure never triggers this replay. Windows Auto/NVENC policy remains unchanged.
Device arguments are typed paths, never arbitrary FFmpeg arguments.

The shared evaluator/native CPU compositor and Pango caption rasterization remain
unchanged. This slice accelerates video compression, not all rendering/decoding.
Results explicitly report the actual encoder, requested VAAPI node and CPU
composition. No zero-copy or measured speed claim. Native Linux GPU composition
remains a separate acceptance/work item; do not label CPU composition as GPU.

Updater must pass only the selected DRM render node and its observed group to
the non-root container. An application catalog cannot make the old Updater's
unextended Docker contract pass devices. A narrow typed Updater capability is a
prerequisite; no privileged container or arbitrary device mapping is introduced.
Mesa/libva user-space drivers and FFmpeg are application image dependencies;
host kernel/firmware and physical codec support require actual host validation.

Windows keeps `kachinco` and its existing connector. Linux uses descriptor ID
`kachinco-lime`, namespace `kachinco_lime`, a separate credential and separate WSS
route. Equal product IDs do not merge sessions: Hub routes connectors by descriptor
and tools by namespace. Both can be registered simultaneously. Tests validate the
combined catalog with the actual Hub parser; live registration remains deployment
acceptance. No project schema or persistent command contract changes.
