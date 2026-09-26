using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Flamoris.Mcp.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private KachincoMcpHost? mcpHost;
    private McpBoundary? mcpBoundary;
    private CapabilityGrant? mcpGrant;
    private Window? mcpInformation;
    private bool mcpShuttingDown;
    private long mcpEnableGeneration;
    // Includes nested WPF modal loops, which continue to dispatch MCP requests.
    private int humanOperationDepth;

    private void InitializeMcp()
    {
        mcpHost = new(session, () => busy || Volatile.Read(ref humanOperationDepth) > 0 || Timeline.HasActiveGesture || draggedMediaId is not null,
            (action, token) => {
                if (Dispatcher.CheckAccess()) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
                return Dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task;
            });
        mcpHost.Invalidating += QueueMcpStatus;
    }
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
    private async void McpReadOnly_Click(object sender, RoutedEventArgs e) => await EnableMcp(McpPermission.ReadOnly);
    private async void McpEdit_Click(object sender, RoutedEventArgs e) => await EnableMcp(McpPermission.Edit);
    private void McpStop_Click(object sender, RoutedEventArgs e) => RevokeMcp();
    private void McpCopy_Click(object sender, RoutedEventArgs e)
    {
        if (mcpGrant?.IsActive != true) return;
        try { Clipboard.SetText(McpConnectionInformation()); }
        catch (System.Runtime.InteropServices.ExternalException)
        { Status.Text = EditorText.Choose("クリップボードを使用中です。もう一度コピーしてください。", "Clipboard is busy. Try copying again."); }
    }
    // Explicit transient handoff only. Never persist this JSON as application settings.
    private string McpConnectionInformation() => JsonSerializer.Serialize(new {
        command = Path.Combine(AppContext.BaseDirectory, "mcp", "Flamoris.Mcp.Bridge.exe"),
        args = new[] { "--pipe", mcpBoundary!.Options.PipeName },
        env = new Dictionary<string, string> { [StdioBridge.CredentialEnvironmentVariable] = mcpGrant!.ExportCredential() }
    }, new JsonSerializerOptions { WriteIndented = true });

    private void QueueMcpStatus()
    {
        if (mcpShuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(new Action(UpdateMcpStatus), DispatcherPriority.Background);
    }
    private void UpdateMcpStatus()
    {
        if (mcpGrant is { IsActive: false }) { RevokeMcp(); return; }
        var state = mcpBoundary?.Status.Current;
        bool active = mcpGrant?.IsActive == true;
        McpReadOnlyMenu.IsChecked = active && mcpGrant!.Permission == McpPermission.ReadOnly;
        McpEditMenu.IsChecked = active && mcpGrant!.Permission == McpPermission.Edit;
        McpCopyMenu.IsEnabled = McpStopMenu.IsEnabled = active;
        string connection = state?.Connected == true ? EditorText.Choose("接続中", "Connected") :
            state?.IsGreen == true ? EditorText.Choose("接続待ち", "Ready") : EditorText.Choose("無効 / 接続不可", "Disabled / unavailable");
        McpStatusText.Text = (state?.IsGreen == true ? "🟢 MCP " : "🔴 MCP ") + connection;
        McpActivityText.Text = state?.ActivityVisible == true ? EditorText.Choose("AI 処理中", "AI working") : "";
        // Window cursor is a projection, not a cached tool cursor. Child tool cursors
        // continue to own their local values; ClearValue restores normal inheritance.
        if (state?.ActivityVisible == true) Cursor = Cursors.AppStarting;
        else ClearValue(CursorProperty);
    }
    private void RevokeMcp()
    {
        mcpEnableGeneration++;
        var boundary = mcpBoundary;
        mcpBoundary = null;
        mcpGrant = null;
        if (boundary is not null) { boundary.Status.Changed -= QueueMcpStatus; boundary.Dispose(); }
        mcpInformation?.Close(); mcpInformation = null;
        UpdateMcpStatus();
        Status.Text = EditorText.Choose("MCP接続は無効です。", "MCP is disabled.");
    }
    private void ShutdownMcp()
    {
        mcpShuttingDown = true;
        mcpHost?.Shutdown();
        RevokeMcp();
        mcpHost?.Dispose();
    }
    private async Task EnableMcp(McpPermission permission)
    {
        RevokeMcp();
        long generation = mcpEnableGeneration;
        if (mcpShuttingDown || session.GetProject().Project is null)
        { Status.Text = EditorText.Choose("先にプロジェクトを開いてください。", "Open a project first."); return; }
        var boundary = new McpBoundary(mcpHost!, KachincoMcpTools.Create(session,
            () => new { sequenceId = selectedSequenceId, clipId = selectedClipId,
                playheadTicks = Timeline.PlayheadTicks.ToString(CultureInfo.InvariantCulture) },
            () => Refresh(EditorText.Choose("MCPから編集しました。", "Edited through MCP."))),
            new McpOptions(), new McpDiagnostics(logger));
        mcpBoundary = boundary;
        boundary.Status.Changed += QueueMcpStatus;
        try
        {
            var grant = await boundary.EnableAsync(permission);
            if (generation != mcpEnableGeneration || mcpShuttingDown) { boundary.Dispose(); return; }
            mcpGrant = grant;
            UpdateMcpStatus();
            var information = new TextBox { Text = McpConnectionInformation(), IsReadOnly = true,
                Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
            mcpInformation = new Window { Owner = this,
                Title = EditorText.Choose("MCP接続・一時情報（保存しないでください）", "MCP connection · transient information (do not save)"),
                Width = 700, SizeToContent = SizeToContent.Height, Content = information,
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            mcpInformation.Closed += (_, _) => information.Clear();
            mcpInformation.Show();
            Status.Text = EditorText.Choose("MCP接続を待っています。ファイル操作は許可されていません。", "Waiting for MCP. File operations are not authorized.");
            _ = ServeMcpAsync(boundary, grant);
        }
        catch
        {
            if (ReferenceEquals(mcpBoundary, boundary)) RevokeMcp(); else boundary.Dispose();
            Status.Text = EditorText.Choose("MCP接続を開始できません。", "MCP endpoint unavailable.");
        }
    }
    private async Task ServeMcpAsync(McpBoundary boundary, CapabilityGrant grant)
    {
        try { await Task.Run(() => new LocalMcpEndpoint(boundary).RunAsync(grant)); }
        catch { /* No transport exception may escape into WPF. */ }
        finally
        {
            if (ReferenceEquals(mcpBoundary, boundary)) RevokeMcp();
            else boundary.Dispose();
        }
    }
}
