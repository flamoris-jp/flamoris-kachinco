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
