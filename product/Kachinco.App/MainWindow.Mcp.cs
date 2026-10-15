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
    private PreviewFrameCapture previewCapture = null!;
    private readonly SemaphoreSlim downloadAdmission = new(1, 1);
    private void InitializeMcp()
    {
        previewCapture = new(new WindowsPreviewImageEncoder(), new WindowsCaptionRasterizer(Dispatcher));
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
        var uploads = new MediaUploadStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FLAMORIS", "Kachinco", "imported-media"), mediaProbe);
        var downloads = new ClipDownloadStore(downloadAdmission);
        var boundary = new McpBoundary(mcpHost!, KachincoMcpTools.Create(session,
            () => new { sequenceId = selectedSequenceId, clipId = selectedClipId,
                playheadTicks = Timeline.PlayheadTicks.ToString(CultureInfo.InvariantCulture) },
            () => Refresh(EditorText.Choose("MCPから編集しました。", "Edited through MCP."))).Append(previewCapture.CreateTool(session, () => filename, () => mcpHost!.Snapshot)).Concat(allowAiEffectLibrary && effectLibrary is not null
                ? EffectLibraryMcpTools.Create(session, effectLibrary, () => Refresh(EditorText.Choose("エフェクトを適用しました。", "Effect applied."))) : [])
            .Concat(MediaUploadMcpTools.Create(session, uploads, () => Refresh(EditorText.Choose("素材を読み込みました。", "Media imported."))))
            .Concat(ClipDownloadMcpTools.Create(session, downloads, () => filename)),
            new McpOptions(), new McpDiagnostics(logger));
        try
        {
            var grant = await boundary.EnableAsync(permission);
            grant.Revoked.Register(uploads.Dispose);
            grant.Revoked.Register(downloads.Dispose);
            return new(boundary, grant);
        }
        catch (Exception exception)
        {
            logger.Error("mcp.attach", "MCP attachment failed", exception,
                new Dictionary<string, object?> { ["revision"] = session.GetProject().Revision,
                    ["hasProject"] = session.GetProject().Project is not null });
            boundary.Dispose(); uploads.Dispose(); downloads.Dispose(); throw;
        }
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

