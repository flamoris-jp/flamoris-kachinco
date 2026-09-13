using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private CancellationTokenSource? mcpLifetime;
    private readonly Dictionary<Guid, EditorExportJob> exportJobs = [];
    private async void Mcp_Click(object sender, RoutedEventArgs e)
    {
        if (mcpLifetime is not null) { mcpLifetime.Cancel(); mcpLifetime = null; Status.Text = "MCP接続を停止しました。"; return; }
        var lifetime = new CancellationTokenSource(); mcpLifetime = lifetime;
        string pipeName = "kachinco-" + Guid.NewGuid().ToString("N");
        var information = new System.Windows.Controls.TextBox { Text = "Kachinco.Mcp.exe --pipe " + pipeName, IsReadOnly = true, Margin = new Thickness(16) };
        new Window { Owner = this, Title = "MCPクライアントの起動コマンド", Width = 700, SizeToContent = SizeToContent.Height,
            Content = information, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
        Status.Text = "MCP接続を待っています。";
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                var adapter = new McpEditorAdapter(session,
                    () => new { sequenceId = selectedSequenceId, clipId = selectedClipId, playheadTicks = Timeline.PlayheadTicks.ToString(CultureInfo.InvariantCulture) },
                    () => Refresh("MCPから編集しました。"), McpJobAsync);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                while (pipe.IsConnected && !lifetime.IsCancellationRequested)
                {
                    var line = await ReadBoundedLine(reader, lifetime.Token);
                    if (line is null) break;
                    // This continuation executes on the WPF dispatcher, sharing the exact UI session.
                    if (busy) { await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32000,\"message\":\"Editor is busy\"}}"); continue; }
                    var result = await adapter.HandleAsync(line);
                    if (result is not null) await writer.WriteLineAsync(result.AsMemory(), lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = "MCP接続を終了しました: " + ex.Message; }
        finally { if (ReferenceEquals(mcpLifetime, lifetime)) mcpLifetime = null; lifetime.Dispose(); }
    }
    private static async Task<string?> ReadBoundedLine(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return text.ToString();
            if (text.Length >= 4 * 1024 * 1024) throw new InvalidDataException("MCP message is too large.");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }
    private Task<object> McpJobAsync(string method, JsonElement args)
    {
        if (method == "export_start")
        {
            var snapshot = session.GetProject();
            var revision = long.Parse(args.GetProperty("expectedRevision").GetString()!, CultureInfo.InvariantCulture);
            if (revision != snapshot.Revision || snapshot.Project is null) return Task.FromResult<object>(new { error = "REVISION_CONFLICT" });
            if (exportJobs.Values.Any(x => x.Result is null)) return Task.FromResult<object>(new { error = "EXPORT_BUSY" });
            foreach (var old in exportJobs.Where(x => x.Value.Result is not null).Select(x => x.Key).ToArray()) { exportJobs[old].Cancellation.Dispose(); exportJobs.Remove(old); }
            var id = Guid.NewGuid(); var job = new EditorExportJob(); exportJobs.Add(id, job);
            var decoder = new FfmpegMediaDecoder();
            var service = new SnapshotExportService(new SharedFrameRenderer(decoder, filename, new WindowsCaptionRasterizer(Dispatcher)),
                new SharedAudioRenderer(decoder, filename), new FfmpegEncodingBackend(), new(null), filename);
            var request = new ExportRequest(id, args.GetProperty("sequenceId").GetGuid(), args.GetProperty("outputPath").GetString()!, ExportPreset.YoutubeH264AacMp4, revision);
            _ = RunEditorJob(job, service, snapshot, request);
            return Task.FromResult<object>(new { jobId = id, revision = revision.ToString(CultureInfo.InvariantCulture) });
        }
        var jobId = args.GetProperty("jobId").GetGuid();
        if (!exportJobs.TryGetValue(jobId, out var found)) return Task.FromResult<object>(new { error = "JOB_NOT_FOUND" });
        if (method == "job_cancel") found.Cancellation.Cancel();
        return Task.FromResult<object>(new { jobId, progress = found.Progress, result = found.Result });
    }
    private async Task RunEditorJob(EditorExportJob job, IExportService service, ProjectSnapshot snapshot, ExportRequest request)
    {
        var progress = new Progress<ExportProgress>(p => job.Progress = p);
        try { job.Result = await Task.Run(() => service.ExportAsync(snapshot, request, progress, job.Cancellation.Token)); }
        catch (Exception ex) { job.Result = new(request.JobId, ExportStage.Failed, null, [Diagnostic.Error("EXPORT_FAILED", ex.Message)]); }
    }
    private sealed class EditorExportJob
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public ExportProgress? Progress { get; set; }
        public ExportResult? Result { get; set; }
    }
}
