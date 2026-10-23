# ADR 0026: Configured resident project workspaces and durable exports

Status: proposed

The resident CLI may opt into an operator-owned `workspace.json`. It selects an
absolute `projectsRoot`, a single portable `projectName`, and a separate absolute
`temporaryExportRoot`. Unknown/duplicate fields, traversal, overlapping roots and
symlinked workspace paths fail. The projects root must already exist. Changing
the file takes effect after a graceful restart; it never moves existing data.
Without this configuration the 0.1.0 single-project behavior remains unchanged.

Each selected directory contains `project.fkproj`, `media/` and `exports/`.
Opening an absent project creates the ordinary native-backed project/sequence
under the same exclusive project lock; existing malformed projects are never
replaced. Edits, history, save and rendering still use the shared session.
Uploads register project-relative media paths so the directory can be reopened
elsewhere; external read-only media references are not copied implicitly.

For SMB/NAS storage, `requiredMount` names the existing network mount enclosing
the projects root. Linux checks the actual mount table for CIFS/NFS before startup
and session dispatch/save/publication. Missing mounts fail instead of creating a
local substitute directory. No credentials, mount commands, privileged container,
or host-specific NAS address belongs in this public application configuration.
Mounted-filesystem permissions and SMB locking/rename guarantees require real-host
acceptance. A network disconnect can still cause an ordinary I/O/save failure.

Encoding stays in the separate local Job directory. A completed MP4 is copied to
an exclusive temporary file in the selected project's `exports/`, flushed, then
renamed without overwrite. Names include sequence ID, revision and Job UUID.
Cancellation/failure removes only the unpublished copy. Successful durable files
are never Job cleanup targets. Publication failure reports a failed Job with an
explicit diagnostic, retains the encoded local file (also on shutdown), and
allows bounded `export_read` recovery. It is not silently reported as successful.
Failed-publication files require operator recovery/cleanup; there is no replay or
unbounded background retry. Later successful exports cannot erase them.

Updater keeps its existing mount/settings/profile contract. The entrypoint reads
optional `/data/projects/workspace.json` from the existing retained project mount.
An administrator can mount a dedicated NAS subdirectory beneath that host data
mount **before recreating/starting the container**. No Updater journal edit, mount
profile drift, auto migration, or second resident service is introduced. The
container must actually see the nested network mount; merely configuring a host
path is insufficient. Exact host mounts, credentials, boot order and acceptance
belong in private operations documentation.
