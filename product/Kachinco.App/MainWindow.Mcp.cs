using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private McpAccessLease? mcpLease;
    private string? mcpPipeName;
    private Window? mcpInformation;

    private void McpReadOnly_Click(object sender, RoutedEventArgs e) => EnableMcp(McpPermission.ReadOnly);
    private void McpEdit_Click(object sender, RoutedEventArgs e) => EnableMcp(McpPermission.Edit);
    private void McpStop_Click(object sender, RoutedEventArgs e) => RevokeMcp();
    private void McpCopy_Click(object sender, RoutedEventArgs e)
    {
        if (mcpLease?.IsActive != true || mcpPipeName is null) return;
        try { Clipboard.SetText(McpConnectionCommand()); }
        catch (System.Runtime.InteropServices.ExternalException)
        { Status.Text = EditorText.Choose("クリップボードを使用中です。もう一度コピーしてください。", "Clipboard is busy. Try copying again."); }
    }
    private string McpConnectionCommand() => "\"" + Path.Combine(AppContext.BaseDirectory, "mcp", "Kachinco.Mcp.exe") + "\" --pipe " + mcpPipeName;
    private void UpdateMcpStatus()
    {
        McpReadOnlyMenu.IsChecked = mcpLease?.Permission == McpPermission.ReadOnly;
        McpEditMenu.IsChecked = mcpLease?.Permission == McpPermission.Edit;
        McpCopyMenu.IsEnabled = McpStopMenu.IsEnabled = mcpLease?.IsActive == true;
    }
    private void RevokeMcp()
    {
        var previous = mcpLease; mcpLease = null; mcpPipeName = null;
        previous?.Revoke();
        mcpInformation?.Close(); mcpInformation = null;
        UpdateMcpStatus();
        Status.Text = EditorText.Choose("MCP接続は無効です。", "MCP is disabled.");
    }
    private void EnableMcp(McpPermission permission)
    {
        RevokeMcp();
        if (session.GetProject().Project is null) { Status.Text = EditorText.Choose("先にプロジェクトを開いてください。", "Open a project first."); return; }
        var lease = new McpAccessLease(session, permission); mcpLease = lease;
        string pipeName = "kachinco-" + Guid.NewGuid().ToString("N"); mcpPipeName = pipeName;
        UpdateMcpStatus();
        var information = new System.Windows.Controls.TextBox { Text = McpConnectionCommand(), IsReadOnly = true, Margin = new Thickness(16) };
        mcpInformation = new Window { Owner = this, Title = EditorText.Choose("MCP接続・同時接続は1つ", "MCP connection · one client at a time"), Width = 700,
            SizeToContent = SizeToContent.Height, Content = information, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        mcpInformation.Closed += (_, _) => information.Clear();
        mcpInformation.Show();
        Status.Text = EditorText.Choose("MCP接続を待っています。ファイル操作は許可されていません。", "Waiting for MCP. File operations are not authorized.");
        _ = ServeMcpAsync(pipeName, lease);
    }
    private async Task ServeMcpAsync(string pipeName, McpAccessLease lease)
    {
        try
        {
            while (lease.IsActive)
            {
                await using var pipe = WindowsLocalPipe.Create(pipeName);
                using var closeOnRevoke = lease.Token.Register(() => pipe.Dispose());
                try
                {
                    await pipe.WaitForConnectionAsync(lease.Token);
                    var adapter = new McpEditorAdapter(session,
                        () => new { sequenceId = selectedSequenceId, clipId = selectedClipId, playheadTicks = Timeline.PlayheadTicks.ToString(CultureInfo.InvariantCulture) },
                        () => Refresh(EditorText.Choose("MCPから編集しました。", "Edited through MCP.")), lease);
                    var reader = new McpBoundedLineReader(pipe);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    while (pipe.IsConnected && lease.IsActive)
                    {
                        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                        readDeadline.CancelAfter(TimeSpan.FromMinutes(2));
                        var frame = await reader.ReadAsync(readDeadline.Token);
                        if (frame.Status == McpFrameStatus.EndOfStream) break;
                        // Buffered clients must not starve Stop/New/permission input on WPF.
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        lease.Demand();
                        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                        requestDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                        string? result = frame.Status == McpFrameStatus.Success
                            ? await adapter.HandleAsync(frame.Line!, requestDeadline.Token, busy)
                            : McpEnvelope.Error(null, -32700, "Invalid or oversized UTF-8 frame.");
                        lease.Demand(cancellationToken: requestDeadline.Token);
                        if (result is not null)
                        {
                            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(requestDeadline.Token);
                            writeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                            await writer.WriteLineAsync(result.AsMemory(), writeDeadline.Token);
                        }
                        if (frame.Status != McpFrameStatus.Success) break;
                    }
                }
                // The request boundary must never fault the WPF dispatcher. Do not log payloads.
                catch (Exception) { if (lease.IsActive) Status.Text = EditorText.Choose("MCP接続を閉じました。再接続できます。", "MCP connection closed. Reconnection is available."); }
            }
        }
        catch (Exception) { if (ReferenceEquals(mcpLease, lease)) Status.Text = EditorText.Choose("MCP接続を開始できません。", "MCP endpoint unavailable."); }
        finally { if (ReferenceEquals(mcpLease, lease)) RevokeMcp(); else lease.Revoke(); }
    }
}
