using System.Collections.Immutable;
using System.Text.Json;
using Kachinco.Native;

namespace Kachinco.Core;

// WPF and MCP share this facade and its ONE native editing handle.
public sealed class EditorSession : IDisposable
{
    private readonly object gate = new();
    private readonly NativeEditorSession native;
    private readonly string instanceToken = Guid.NewGuid().ToString("N");
    private ProjectSnapshot? projection;
    private long documentGeneration;
    public event Action? DocumentReplacing;
    public EditorSession(int historyLimit = 100) => native = new(historyLimit);
    public string DocumentToken { get { lock (gate) { GetProject(); return instanceToken + ":" + documentGeneration; } } }
    public ProjectSnapshot GetProject()
    {
        lock (gate)
        {
            if (projection is not null) return projection;
            var result = Request(new { action = "get" });
            documentGeneration = result.GetProperty("documentGeneration").GetInt64();
            projection = new(result.GetProperty("revision").GetInt64(), result.GetProperty("project").Deserialize<Project>(NativeProjectCodec.Options),
                result.GetProperty("canUndo").GetBoolean(), result.GetProperty("canRedo").GetBoolean());
            return projection;
        }
    }
    public EditResult Execute(EditBatch batch, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (batch is null) return new(false, GetProject().Revision, [Diagnostic.Error("INVALID_BATCH", "Batch is required.")]);
            var commands = batch.Commands.IsDefault ? null : batch.Commands.Select(c => c is null ? (object?)null : new { type = c.GetType().Name, value = NativeProjectCodec.Element(c) }).ToArray();
            return Change(new { action = "execute", commands, expectedRevision = batch.ExpectedRevision, dryRun = batch.DryRun }, cancellationToken);
        }
    }
    public EditResult Undo(long? expectedRevision = null, CancellationToken cancellationToken = default)
    { lock (gate) return Change(new { action = "undo", expectedRevision }, cancellationToken); }
    public EditResult Redo(long? expectedRevision = null, CancellationToken cancellationToken = default)
    { lock (gate) return Change(new { action = "redo", expectedRevision }, cancellationToken); }
    public EditResult ReplaceProject(Project? project, long? expectedRevision = null)
    { lock (gate) return Change(new { action = "replace", project = NativeProjectCodec.Element(project), expectedRevision }, default); }
    public Result<Sequence> GetSequence(Guid sequenceId)
    {
        lock (gate)
        {
            var value = GetProject().Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId);
            return value is null ? Result<Sequence>.Fail(Diagnostic.Error("SEQUENCE_NOT_FOUND", "Sequence not found.", sequenceId)) : Result<Sequence>.Ok(value);
        }
    }
    private EditResult Change(object request, CancellationToken cancellationToken)
    {
        var prepared = Request(request);
        if (!prepared.GetProperty("success").GetBoolean() || !prepared.TryGetProperty("transactionId", out var transaction)) return Result(prepared);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared.GetProperty("documentChanged").GetBoolean())
                foreach (Action observer in DocumentReplacing?.GetInvocationList() ?? [])
                    try { observer(); } catch { /* observers cannot prevent host lifecycle */ }
            var committed = Request(new { action = "commit", transactionId = transaction.GetInt64() });
            if (committed.GetProperty("success").GetBoolean()) projection = null;
            return Result(committed);
        }
        catch { Request(new { action = "abort" }); throw; }
    }
    private JsonElement Request(object value) => JsonSerializer.Deserialize<JsonElement>(native.Request(JsonSerializer.Serialize(value, NativeProjectCodec.Options)));
    private static EditResult Result(JsonElement value) => new(value.GetProperty("success").GetBoolean(), value.GetProperty("revision").GetInt64(), NativeProjectCodec.Diagnostics(value));
    public void Dispose() { lock (gate) { native.Dispose(); projection = null; } }
}

public static class TimelineQueries
{
    public static ImmutableArray<Clip> ListClips(Track track) =>
        [.. track.Clips.OrderBy(x => x.StartTicks).ThenBy(x => x.Id.ToString("N"), StringComparer.Ordinal)];
    public static ImmutableArray<Caption> ListCaptions(Track track) =>
        [.. track.Captions.OrderBy(x => x.StartTicks).ThenBy(x => x.Id.ToString("N"), StringComparer.Ordinal)];
    public static ImmutableArray<MediaAsset> ListMediaAssets(Project project) =>
        [.. project.Assets.OrderBy(x => x.Id.ToString("N"), StringComparer.Ordinal)];
    public static ImmutableArray<Sequence> ListSequences(Project project) =>
        [.. project.Sequences.OrderBy(x => x.Id.ToString("N"), StringComparer.Ordinal)];
}
