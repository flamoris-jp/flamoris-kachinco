using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;

internal static partial class PackagedMcpChecks
{
    // Separate from the existing System32-only MCP proof: this checks the actual
    // documented portable deployment with externally installed FFmpeg/ffprobe.
    public static async Task RunMediaImport(string bundle)
    {
        string ffmpeg = FindMediaExecutable("ffmpeg.exe"), ffprobe = FindMediaExecutable("ffprobe.exe");
        string directory = Path.Combine(Path.GetTempPath(), "kachinco-package-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Path.Combine(bundle, "Kachinco.App.exe")) { UseShellExecute = false, WorkingDirectory = bundle };
        start.Environment["PATH"] = string.Join(';', new[] { CleanPath, Path.GetDirectoryName(ffmpeg), Path.GetDirectoryName(ffprobe) }.Distinct());
        using var process = Process.Start(start)!;
        try
        {
            AutomationElement? main = null;
            await Until(() => { process.Refresh(); Check(!process.HasExited, "Published editor exited during media acceptance.");
                if (process.MainWindowHandle == 0) return false;
                main = AutomationElement.FromHandle(process.MainWindowHandle); return main is not null; });
            await Menu(main!, "FileMenu", "NewLandscapeMenu");
            await Menu(main!, "McpMenu", "McpReadOnlyMenu");
            string pipe = await Connection(process.Id);
            await using var connection = new RevokedClient(await Connect(Path.Combine(bundle, "mcp", "Flamoris.Mcp.Bridge.exe"), pipe));
            int count = 0;
            foreach (var (extension, codec, video) in new[] {
                ("mov", "mpeg4", true), ("mp4", "mpeg4", true), ("wav", "pcm_s16le", false),
                ("mp3", "libmp3lame", false), ("m4a", "aac", false) })
            {
                string path = Path.Combine(directory, "source with spaces." + extension);
                await GenerateMediaFixture(ffmpeg, path, codec, video);
                await ImportThroughPublishedPicker(main!, process.Id, path);
                var assets = Assets(await Query(connection.Client));
                Check(assets.GetArrayLength() == ++count, "Published picker failed to import " + extension);
                var asset = assets.EnumerateArray().Single(a => a.GetProperty("sourcePath").GetString() == path);
                Check(asset.GetProperty("kind").GetString() == (video ? "Mov" : "Wav"), "Published media kind mismatch.");
                Check(long.Parse(asset.GetProperty("durationTicks").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 0,
                    "Published import did not obtain real probe duration.");
                Guid id = asset.GetProperty("id").GetGuid();
                await Until(() => VisibleText(main!, extension.ToUpperInvariant()));
                await Invoke(Find(main!, "UndoButton"));
                Check(Assets(await Query(connection.Client)).GetArrayLength() == count - 1, "Published import Undo failed.");
                await Invoke(Find(main!, "RedoButton"));
                Check(Assets(await Query(connection.Client)).EnumerateArray().Any(a => a.GetProperty("id").GetGuid() == id), "Published import Redo changed identity.");
            }
            string invalid = Path.Combine(directory, "invalid.mp4");
            await File.WriteAllTextAsync(invalid, "invalid media");
            await ImportThroughPublishedPicker(main!, process.Id, invalid);
            Check(Assets(await Query(connection.Client)).GetArrayLength() == count, "Invalid file mutated published Project.");
            Console.WriteLine("Published MOV/MP4/WAV/MP3/M4A picker -> real ffprobe -> shared session, visible labels, stable Undo/Redo and invalid-file rejection: PASS. FFmpeg/ffprobe use explicit external PATH directories; binaries are not bundled.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            Directory.Delete(directory, true);
        }
    }

    private static JsonElement Assets(JsonElement state) => state.GetProperty("project").GetProperty("project").GetProperty("assets");

    private static async Task ImportThroughPublishedPicker(AutomationElement main, int processId, string path)
    {
        var importing = Invoke(Find(main, "ImportButton"));
        await ChooseProjectFile(processId, path);
        await importing;
        await Until(() => Find(main, "ImportButton").Current.IsEnabled);
    }

    private static string FindMediaExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), name)).FirstOrDefault(File.Exists)
        ?? throw new Exception("Required external media runtime missing: " + name);

    private static async Task GenerateMediaFixture(string ffmpeg, string path, string codec, bool video)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = new(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        string[] args = video
            ? ["-v", "error", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=30", "-t", "0.3", "-c:v", codec, "-an", path]
            : ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "0.3", "-c:a", codec, path];
        foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Check(process.ExitCode == 0, "Could not generate packaged media fixture: " + await error);
    }
}
