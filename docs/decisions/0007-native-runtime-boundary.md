# ADR 0007: staged native runtime and C ABI

Status: proposed for Issue #32; accepted when its PR is merged.

## Authority and migration order

Issue #31 is delivered in order #32 → #33 → #34 → #35 → #36. In #32,
C# EditorSession, TimelineTime, evaluator, codecs, persistence and history remain
production authorities. Native time functions are conformance candidates only.
The runtime object is a resource/ABI handshake, never an editable session.
Subsequent phases must prove parity before switching a production caller.

## Boundary

`product/native` builds `Kachinco.Native.Runtime` with CMake/C++17. Its public
header is C-compatible: cdecl, fixed-width integers, opaque runtime pointers,
explicitly sized POD outputs and numeric status codes. No STL, exceptions, bool,
platform-sized integers, allocator ownership or C++ object layout crosses the ABI.
ABI version 1 requires an exact match. Future incompatible changes get a new ABI
version; capabilities advertise only implemented operations, never roadmap items.

A successful create returns one native-owned object; release it exactly once with
`kn_runtime_destroy`. Null release is harmless. A live pointer belongs to its
creator and must not be fabricated, copied as an independent owner, or used after
release. The managed adapter owns it with SafeHandle; P/Invoke pins the handle
through each call, including races with Dispose. Runtime operations are immutable
and thread-safe. Native functions catch allocation/other C++ exceptions. Error
outputs are zeroed and status messages are fixed UTF-8 static storage (borrowed,
never freed). There is no process-global last-error buffer and no caller data in
messages. Stable status codes are machine authority, not localized text.

Value structs use sequential natural 8-byte alignment, fixed member order and a
size argument checked before writing. V1 supports 64-bit Windows and Linux only.
Callers own POD storage; native code never retains input/output pointers. The
media/timeline value round-trip is a transport test, not metadata validation or
registration. Persistent asset identities never become native memory addresses.

## Exact time conformance

The candidate uses the existing 35,280,000 ticks/second, reduced rational FPS in
[1,240], nonnegative inputs, round-half-up and half-open frame/sample counts.
Portable two-word unsigned arithmetic protects intermediates up to 128 bits on
MSVC and GCC without floating point or a third-party dependency. Output overflow
returns a separate status. Decimal seconds remain a managed input adapter in this
phase; do not replace decimal conversion with binary floating point.

Tests derive expected values from current C# Product (BigInteger intermediates),
including near Int64 limits, uncommon FPS, invalid inputs and overflow. The native
unit suite separately checks C layout, diagnostics, output clearing and lifetime.
This becomes the reusable parity pattern for later phases; it does not claim media,
evaluation or editing parity before those implementations exist.

## Build and distribution

The dedicated managed project builds the runtime with CMake before its own build,
and publishes/copies the library transitively to app and headless test output.
Windows uses x64 Visual Studio Build Tools (Desktop development with C++), CMake
3.20+ supporting the installed Visual Studio version, and the static MSVC runtime, avoiding a separate VC redistributable dependency.
Linux uses a C++17 compiler and CMake for headless conformance only. No native library
enters the independent MCP bridge. Build directories are configuration-specific.

Startup performs the version/capability handshake and a create/dispose smoke before
opening WPF. Missing, incompatible and wrong-architecture libraries produce an
explicit startup message and log entry. No silent fallback to an unverified DLL.
The existing Windows package/startup/shared-MCP jobs remain the packaging gates;
managed parity tests run on both Linux and Windows. Physical A/V acceptance is
still required at the playback cutover and cannot be claimed by a startup test.
