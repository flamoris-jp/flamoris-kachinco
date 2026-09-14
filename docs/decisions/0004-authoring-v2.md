# Clapper / Recipe persistence v2 and restricted worker

Issue #5. Version 1 remains readable without mutation. Version 2 retains the frozen
v1 project DTO as its `project` field and adds required `authoring` sequence entries
and `generatedAssets` provenance entries in the envelope. Every sequence has exactly
one authoring entry; unknown/missing entries fail. V1 reads produce empty authoring
collections. Every subsequent save writes v2. Version dispatch precedes decoding.

Sequence-owned Clappers have globally stable IDs, unique ordinal names within the
sequence, half-open ranges, optional point/rectangle in project pixels, optional
target-track and source-clip IDs, and notes. Dangling references reject the command;
no deletion silently changes the named context. UI and MCP use the same commands.

Recipes are sequence-owned immutable revisions with stable ID, Clapper reference,
restricted Python source, API version, deterministic seed and renderer version.
The initial proof supports only explicit text and seeded particle primitives.
Normalized source hash plus recipe revision and rendered-file SHA-256 are attached
to the generated MediaAsset. Generated assets remain MOV registrations, so ordinary
clip operations and the same renderer apply. A new generation adds Recipe, asset
and clip in one transaction; failed rendering does not change the project.
Regeneration replaces media on the same asset identity only when compatible with
all current clip ranges; hand-edited placement and clip identity remain unchanged.

Python is parsed by a separate isolated worker as a small whitelisted language.
User source is never passed to `exec` or `eval`. Only expression calls to `text`
and `particles` with literal keyword arguments are accepted; imports, attributes,
loops, comprehensions, definitions, arbitrary calls and computed expressions fail.
This intentionally narrow Python subset compiles to typed render IR. The worker
has bounded input/output, wall timeout and process-memory limits. Windows job
objects constrain memory and terminate worker children. A missing Python runtime
is a structured failure, never an implicit download or unrestricted fallback.

Typed IR is validated again by the C# host before drawing. Coordinates and counts
are bounded; PRNG and API versions are explicit. Text output reproducibility is
scoped to the same installed font and renderer runtime. Repeating identical input
must yield identical RGBA frames; encoded bitstream reproducibility additionally
requires the recorded FFmpeg build, and is not promised across encoder versions.
