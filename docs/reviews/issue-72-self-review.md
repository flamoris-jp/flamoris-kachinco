# Issue 72 self review

Scope: one read-only image query; ADR 0017 precedes the Product contract. Native
EditorSession remains authority, TimelineTime supplies nearest-frame conversion,
TimelineEvaluator/SharedFrameRenderer supply existing rendering semantics. No new
project schema, UI controls, commands, evaluator or export filesystem authority.

Reviewed risks: immutable snapshot and final revision/document/path check; single
per-window admission across grant rotation; Core cancellation retains unjoined work;
independent decoder avoids touching playback forward streams or GPU backend;
full-canvas render before bounded downsampling preserves caption/transform semantics;
source/output/layer/encoded-byte limits; bounded encoding stream including WIC
wrapped failures; metadata has no duplicate base64 or source paths. Core 1.3 is a
cross-repository dependency: Core 1.2 flattened image results into text on both
transports. Hub already validates/forwards CallToolResult image content.

Local targeted capture tests and native contracts pass. Linux can compile the WPF
smoke but cannot run WPF; named-pipe external-client tests are blocked by sandbox
AF_UNIX permission. Windows CI must pass full Product/build/codec/packaged-bridge
checks. Actual deployed tunnel/Hub and physical viewer checks remain explicitly
pending in staging/windows-issue72.md. Do not close the Issue from merge alone.

CI #145 (750bc2c) passes all gates: Linux 285, regular/sanitized native contracts,
Windows ABI 48, portrait pixel parity, actual bounded PNG/JPEG, published external
MCP image response, archive and startup. Core 1.3.0 release is verified after NuGet
indexing; Hub #50 CI and actual external HTTP/WebSocket fixture pass. Windows
CMake availability is now checked explicitly after a runner lacked it. Final review
corrects a pre-existing fixed Core 1.2 console label to report the loaded assembly
version and adds a dedicated external-image PASS line; no Product behavior changes.

A final unchanged-Product rerun reproduced the pre-existing continuous decoder
file lock twice (#74). Native destruction terminated the Windows job but waited
only for the launcher. Add a bounded wait for job ActiveProcesses == 0 and a
controlled exited-launcher/live-descendant fixture proving exclusive file ownership,
process exit and immediate file release across eight cycles. Keep the existing
real-codec immediate-delete assertions. This shared owner is also used by capture
cancellation; no sleeps/retries are added to the codec deletion test. Final CI must
verify this prerequisite before merge.

The controlled Windows fixture rejected an accounting-only drain: job accounting
was zero while the descendant process object remained unsignaled. Replaced that
approximation with bounded job-member handles retained before cancellation and a
shared five-second wait for actual process signals. A 256-process job limit bounds
retained handles; snapshot again immediately after termination to include a member
admitted during the first enumeration. Keep the same strong descendant/file tests.

Final PR #73 Product CI #148 passed all gates including that controlled fixture.
Live Hub acceptance then found a separate Core 1.3.0 wire-format defect: context
and project reads failed after successful registration because generic Web JSON
serialized the SDK's unset resultType as null. Python MCP rejects that value.
The earlier separate .NET and Python fixtures did not cover this cross-SDK member.
Core #15 / PR #16 switches the Hub envelope to official SDK JSON options and tests
context/error/image omission, with a .NET-output-to-Python-validator reproduction.
Kachinco consumes the 1.3.1 package fix without changing tools, session or renderer.
Full bundle CI and actual OpenAI tunnel image receipt remain acceptance gates.
