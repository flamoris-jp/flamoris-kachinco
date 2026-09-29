# ADR 0009: shared native evaluation, composition and playback policy

Status: proposed for #34; accepted only after parity and review.

## Authority and boundary

C# EditorSession, commands, validation and project formats remain the only persistent
editing authority until #35. Each immutable evaluator projects its validated sequence
into a native owned evaluation snapshot. Native code sorts track/clip/caption inputs,
selects half-open active ranges, maps source time and evaluates effective parameters.
Managed IDs, text and domain records are projections keyed by native result indices;
they do not run an alternative active-range evaluator.

Core may reference the BCL-only Native adapter assembly. Native does not reference
Core, Infrastructure or WPF. The deployment already requires the runtime DLL. Native
snapshot handles use SafeHandle ownership; borrowed arrays exist only during calls.
No native pointer references managed project memory and no callback crosses the ABI.

Preview and export keep the same TimelineEvaluator/SharedRenderer entry points, now
adapting native evaluation, RGBA composition and PCM accumulation. WPF caption
rasterization remains desktop integration; its pixels use the same native compositor.
Normal/Screen use current straight-alpha encoded SDR equations and byte rounding.
Transform order, pixel centers, nearest sampling and quality scaling remain unchanged.
The native parameter evaluation boundary admits deterministic clip-local time without
adding automation persistence or changing #30's UI/Undo/MCP scope.

## Playback policy

Native playback state owns request generations, latest-wins acceptance, consumed
sample clock mapping, bounded video/audio scheduling and pause/end frame targets.
The managed host performs asynchronous decode and Windows device I/O from those
decisions. Dispatcher callbacks and rendering delays are never clocks. Device sample
frames are the sole playback clock input; UI/MCP see the same projected position.

Pause first freezes the physical audio device, captures its consumed sample count,
then supersedes in-flight playback work with the exact frozen target frame. A stale
decode completion cannot replace that frame. The sequence-end cursor keeps the end
position while displaying the last canonical output frame. Resume starts a bounded
new decode window from that frozen position; it cannot accumulate old device queues.

## Validation and evidence

Preserve existing headless, real-codec, Windows raster/package and external MCP gates.
Add native/managed conformance for ordering, half-open boundaries, source offsets,
muted/disabled values, Screen/transforms, PCM clipping and clock/generation bounds.
Add a delayed-decoder clip-boundary pause regression for #28, including stale results,
repeat Play/Pause and end behavior. Seeded numerical and boundary oracles belong in
tests only. Production retains one evaluation/composition path.

Record reproducible before/after workload measurements with OS, machine, fixture,
quality and revision. Decode throughput is not physical audible/visible acceptance.
Any outstanding subjective Windows A/V acceptance must be explicitly identified;
do not close #28/#30 or the migration parent based on unrelated smoke results.
