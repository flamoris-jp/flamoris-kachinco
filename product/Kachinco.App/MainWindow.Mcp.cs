using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Flamoris.Mcp.Core;
using Flamoris.Mcp.Wpf;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private KachincoMcpHost? mcpHost;
    private McpDesktopUi? mcpUi;
    private int humanOperationDepth;
    private void InitializeMcp()
    {
        mcpHost = new(session, () => busy || Volatile.Read(ref humanOperationDepth) > 0 || Timeline.HasActiveGesture || draggedMediaId is not null,
            (action, token) => {
                if (Dispatcher.CheckAccess()) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
                return Dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task;
            });
        mcpUi = new(this, McpMenu, "flamoris-kachinco",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Kachinco", "mcp-connection.json"),
            "FLAMORIS.Kachinco", Path.Combine(AppContext.BaseDirectory, "mcp", "Flamoris.Mcp.Bridge.exe"),
            AttachMcp, () => session.GetProject().Project is not null, () => EditorText.Culture);
        McpStatusPanel.Content = mcpUi.StatusIndicator;
        mcpHost.Invalidating += RevokeMcp;
    }
    private async Task<McpDesktopAttachment> AttachMcp(McpPermission permission)
    {
        var boundary = new McpBoundary(mcpHost!, KachincoMcpTools.Create(session,
            () => new { sequenceId = selectedSequenceId, clipId = selectedClipId,
                playheadTicks = Timeline.PlayheadTicks.ToString(CultureInfo.InvariantCulture) },
            () => Refresh(EditorText.Choose("MCPから編集しました。", "Edited through MCP."))),
            new McpOptions(), new McpDiagnostics(logger));
        try { return new(boundary, await boundary.EnableAsync(permission)); }
        catch { boundary.Dispose(); throw; }
    }
    private void UpdateMcpStatus() { mcpUi?.Refresh(); mcpUi?.NotifyHostReady(); }
    private void RevokeMcp() => mcpUi?.Invalidate();
    private void ShutdownMcp() { mcpUi?.Shutdown(); mcpHost?.Shutdown(); mcpHost?.Dispose(); }
    private IDisposable BeginHumanOperation()
    {
        Interlocked.Increment(ref humanOperationDepth);
        return new HumanOperation(this);
    }
    private sealed class HumanOperation(MainWindow owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Decrement(ref owner.humanOperationDepth);
        }
    }
}
