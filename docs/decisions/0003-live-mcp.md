# Live editor MCP adapter

Issue #5. Protocol compatibility target: MCP 2025-03-26
([lifecycle](https://modelcontextprotocol.io/specification/2025-03-26/basic/lifecycle),
[tools](https://modelcontextprotocol.io/specification/2025-03-26/server/tools)).
The target is explicitly versioned; no claim of implementing newer protocols.

A small console bridge carries newline-delimited stdio JSON-RPC to the running
Windows editor through a random named pipe restricted to the same OS user.
The editor explicitly starts/stops that endpoint. No TCP listener or cloud service.
The bridge owns no Project, history, evaluator, or command implementation.

Each connected client initializes before listing/calling tools. Project query
returns the durable project envelope plus string revision and transient selection /
playhead supplied by the editor. `edit_batch` dispatches an allowlist of typed
commands through the existing session on the UI dispatcher. Expected revision is
required for mutations; dry-run uses that same path. Undo/redo use the same history.
Long-running export belongs to the editor job adapter; job query/cancel never
mutates the project. Commands contain decimal-string ticks, stable UUIDs and exact
enum names. Invalid or unknown fields are rejected at the typed wire boundary.

Messages are bounded to 4 MiB UTF-8 per line. Oversized/invalid messages fail without
project edits. Closing the editor stops the endpoint and its jobs. Native stdio
clients must use the displayed pipe name; no arbitrary editor-instance selection.
