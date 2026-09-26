# Logging integration

Kachinco consumes `Flamoris.Logging` 1.0.0 from nuget.org.
The package is referenced normally; its DLL is not copied into this repository.
Logging observes the existing `EditorSession`, Project, preview/render and MCP
boundaries and owns no editor state.

## Configuration and location

`appsettings.json` contains the ordinary application-owned `logging` section.
The default level is `debug`, with console output and a rotating text file:

```json
{
  "logging": {
    "level": "debug",
    "categories": {
      "mcp": "info",
      "mcp.transport": "debug",
      "mcp.auth": "warn"
    },
    "outputs": [
      { "type": "console" },
      {
        "type": "file",
        "path": "logs/kachinco.log",
        "format": "text",
        "rotation": {
          "enabled": true,
          "maxFileSizeMb": 20,
          "maxFiles": 10
        }
      }
    ]
  }
}
```

Relative paths resolve below the explicit per-user base path
`%LOCALAPPDATA%\FLAMORIS\Kachinco`, never the installation directory or process
working directory.

The stdio MCP bridge removes console logging because stdout is the MCP protocol
channel. Its file output is separated as `logs/kachinco-mcp.log` so the editor
and bridge never write the same file concurrently. The launcher captures the raw
stdout stream and redirects Console.Out to stderr before logger construction,
including fallback sinks.

## Categories

- `app`, `app.startup`, `app.shutdown`
- `project`, `document`, `document.open`, `document.save`
- `command.failure`
- `media`, `preview`, `render`
- `mcp.transport`, `mcp.protocol`, `mcp.auth`, `mcp.session`,
  `mcp.command`, `mcp.query`

Routine command/query detail stays at `debug`. Major lifecycle events use
`info`; recoverable transport, permission and revision conflicts use `warn`;
failed operations and unexpected exceptions use `error`.
Core 1.1.0 MCP diagnostics use its curated `info` boundary events with transport,
outcome, tool name and duration only; stale guards appear as `stale_revision`.
Application-local transport diagnostics and request/exception logging were removed.

## Privacy and failure isolation

Call sites log stable IDs, revisions, operation names, levels, categories and
diagnostic codes. They do not pass credentials, tokens, API keys, authorization
headers, MCP payloads, project/media contents, or source/output paths as
structured properties. Expected file-operation failures omit the raw exception
because operating-system exception messages commonly contain absolute paths;
their diagnostic code and exception type are logged instead. Unexpected and
fatal exceptions use the logging exception argument instead of being
concatenated into messages, and may therefore contain operating-system or
third-party diagnostic text.

`Flamoris.Logging` isolates sink failures. Kachinco's configuration bootstrap
also falls back safely, so a logging output failure cannot fail startup, save,
preview, command execution, or MCP transport.
