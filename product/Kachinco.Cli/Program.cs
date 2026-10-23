using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

public static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new LongStringConverter(), new JsonStringEnumConverter() } };
    public static async Task<int> Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        using var term = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; lifetime.Cancel(); }) : null;
        try { return await RunAsync(args, lifetime.Token); }
        catch (OperationCanceledException) { Error("cancelled"); return 130; }
        catch (Exception e) when (e is ArgumentException or JsonException or FormatException or OverflowException)
        { Error("invalid_request", e.Message); return 2; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or DllNotFoundException or PlatformNotSupportedException)
        { Error("operation_failed", e.Message); return 1; }
    }
    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length == 0 || args is ["--help"] or ["help"])
        {
            Console.WriteLine("""
                Kachinco Linux CLI (CPU / VAAPI encoding)
                project new --output movie.fkproj [--name Movie] [--width 1920 --height 1080 --duration 10]
                project info|validate movie.fkproj [--json]
                media import movie.fkproj --source media.mp4 --output edited.fkproj
                media relink movie.fkproj --asset UUID --source media.mp4 --output edited.fkproj
                edit apply movie.fkproj --commands edits.json --output edited.fkproj
                render frame movie.fkproj --time 1.5 --output frame.png [--sequence UUID]
                export movie.fkproj --output movie.mp4 [--sequence UUID] [--encoder cpu|auto|vaapi] [--vaapi-device /dev/dri/renderD128]
                serve --project movie.fkproj --hub-endpoint wss://host/desktop/kachinco-lime --credential-file token --permission edit|readOnly --output-root directory
                catalog
                gpu check [--vaapi-device /dev/dri/renderD128]
                JSON results on stdout; diagnostics/progress on stderr. Outputs must be new files.
                """); return 0;
        }
        if (args is ["catalog"])
        {
            using var catalog = new HeadlessSession(Path.Combine(Path.GetTempPath(), "catalog.fkproj"), outputRoot: Path.GetTempPath());
            Write(catalog.Boundary.DescribeCatalog()); return 0;
        }
        if (args.Length >= 2 && args[0] == "gpu" && args[1] == "check")
        {
            var options = Options.Parse(args[2..], "vaapi-device");
            string device = options.Get("vaapi-device") ?? "/dev/dri/renderD128";
            var result = await FfmpegEncodingBackend.CheckVaapiAsync(device, token);
            Write(new { available = result.Success, encoder = result.Value, device, composition = "CPU", result.Diagnostics });
            return result.Success ? 0 : 1;
        }
        if (args[0] == "serve")
        {
            string[] keys = ["project", "hub-endpoint", "credential-file", "permission", "output-root", "encoder", "vaapi-device"];
            Options service;
            if (args is ["serve", "--config", var configPath])
            {
                using var config = JsonDocument.Parse(await ReadBoundedAsync(configPath, 16384, token));
                if (config.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Service config must be an object.");
                service = Options.Parse(config.RootElement.EnumerateObject().SelectMany(p => new[] { "--" + p.Name, p.Value.GetString() ?? "" }).ToArray(), keys);
            }
            else service = Options.Parse(args[1..], keys);
            await ServeAsync(service, token); return 0;
        }
        string command; string? projectPath;
        if (args[0] == "export") { if (args.Length < 2) throw new ArgumentException("Project path is required."); command = "export"; projectPath = args[1]; args = args[2..]; }
        else
        {
            if (args.Length < 2) throw new ArgumentException("Missing subcommand.");
            command = args[0] + " " + args[1];
            if (command != "project new" && args.Length < 3) throw new ArgumentException("Project path is required.");
            projectPath = command == "project new" ? null : args[2];
            args = args[(projectPath is null ? 2 : 3)..];
        }
        string[] allowed = command switch {
            "project new" => ["output", "name", "width", "height", "duration"], "project info" or "project validate" => ["json"],
            "media import" => ["source", "output"], "media relink" => ["asset", "source", "output"],
            "edit apply" => ["commands", "output"], "render frame" => ["time", "output", "sequence"],
            "export" => ["output", "sequence", "encoder", "vaapi-device"], _ => throw new ArgumentException("Unknown command.") };
        var o = Options.Parse(args, allowed);
        if (command == "project new")
        {
            var output = o.Require("output"); CliOperations.NewOutput(output);
            using var session = new EditorSession();
            var result = session.Execute(new([new CreateProject(Guid.NewGuid(), o.Get("name") ?? "Untitled"),
                new CreateSequence(Guid.NewGuid(), "Main", new(int.Parse(o.Get("width") ?? "1920", CultureInfo.InvariantCulture),
                    int.Parse(o.Get("height") ?? "1080", CultureInfo.InvariantCulture), new(30, 1)),
                    TimelineTime.SecondsToTicks(decimal.Parse(o.Get("duration") ?? "10", CultureInfo.InvariantCulture)))]));
            if (!result.Success) { Write(result); return 1; }
            var json = ProjectJson.Serialize(session.GetProject().Project!);
            if (!json.Success) { Write(json.Diagnostics); return 1; }
            await CliOperations.PublishAsync(output, System.Text.Encoding.UTF8.GetBytes(json.Value!), token);
            Write(new { output = Path.GetFullPath(output), projectId = session.GetProject().Project!.Id }); return 0;
        }
        if (string.IsNullOrWhiteSpace(projectPath) || projectPath.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Project path is required.");
        bool mutates = command is "edit apply" or "media import" or "media relink";
        string? target = mutates ? o.Require("output") : null;
        if (target is not null) CliOperations.NewOutput(target);
        using var host = new HeadlessSession(projectPath, target, writable: mutates);
        await host.LoadAsync(token);
        var snapshot = host.Session.GetProject(); var project = snapshot.Project!;
        if (command == "project info")
        {
            Write(new { revision = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
                project = JsonSerializer.Deserialize<JsonElement>(ProjectJson.Serialize(project).Value!), media = CliOperations.InspectMedia(project, projectPath) }); return 0;
        }
        if (command == "project validate")
        {
            var diagnostics = ProjectValidator.Validate(project); var media = CliOperations.InspectMedia(project, projectPath);
            bool valid = diagnostics.IsEmpty && media.All(p => p.IsAvailable); Write(new { valid, diagnostics, media }); return valid ? 0 : 1;
        }
        if (command == "edit apply")
        {
            string commands = await ReadBoundedAsync(o.Require("commands"), 1024 * 1024, token);
            var input = JsonSerializer.Deserialize<JsonElement>(commands, new JsonSerializerOptions { MaxDepth = 64 });
            var grant = await host.Boundary.EnableAsync(McpPermission.Edit, token);
            var result = await host.Boundary.InvokeAsync(grant, "edit_batch", input, host.Guard(), token);
            Write(new { result.IsError, result.Error, result.Value });
            if (result.IsError || !host.Dirty) return 1;
            host.Save(); return 0;
        }
        if (command is "media import" or "media relink")
        {
            var probe = new FfprobeMediaProbe(); var source = o.Require("source");
            EditCommand change;
            if (command == "media import")
            {
                var probed = await probe.ProbeAsync(source, token);
                if (!probed.Success) { Write(probed.Diagnostics); return 1; }
                change = new RegisterMedia(probed.Value!.ToMediaAsset(Guid.NewGuid()));
            }
            else
            {
                var prepared = await new MediaRelinkService(probe).PrepareAsync(project, Guid.Parse(o.Require("asset")), source, token);
                if (!prepared.Success) { Write(prepared.Diagnostics); return 1; }
                change = prepared.Value!;
            }
            var result = host.Session.Execute(new([change], snapshot.Revision), token);
            if (result.Success) host.Save(); Write(result); return result.Success ? 0 : 1;
        }
        var sequence = o.Get("sequence") is { } seq ? project.Sequences.SingleOrDefault(s => s.Id == Guid.Parse(seq)) : project.Sequences.Length == 1 ? project.Sequences[0] : null;
        if (sequence is null) throw new ArgumentException("Specify a valid --sequence UUID (required for multiple sequences).");
        string destination = o.Require("output"); CliOperations.NewOutput(destination);
        if (command == "render frame")
        {
            var ext = Path.GetExtension(destination).ToLowerInvariant();
            string format = ext is ".jpg" or ".jpeg" ? "jpeg" : ext == ".png" ? "png" : throw new ArgumentException("Frame output must be .png or .jpg.");
            long ticks = TimelineTime.SecondsToTicks(decimal.Parse(o.Require("time"), CultureInfo.InvariantCulture));
            var bytes = await CliOperations.FrameAsync(project, projectPath, sequence.Id, ticks, format, token);
            await CliOperations.PublishAsync(destination, bytes, token); Write(new { output = destination, bytes = bytes.Length }); return 0;
        }
        var encoding = CliOperations.Encoding(o.Get("encoder"), o.Get("vaapi-device"));
        CliOperations.CheckCanvas(sequence); CliOperations.CheckMedia(project, projectPath);
        // Export backend can replace an existing output: isolate it then publish with no overwrite.
        string tempOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".kachinco-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var result = await CliOperations.ExportAsync(snapshot, projectPath,
                new(Guid.NewGuid(), sequence.Id, tempOutput, ExportPreset.YoutubeH264AacMp4, snapshot.Revision), new StderrProgress(), token, encoding);
            token.ThrowIfCancellationRequested();
            if (result.Stage == ExportStage.Completed) { File.Move(tempOutput, destination, false); result = result with { OutputPath = Path.GetFullPath(destination) }; }
            Write(result); return result.Stage == ExportStage.Completed ? 0 : 1;
        }
        finally { File.Delete(tempOutput); }
    }
    private static async Task ServeAsync(Options o, CancellationToken token)
    {
        string project = o.Require("project"), root = o.Require("output-root");
        var permission = o.Require("permission") switch { "edit" => McpPermission.Edit, "readOnly" => McpPermission.ReadOnly, _ => throw new ArgumentException("Permission must be edit or readOnly.") };
        var settings = new HubConnectionSettings(new Uri(o.Require("hub-endpoint"))); settings.Validate();
        string credentialFile = Path.GetFullPath(o.Require("credential-file"));
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(credentialFile) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("Credential file must be private (chmod 600).");
        string credential = (await ReadBoundedAsync(credentialFile, 4096, token)).Trim();
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))
            throw new UnauthorizedAccessException("Configure a nonempty connector credential in the private credential file.");
        using var host = new HeadlessSession(project, writable: true, autosave: permission == McpPermission.Edit, outputRoot: root,
            encoding: CliOperations.Encoding(o.Get("encoder"), o.Get("vaapi-device")));
        await host.LoadAsync(token);
        var grant = await host.Boundary.EnableAsync(permission, token);
        var provider = new HubConnectionProvider(settings, host.Boundary, grant, async ct => (await ReadBoundedAsync(credentialFile, 4096, ct)).Trim());
        try
        {
            await provider.StartAsync(token);
            Write(new { status = "started", permission = o.Require("permission"), runtimeId = host.Host.Snapshot.RuntimeId, note = "Hub connection is asynchronous; inspect Hub readiness separately." });
            // Provider has a bounded reconnect budget. Restart it after exhaustion;
            // this keeps a long-running service recoverable after a Hub outage.
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token);
                if (host.Boundary.Status.Current.LastError is not null) await provider.RefreshAsync(token);
            }
        }
        finally
        {
            host.Boundary.Disable(); await provider.StopAsync(CancellationToken.None);
            if (host.Jobs is not null) await host.Jobs.DisposeAsync();
            await host.Host.InvokeAsync(() => { if (host.Dirty) host.Save(); return true; }, CancellationToken.None);
        }
    }
    internal static async Task<string> ReadBoundedAsync(string path, int max, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        if (file.Length > max) throw new InvalidDataException("Input file exceeds its size limit.");
        using var memory = new MemoryStream(); byte[] bytes = new byte[8192]; int n;
        while ((n = await file.ReadAsync(bytes, token)) > 0) { if (memory.Length + n > max) throw new InvalidDataException("Input file exceeds its size limit."); memory.Write(bytes, 0, n); }
        return new System.Text.UTF8Encoding(false, true).GetString(memory.ToArray());
    }
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    private static void Error(string code, string? message = null) => Console.Error.WriteLine(JsonSerializer.Serialize(new { error = code, message }, Json));
    private sealed class StderrProgress : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => Console.Error.WriteLine(JsonSerializer.Serialize(value, Json)); }
    private sealed class LongStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            long.Parse(reader.GetString() ?? throw new JsonException(), CultureInfo.InvariantCulture);
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
    private sealed class Options
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public string? Get(string key) => values.GetValueOrDefault(key);
        public string Require(string key) => Get(key) ?? throw new ArgumentException("Missing --" + key);
        public static Options Parse(string[] args, params string[] allowed)
        {
            var result = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..])) throw new ArgumentException("Unknown option: " + args[i]);
                string key = args[i][2..];
                string value = key == "json" ? "true" : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException("Missing option value.");
                if (!result.values.TryAdd(key, value)) throw new ArgumentException("Duplicate option: " + key);
            }
            return result;
        }
    }
}
