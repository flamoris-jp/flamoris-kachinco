using System.Globalization;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

// One admitted immutable export; session mutations still use the ordinary boundary.
public sealed class ExportJobs(EditorSession session, string projectPath, string outputRoot, FfmpegSettings? settings = null) : IAsyncDisposable
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
            var snapshot = session.GetProject();
            if (!snapshot.Project!.Sequences.Any(s => s.Id == sequenceId)) throw new McpFault("sequence_not_found");
            CliOperations.CheckMedia(snapshot.Project, projectPath); CliOperations.CheckCanvas(snapshot.Project.Sequences.Single(s => s.Id == sequenceId));
            Cleanup(); Directory.CreateDirectory(outputRoot);
            id = Guid.NewGuid(); revision = snapshot.Revision; output = Path.Combine(Path.GetFullPath(outputRoot), id.ToString("N") + ".mp4");
            cancellation = new(TimeSpan.FromHours(1)); result = null; progress = null;
            var request = new ExportRequest(id, sequenceId, output, ExportPreset.YoutubeH264AacMp4, revision);
            var token = cancellation.Token;
            worker = Task.Run(async () => {
                ExportResult finished;
                try { finished = await CliOperations.ExportAsync(snapshot, projectPath, request, new Reporter(p => { lock (gate) progress = p; }), token, settings); }
                catch (OperationCanceledException) { finished = new(request.JobId, ExportStage.Cancelled, null, []); }
                catch { finished = new(request.JobId, ExportStage.Failed, null, [Diagnostic.Error("EXPORT_FAILED", "Export failed; inspect local diagnostics.")]); }
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
            return Json(new { jobId = id, revision = revision.ToString(CultureInfo.InvariantCulture), stage = result?.Stage.ToString() ?? progress?.Stage.ToString() ?? "Planning",
                framesCompleted = (progress?.FramesCompleted ?? 0).ToString(CultureInfo.InvariantCulture),
                totalFrames = (progress?.TotalFrames ?? 0).ToString(CultureInfo.InvariantCulture),
                diagnostics = result?.Diagnostics, metrics = result?.Metrics,
                bytes = result?.Stage == ExportStage.Completed ? new FileInfo(output!).Length.ToString(CultureInfo.InvariantCulture) : null });
        }
    }
    private JsonElement Read(JsonElement input)
    {
        lock (gate)
        {
            Demand(input.GetProperty("jobId").GetGuid());
            if (result?.Stage != ExportStage.Completed) throw new McpFault("export_not_ready");
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
    private void Cleanup() { if (output is not null) File.Delete(output); cancellation?.Dispose(); cancellation = null; output = null; }
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
