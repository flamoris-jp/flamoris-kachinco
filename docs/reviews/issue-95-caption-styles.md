# Issue #95 implementation review

Akino confirmed full style editing, partial bulk changes and timeline/right-inspector
interaction on 2026-10-18. Reviewed against main dec998d26.

- Native owns validation, per-caption replacement/partial bulk mutations, shared
  history and conditional v7 codec. Legacy v1-v6 inputs and conditional v2-v6
  outputs remain intact without explicit styles. No parallel editing authority.
- UI bulk scope resolves explicit IDs; changing family does not overwrite color,
  position or other properties. Mere focus changes do not commit first-row values
  over mixed selections. Scope resets when the selected captions change.
- Preview/frame capture/export share evaluated styles and the Windows text raster
  adapter. Default pixels match legacy single-caption output; missing families
  fall back to Yu Gothic, with an inspector notice. Style sizes use short-edge
  reference pixels, so preview quality changes preserve layout.
- Existing MCP get_project/edit_batch expose the two new typed commands. Actual
  producer descriptor was exported into the existing clip fixture. Companion Hub
  templates/fixture carry the same descriptor; tool count is unchanged.
- Required native capabilities/portable identity advance to 131071. Complete
  application update is required; mixing an older native DLL is rejected.

## Executed validation

- Release solution Windows build: zero warnings/errors. In this constrained host,
  native MSBuild was run serially with node reuse disabled; the temporary local
  build override was removed from the submitted source.
- Native CTest: all 5 executables passed, including caption patch atomicity,
  validation, history and v7 codec contracts.
- 50 related managed test cases passed via direct reflection invocation of the
  compiled MSTest methods (including DataRows), covering caption styles, SRT,
  persistence, evaluation, native edits, creation/title behavior, MCP schemas/
  history/guards and actual producer catalog parity. Test temporary storage was
  directed into the writable workspace. The ordinary VSTest runner timed out
  connecting to testhost in this host; this is **not** a claim of a successful
  standard dotnet test run or the complete managed suite.
- Windows caption smoke passed: default-pixel equivalence, Japanese/English,
  multiline/wrapping, landscape/portrait/full/half sizes, missing-family fallback,
  exact CPU preview/export RGBA parity and UI multi-selection/partial/sequence
  edits. Inspector PNG was rendered and visually reviewed for clipping/contrast.
- Companion Hub stdlib unittest passed for both templates, partial/null fields,
  guards and retained commands. Full Hub dependency suite remains CI validation.

Physical use with the song's real SRT, final encoded MP4 visual assessment and
the deployed Hub/desktop/client route remain staging acceptance, not completed
production rollout. See staging/windows-caption-styles.md and ADR 0022.
