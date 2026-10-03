# ADR 0014 — Clip volume curves and monitoring preference

Status: Accepted for implementation of #30 under the owner's instruction to design,
implement, review and merge the remaining Kachinco features.

## Persisted volume

`AudioProperties.Gain` remains the constant clip gain [0,16]. A bounded array of
`VolumePoint(id, tick, multiplier)` supplies an additional linear envelope [0,16].
No points means multiplier 1. Before/after the first/last point, hold its value.
Final gain is constant gain × envelope; only the final PCM mix clips to [-1,1].

Ticks are signed Int64 in Kachinco's canonical 35,280,000-tick timebase, relative
to the clip start. Points may lie outside the active range: trimming never silently
deletes authored keys and extending can reveal them again. Changing source-in shifts
every key by old-source-in minus new-source-in, preserving the curve on the same
source samples. Moving/reordering a clip keeps local ticks unchanged. Splitting keeps
the left curve and copies it to the right with ticks shifted by the split offset.
This exactly preserves interpolation at the split without synthesizing keyframes.

Point IDs are stable and **scoped to their clip**; the full mutation identity is
sequence/clip/point. Copy/split therefore preserves key identity within each new clip
without colliding with any global Project/asset/clip identity. IDs must be nonempty
and unique within a curve. Tick order is strictly increasing; duplicate times are
rejected. Maximum 4096 points per clip, finite multiplier 0–16, checked tick shifts.
Add/update/delete are explicit native typed commands, atomic and revision-qualified.
`SetClipProperties` changes constant gain/mute while preserving the curve.

## Compatibility

Frozen v1/v2 input accepts only the original gain/muted shape, migrating to an empty
curve. A project containing any curve writes v3 with a required `volumePoints` array
on every clip's audio, using decimal-string ticks. Projects without curves continue
writing byte-compatible v2 structure. A v3 project can downgrade to v2 only after
all curves are explicitly removed. Reject unknown/missing/duplicate fields and
unsupported versions; never silently erase curves to load into an older app.

## Evaluation

One immutable native timeline holds the validated curves. Instant queries and
per-sample PCM mixing use the same linear envelope and canonical sample conversion.
Preview/export share `SharedAudioRenderer`; a block's starting gain must never be
applied to the entire block when its curve changes. Cache signatures include the
complete contributing curve so equal starting gains with different slopes differ.
Add capability 4096; managed required capabilities become 8191 to reject old DLLs.
Existing ABI struct layouts remain unchanged; curve setup/mix are additive exports.

## Monitoring and UI

Master monitoring gain [0,1] is an editor preference under LocalApplicationData,
not Project/Sequence state or Undo history. Save a bounded validated atomic JSON
preference. The transport offers a compact percentage slider clearly labeled
monitoring; it affects the Windows preview output session, including queued audio,
without changing PCM caches or exported audio. New playback outputs inherit it.
Windows uses waveOutSetVolume on its output handle; on supported modern Windows
this is application-session volume, not the system master volume.

The Audio Inspector provides points in clip-relative seconds and percentage
multiplier, add at playhead, edit time/value, delete, plus one-second (or half-clip)
fade-in/out convenience actions implemented as ordinary point commands. Each action
is one transaction and one Undo. Time/curve errors remain actionable. A point list
is the first editing surface; direct timeline envelopes can follow separately.

Windows physical audible/DPI acceptance remains #42. Official Windows references:
https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/nf-mmeapi-waveoutsetvolume
and https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-events-for-legacy-audio-applications
