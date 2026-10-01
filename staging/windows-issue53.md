# Issue #53: physical preview acceptance

Automated preparation/queue/pixel evidence does not establish physical audible and
visible playback. Keep Issue #53 open until representative local media is checked.

Use a complete fresh Release build/portable bundle: native capability 2048 is required.
Keep the same media, project and machine when comparing baseline and this change.
Logging defaults to `%LOCALAPPDATA%\FLAMORIS\Kachinco\logs\kachinco.log`.

For Full, Half and Quarter, record a 10–30 second cold and warm playback of adjacent
clips, including different assets and a clip with audio. Capture `preview.performance`
entries and the final session summary: video/audio cache hits/misses and mean cost,
preparation average/recent p95/max, conversion, presentation, queue depth/maximum,
requested/presented tick, drops and underruns. Compare averages/p95 over the same
window; counters are cumulative within that controller/source, so subtract start values.

- Confirm playback remains near real time and audio matches visible events.
- Pause at and around clip boundaries, repeat Play/Pause, then scrub, Stop and resume.
- Switch quality while Playing/Buffering; old pixels/audio must not return.
- Edit inside/outside the queued range; the former re-primes, the latter keeps transport.
- Test silence, overlapping tracks, captions/transforms, and the final sequence frame.
- Under load, skipped video must preserve ordered audio and newest available pixels.
- Inspect working set during repeated Play/Pause/quality changes for sustained growth.

The updated Windows smoke captures generated 1080p Full/Half/Quarter real-codec
preparation and device metrics when an audio device exists. A runner without an
audio device reports that absence. Viewer smoke verifies actual BGRA pixels and
WritePixels metrics. Review those automated results separately from this checklist.
