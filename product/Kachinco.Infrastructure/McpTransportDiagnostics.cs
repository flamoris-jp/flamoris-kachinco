using Flamoris.Logging;

namespace Kachinco.Infrastructure;

// Observes transport lifecycle only. It owns no connection, permission, session or editor state.
public sealed class McpTransportDiagnostics(FlamorisLogger logger, string transport)
{
    private Dictionary<string, object?> Properties() => new() { ["transport"] = transport };

    public void EndpointStarted() => logger.Info("mcp.transport", "MCP endpoint started", Properties());
    public void EndpointStopped() => logger.Info("mcp.transport", "MCP endpoint stopped", Properties());
    public void ClientAttached() => logger.Info("mcp.transport", "MCP client attached", Properties());
    public void ClientDetached() => logger.Info("mcp.transport", "MCP client detached", Properties());
    public void ConnectionFailed(Exception exception) =>
        logger.Log(LogLevel.Warn, "mcp.transport", "MCP connection closed or unavailable", Properties(), exception);
}
