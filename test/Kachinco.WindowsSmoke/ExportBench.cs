using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class ExportBench
{
    public static async Task Run(string projectFile, string outputDirectory, int device)
    {
        string projectPath = Path.GetFullPath(projectFile);
        var loaded = await new ProjectFileStore().LoadAsync(projectPath);
        if (!loaded.Success) throw new InvalidDataException(string.Join(";", loaded.Diagnostics.Select(d => d.Message)));
        if (loaded.Value!.Sequences.Length != 1) throw new ArgumentException("Use a project with exactly one sequence for this comparison.");
        using var session = new EditorSession();
        if (!session.ReplaceProject(loaded.Value).Success) throw new InvalidDataException("Invalid project.");
        var snapshot = session.GetProject(); var sequence = loaded.Value.Sequences[0];
        string directory = Path.Combine(Path.GetFullPath(outputDirectory), "export-bench-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        var rows = new List<object>();
        try
        {
            foreach (var composition in new[] { PreviewBackendPreference.Cpu, PreviewBackendPreference.D3D11 })
            foreach (var encoder in new[] { VideoEncoderPreference.Cpu, VideoEncoderPreference.Nvenc })
            {
                cancel.Token.ThrowIfCancellationRequested();
                string name = $"{composition}-{encoder}.mp4";
                using var decoder = new FfmpegMediaDecoder();
                using var renderer = new ExportFrameRenderer(decoder, projectPath, new WindowsCaptionRasterizer(Application.Current.Dispatcher), composition);
                var service = new SnapshotExportService(renderer, new SharedAudioRenderer(decoder, projectPath), new FfmpegEncodingBackend(), new(null, encoder, device), projectPath);
                Console.WriteLine($"Export {name}");
                var result = await Task.Run(() => service.ExportAsync(snapshot,
                    new(Guid.NewGuid(), sequence.Id, Path.Combine(directory, name), ExportPreset.YoutubeH264AacMp4, snapshot.Revision), null, cancel.Token));
                rows.Add(new { requestedComposition = composition.ToString(), requestedEncoder = encoder.ToString(), output = name,
                    stage = result.Stage.ToString(), result.Metrics, result.Diagnostics,
                    bytes = result.OutputPath is { } output ? new FileInfo(output).Length : 0 });
                await File.WriteAllTextAsync(Path.Combine(directory, "measurements.json"), JsonSerializer.Serialize(new
                {
                    os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
                    sequence.Settings, sequence.DurationTicks, nvencDevice = device,
                    note = "Video pipeline includes rendering, uploads/readback, pipe backpressure and encoder drain. CPU/GPU/total VRAM require external observation. Inspect actual metrics for fallback; requested modes are not proof of acceleration.", rows
                }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(result.Metrics));
                if (result.Stage != ExportStage.Completed) throw new InvalidDataException(string.Join(";", result.Diagnostics.Select(d => d.Message)));
            }
            Console.WriteLine("Comparison saved: " + directory);
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
