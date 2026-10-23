using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

public sealed class HeadlessSession : IDisposable
{
    private readonly SemaphoreSlim lane = new(1);
    private readonly FileStream? projectLock;
    public EditorSession Session { get; } = new();
    public KachincoMcpHost Host { get; }
    public McpBoundary Boundary { get; }
    public string ProjectPath { get; }
    public string SavePath { get; }
    public bool Dirty { get; private set; }
    public ExportJobs? Jobs { get; }
    private readonly MediaUploadStore? uploads;
    private readonly bool autosave;
    public HeadlessSession(string path, string? savePath = null, bool writable = false, bool autosave = false, string? outputRoot = null,
        FfmpegSettings? encoding = null)
    {
        ProjectPath = Path.GetFullPath(path); SavePath = Path.GetFullPath(savePath ?? path); this.autosave = autosave;
        try
        {
            if (writable) projectLock = new FileStream(ProjectPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Host = new(Session, () => false, async (action, token) => {
                await lane.WaitAsync(token);
                try { token.ThrowIfCancellationRequested(); action(); } finally { lane.Release(); }
            });
            var capture = new PreviewFrameCapture(new FfmpegImageEncoder(), new LinuxCaptionRasterizer());
            if (outputRoot is not null)
            {
                Jobs = new(Session, ProjectPath, outputRoot, encoding);
                uploads = new(Path.Combine(Path.GetDirectoryName(ProjectPath)!, "imported-media"), new FfprobeMediaProbe());
            }
            Boundary = new(Host, KachincoMcpTools.Create(Session, () => new { headless = true, dirty = Dirty }, Changed)
                .Append(capture.CreateTool(Session, () => ProjectPath, () => Host.Snapshot))
                .Concat(Jobs?.Tools() ?? [])
                .Concat(uploads is null ? [] : MediaUploadMcpTools.Create(Session, uploads, Changed))
                .Append(new HostTool<JsonElement>("project_save", "Atomically checkpoint this host's configured project. No caller-supplied paths.",
                    EmptySchema, OperationKind.Transaction, Empty, (context, _, _) => context.CommitAsync(() => {
                        Save(); return JsonSerializer.SerializeToElement(new { saved = true, revision = Session.GetProject().Revision.ToString(CultureInfo.InvariantCulture) });
                    }))), new McpOptions(), new McpDiagnostics(KachincoLogging.Create("mcp-bridge").Logger));
        }
        catch { Session.Dispose(); projectLock?.Dispose(); throw; }
    }
    public async Task LoadAsync(CancellationToken token)
    {
        var loaded = await new ProjectFileStore().LoadAsync(ProjectPath, token);
        if (!loaded.Success) throw new InvalidDataException(string.Join("; ", loaded.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var replace = Session.ReplaceProject(loaded.Value);
        if (!replace.Success) throw new InvalidDataException("Project validation failed.");
    }
    private void Changed()
    {
        Dirty = true;
        if (!autosave) return;
        try { Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "checkpoint_failed", dirty = true, detail = e.GetType().Name })); }
    }
    public void Save()
    {
        var snapshot = Session.GetProject();
        // Saving elsewhere must not change what project-relative media refers to.
        if (Path.GetDirectoryName(ProjectPath) != Path.GetDirectoryName(SavePath))
        {
            var paths = CliOperations.InspectMedia(snapshot.Project!, ProjectPath);
            var commands = paths.Where(p => !Path.IsPathFullyQualified(p.SourcePath) && p.ResolvedPath is not null)
                .Select(p => { var a = snapshot.Project!.Assets.Single(a => a.Id == p.MediaAssetId);
                    return (EditCommand)new RelinkMedia(a.Id, p.ResolvedPath!, a.DurationTicks, a.SampleRate, a.Channels); }).ToImmutableArray();
            if (!commands.IsEmpty && !Session.Execute(new(commands, snapshot.Revision)).Success)
                throw new InvalidDataException("Could not preserve relative media during Save As.");
            snapshot = Session.GetProject();
        }
        string? unpublished = SavePath != ProjectPath ? Path.Combine(Path.GetDirectoryName(SavePath)!, ".kachinco-" + Guid.NewGuid().ToString("N") + ".fkproj") : null;
        try
        {
            var result = new ProjectFileStore().SaveAsync(unpublished ?? SavePath, snapshot.Project!).GetAwaiter().GetResult();
            if (!result.Success) throw new IOException("PROJECT_WRITE_FAILED: checkpoint could not be saved.");
            if (unpublished is not null) File.Move(unpublished, SavePath, overwrite: false);
        }
        finally { if (unpublished is not null) File.Delete(unpublished); }
        Dirty = false;
    }
    public static JsonElement EmptySchema => JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties = new { } });
    public static JsonElement Empty(JsonElement json)
    { if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Any()) throw new JsonException(); return json; }
    public RequestGuard Guard() => new(Host.Snapshot.RuntimeId, Host.Snapshot.DocumentToken, Host.Snapshot.Revision);
    public void Dispose() { Boundary.Dispose(); Host.Dispose(); uploads?.Dispose(); Session.Dispose(); projectLock?.Dispose(); lane.Dispose(); }
}
