using System.Diagnostics;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;

// Real codec/backend preparation measurements. Physical WPF/clock acceptance is separate.
internal static class GpuPreviewBench
{
    public static async Task Run(string directory, string output)
    {
        Directory.CreateDirectory(directory);
        string media = Path.GetFullPath(Path.Combine(directory, "gpu-preview-1080p.mov"));
        if (!File.Exists(media))
        {
            var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-f", "lavfi", "-i",
                "testsrc2=s=1920x1080:r=30:d=3", "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-pix_fmt", "yuv420p", media })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("FFmpeg did not start.");
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(await error);
        }
        var rows = new List<object>();
        foreach (bool layered in new[] { false, true })
        {
            using var session = new EditorSession();
            var sequence = Guid.NewGuid(); var asset = Guid.NewGuid(); var track = Guid.NewGuid();
            var commands = new List<EditCommand> {
                new CreateProject(Guid.NewGuid(), "GPU benchmark"),
                new CreateSequence(sequence, "1080p", SequenceSettings.Landscape, 3 * TimelineTime.TicksPerSecond),
                new RegisterMedia(new(asset, "Generated H.264", media, MediaKind.Mov, 3 * TimelineTime.TicksPerSecond)),
                new AddTrack(sequence, track, "V1", TrackKind.Video),
                new InsertClip(sequence, track, new(Guid.NewGuid(), asset, 0, 0, 3 * TimelineTime.TicksPerSecond, true,
                    ClipAppearance.Default, AudioProperties.Default)) };
            if (layered)
            {
                var overlay = Guid.NewGuid();
                commands.Add(new AddTrack(sequence, overlay, "V2", TrackKind.Video));
                commands.Add(new InsertClip(sequence, overlay, new(Guid.NewGuid(), asset, 0, 0, 3 * TimelineTime.TicksPerSecond, true,
                    new(new(180, 90, .75, .8, 12), .65, BlendMode.Screen), AudioProperties.Default)));
            }
            var edited = session.Execute(new([.. commands]));
            if (!edited.Success) throw new InvalidDataException(string.Join(" / ", edited.Diagnostics));
            var context = PreviewContext.Create(session.GetProject(), sequence).Value!;
            foreach (var preference in new[] { PreviewBackendPreference.Cpu, PreviewBackendPreference.D3D11, PreviewBackendPreference.Auto })
            foreach (var quality in new[] { PreviewQuality.Full, PreviewQuality.Half, PreviewQuality.Quarter })
            {
                using var source = new InteractivePreviewSource(backendPreference: preference);
                var durations = new List<double>();
                using var process = Process.GetCurrentProcess();
                TimeSpan startCpu = process.TotalProcessorTime;
                var wall = Stopwatch.StartNew();
                for (int i = 0; i < 24; i++)
                {
                    var request = Stopwatch.StartNew();
                    var result = await source.FrameAsync(context, TimelineTime.FrameToTicks(i, new(30, 1)), quality, true, default);
                    if (!result.Success) throw new InvalidDataException(string.Join(" / ", result.Diagnostics));
                    await PreviewPresentation.PrepareAsync(result.Value!, default);
                    durations.Add(request.Elapsed.TotalMilliseconds);
                }
                wall.Stop(); process.Refresh();
                double editorCpu = (process.TotalProcessorTime - startCpu).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
                var backend = source.BackendDiagnostics;
                var decode = source.DecodeDiagnostics;
                var seeks = new List<double>();
                foreach (int frame in new[] { 70, 7, 40, 1 })
                {
                    await source.ResetAsync(); source.Frames.Clear();
                    var request = Stopwatch.StartNew();
                    var result = await source.FrameAsync(context, TimelineTime.FrameToTicks(frame, new(30, 1)), quality, false, default);
                    if (!result.Success) throw new InvalidDataException(string.Join(" / ", result.Diagnostics));
                    seeks.Add(request.Elapsed.TotalMilliseconds);
                }
                rows.Add(new { scenario = layered ? "layered_transform_screen" : "identity", preference = preference.ToString(),
                    quality = quality.ToString(), frames = durations.Count, preparationAndBgra = Timing(durations),
                    preparedFramesPerSecond = durations.Count / wall.Elapsed.TotalSeconds,
                    editorCpuPercentOneCore = editorCpu, editorWorkingSetBytes = process.WorkingSet64,
                    backend, decode, coldSoftwareSeek = Timing(seeks) });
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { os = Environment.OSVersion.ToString(),
            note = "Preparation/BGRA wall time; no WPF presentation FPS/drop claim. CPU is editor parent only, excluding FFmpeg children. Compositor bytes exclude decoder surfaces/driver overhead. Record physical GPU counters separately.",
            results = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(Path.GetFullPath(output));
    }
    private static object Timing(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { averageMs = values.Average(), p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], maximumMs = values.Max() };
    }
}
