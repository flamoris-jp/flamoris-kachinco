using System.Globalization;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

// One admitted immutable export; session mutations still use the ordinary boundary.
public sealed class ExportJobs(EditorSession session, string projectPath, string outputRoot, FfmpegSettings? settings = null,
    WorkspaceStorage? workspace = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private Guid id;
    private string? output;
    private ExportResult? result;
    private ExportProgress? progress;
    private long revision;
    private bool stopping;
    private bool downloadAvailable;
    private bool retainForRecovery;
    private string? publishedPath;
    private string? publicationStage;

    public IEnumerable<HostTool> Tools() => [
        new HostTool<JsonElement>("export_begin", "Start one H.264/AAC sequence export with host-configured GPU/CPU encoding at the guarded revision. Returns an opaque job ID.",
            Schema("{\"sequenceId\":{\"type\":\"string\",\"format\":\"uuid\"}}", "sequenceId"), OperationKind.Transaction,
            json => Fields(json, "sequenceId"), (context, input, token) => context.CommitAsync(() => Begin(input.GetProperty("sequenceId").GetGuid()))),
        new HostTool<JsonElement>("export_status", "Inspect this host's current export job, snapshot revision and encoding progress.",
            JobSchema, OperationKind.Query, JobInput, (context, input, token) => context.ReadAsync(() => Status(input.GetProperty("jobId").GetGuid())), foreground: false),
        new HostTool<JsonElement>("export_cancel", "Cancel this host's current export and join cleanup asynchronously.",
            JobSchema, OperationKind.Transaction, JobInput, (context, input, token) => context.CommitAsync(() => {
                lock (gate) { Demand(input.GetProperty("jobId").GetGuid()); cancellation?.Cancel(); return Json(new { cancelled = true, jobId = id }); }
            })),
        new HostTool<JsonElement>("export_read", "Read a completed export in bounded base64 chunks (max 196608 bytes).",
            Schema("{\"jobId\":{\"type\":\"string\",\"format\":\"uuid\"},\"offset\":{\"type\":\"string\",\"pattern\":\"^(0|[1-9][0-9]*)$\"}}", "jobId", "offset"),
            OperationKind.Query, json => Fields(json, "jobId", "offset"), (context, input, token) => context.ReadAsync(() => Read(input)), foreground: false)
    ];
    private JsonElement Begin(Guid sequenceId)
    {
        lock (gate)
        {
            if (stopping || worker is { IsCompleted: false }) throw new McpFault(McpErrors.Busy);
            workspace?.VerifyAvailable();
            var snapshot = session.GetProject();
            if (!snapshot.Project!.Sequences.Any(s => s.Id == sequenceId)) throw new McpFault("sequence_not_found");
            CliOperations.CheckMedia(snapshot.Project, projectPath); CliOperations.CheckCanvas(snapshot.Project.Sequences.Single(s => s.Id == sequenceId));
            Cleanup(); Directory.CreateDirectory(outputRoot);
            id = Guid.NewGuid(); revision = snapshot.Revision; output = Path.Combine(Path.GetFullPath(outputRoot), id.ToString("N") + ".mp4");
            cancellation = new(TimeSpan.FromHours(1)); result = null; progress = null;
            downloadAvailable = false; retainForRecovery = false; publishedPath = null;
            publicationStage = workspace is null ? "NotConfigured" : "Pending";
            var request = new ExportRequest(id, sequenceId, output, ExportPreset.YoutubeH264AacMp4, revision);
            var token = cancellation.Token;
            worker = Task.Run(async () => {
                ExportResult finished;
                try { finished = await CliOperations.ExportAsync(snapshot, projectPath, request, new Reporter(p => { lock (gate) progress = p; }), token, settings); }
                catch (OperationCanceledException) { finished = new(request.JobId, ExportStage.Cancelled, null, []); }
                catch { finished = new(request.JobId, ExportStage.Failed, null, [Diagnostic.Error("EXPORT_FAILED", "Export failed; inspect local diagnostics.")]); }
                bool encoded = finished.Stage == ExportStage.Completed;
                lock (gate) downloadAvailable = encoded;
                if (workspace is not null && encoded)
                {
                    try
                    {
                        string durable = await workspace.PublishAsync(request.OutputPath, request.JobId, sequenceId, snapshot.Revision, token);
                        lock (gate) { publishedPath = durable; publicationStage = "Completed"; }
                    }
                    catch (OperationCanceledException)
                    {
                        finished = finished with { Stage = ExportStage.Cancelled, Diagnostics = [Diagnostic.Error("EXPORT_PUBLICATION_CANCELLED", "Durable publication cancelled; local encoded MP4 retained for recovery.")] };
                        lock (gate) { retainForRecovery = true; publicationStage = "Cancelled"; }
                    }
                    catch
                    {
                        finished = finished with { Stage = ExportStage.Failed, Diagnostics = [Diagnostic.Error("EXPORT_PUBLICATION_FAILED", "Durable publication failed; local encoded MP4 retained for recovery.")] };
                        lock (gate) { retainForRecovery = true; publicationStage = "Failed"; }
                    }
                }
                lock (gate) result = finished;
            }, CancellationToken.None);
            return Json(new { jobId = id, revision = revision.ToString(CultureInfo.InvariantCulture) });
        }
    }
    private JsonElement Status(Guid job)
    {
        lock (gate)
        {
            Demand(job);
            // The encoder reports Completed before durable publication returns.
            // Only the final Job result may expose a terminal stage to pollers.
            string stage = result?.Stage.ToString() ?? (workspace is not null && (downloadAvailable || progress?.Stage == ExportStage.Completed)
                ? "Publishing" : progress?.Stage == ExportStage.Completed ? "Encoding" : progress?.Stage.ToString() ?? "Planning");
            return Json(new { jobId = id, revision = revision.ToString(CultureInfo.InvariantCulture),
                stage,
                framesCompleted = (progress?.FramesCompleted ?? 0).ToString(CultureInfo.InvariantCulture),
                totalFrames = (progress?.TotalFrames ?? 0).ToString(CultureInfo.InvariantCulture),
                diagnostics = result?.Diagnostics, metrics = result?.Metrics, publicationStage, publishedPath, downloadAvailable,
                bytes = downloadAvailable ? new FileInfo(output!).Length.ToString(CultureInfo.InvariantCulture) : null });
        }
    }
    private JsonElement Read(JsonElement input)
    {
        lock (gate)
        {
            Demand(input.GetProperty("jobId").GetGuid());
            if (!downloadAvailable) throw new McpFault("export_not_ready");
            var text = input.GetProperty("offset").GetString();
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset.ToString(CultureInfo.InvariantCulture) != text)
                throw new JsonException();
            using var file = File.OpenRead(output!);
            if (offset > file.Length) throw new McpFault("invalid_offset");
            file.Position = offset; var bytes = new byte[(int)Math.Min(196608, file.Length - offset)]; file.ReadExactly(bytes);
            return Json(new { jobId = id, offset = text, data = Convert.ToBase64String(bytes), nextOffset = file.Position.ToString(CultureInfo.InvariantCulture), eof = file.Position == file.Length });
        }
    }
    private void Demand(Guid job) { if (job == Guid.Empty || job != id) throw new McpFault("export_not_found"); }
    private void Cleanup()
    {
        if (output is not null && !retainForRecovery) File.Delete(output);
        cancellation?.Dispose(); cancellation = null; output = null; downloadAvailable = false;
    }
    public async ValueTask DisposeAsync()
    {
        Task? pending; lock (gate) { stopping = true; cancellation?.Cancel(); pending = worker; }
        if (pending is not null) await pending;
        lock (gate) Cleanup();
    }
    private sealed class Reporter(Action<ExportProgress> report) : IProgress<ExportProgress> { public void Report(ExportProgress value) => report(value); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement JobSchema => Schema("{\"jobId\":{\"type\":\"string\",\"format\":\"uuid\"}}", "jobId");
    private static JsonElement JobInput(JsonElement value) => Fields(value, "jobId");
    private static JsonElement Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(p => !names.Contains(p.Name)) || names.Any(n => !value.TryGetProperty(n, out _))) throw new JsonException();
        return value;
    }
    private static JsonElement Schema(string properties, params string[] required) => Json(new {
        type = "object", additionalProperties = false, required, properties = JsonSerializer.Deserialize<JsonElement>(properties) });
}
