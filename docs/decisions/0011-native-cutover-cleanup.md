# ADR 0011: complete the native engine cutover

Accepted for #36, following ADRs 0007–0010. The owner deferred physical Windows
A/V acceptance until the migration finishes; that evidence remains a separate gate.

C++ is the production authority for editing/validation/history, project v1/v2 codec,
canonical frame/sample time, evaluation, composition/mix, media buffers/cache and
playback policy. Managed TimelineTime delegates rational conversions to the same
native functions used by playback/export; native also validates reduced FPS.
Decimal seconds, display rounding, input normalization and transient UI geometry
remain managed adapters. Preserve existing argument/overflow exception behavior.
The additive FPS/inverse-frame queries require capability 1024 (complete required mask 2047),
so a stale phase-3 DLL fails the handshake before editing.

Remove unused managed file DTO/codec implementations. Preserve frozen managed
BigInteger time and editing/codec/evaluation implementations only under test as
independent compatibility oracles. UI bitmap caches retain managed WPF object
ownership; they do not store decoded production frame/audio authority.

Retain WPF layout/raster/audio device, async media protocol and atomic filesystem
I/O, MCP transport and the restricted Recipe worker in their established adapters.
No hardware acceleration, embedded libav or C++ UI rewrite is claimed. The final
portable bundle includes matching adapter/runtime, third-party licenses, build
identity and the post-migration physical checklist; its bridge contains no engine.
