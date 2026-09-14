using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class InteractivePreviewChecks
{
    private const long T = TimelineTime.TicksPerSecond;
    public static async Task Run(MainWindow main)
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-interactive-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        var evidence = new List<object>();
        try
        {
            string mov = Path.Combine(dir, "source.mov"), wav = Path.Combine(dir, "source.wav");
            await Ffmpeg(["-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=30:d=3", "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-pix_fmt", "yuv420p", mov]);
            await Ffmpeg(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3", "-c:a", "pcm_s16le", wav]);
            var fixture = Create(mov, wav, 219.6m, 110 * T);
            var context = PreviewContext.Create(fixture.Session.GetProject(), fixture.Sequence).Value!;
            foreach (var quality in new[] {PreviewQuality.Full, PreviewQuality.Half, PreviewQuality.Quarter})
            {
                using var video = new FfmpegForwardDecoder(); using var audio = new FfmpegForwardDecoder();
                using var source = new InteractivePreviewSource(new WindowsCaptionRasterizer(main.Dispatcher), forwardVideo: video, forwardAudio: audio);
                var watch = Stopwatch.StartNew();
                var cold = await source.FrameAsync(context, 111 * T, quality, false, default); Require(cold.Success, "Cold scrub: " + string.Join(" / ", cold.Diagnostics));
                double coldMs = watch.Elapsed.TotalMilliseconds; watch.Restart();
                var warm = await source.FrameAsync(context, 111 * T, quality, false, default); double cachedMs = watch.Elapsed.TotalMilliseconds;
                Require(ReferenceEquals(cold.Value, warm.Value), "Cached frame is reused.");
                long bytes = 0; var cpu = Process.GetCurrentProcess().TotalProcessorTime; watch.Restart();
                for (int i = 0; i < 60; i++)
                {
                    var frame = await source.FrameAsync(context, 110 * T + TimelineTime.FrameToTicks(i, SequenceSettings.Landscape.FrameRate), quality, true, default);
                    Require(frame.Success, "Forward video: " + string.Join(" / ", frame.Diagnostics));
                    if (i % 3 == 0)
                    {
                        var pcm = await source.AudioAsync(context, 110 * 48000 + i * 1600, 4800, default);
                        Require(pcm.Success && pcm.Value!.Samples.Any(x => x != 0), "Mixed PCM must contain the WAV.");
                    }
                    bytes = Math.Max(bytes, Process.GetCurrentProcess().WorkingSet64 + Process.GetProcessesByName("ffmpeg").Sum(Memory));
                }
                evidence.Add(new { kind = "decode_throughput_not_device_playback", quality = quality.ToString(), coldScrubMs = coldMs, cachedScrubMs = cachedMs,
                    frames = 60, elapsedMs = watch.Elapsed.TotalMilliseconds, hostCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
                    sampledHostAndFfmpegPeakBytes = bytes, cache = source.Frames.Statistics, videoProcesses = video.ProcessStarts, audioProcesses = audio.ProcessStarts });
                Require(source.Frames.Statistics.Bytes <= 96 * 1024 * 1024 && source.Audio.Statistics.Bytes <= 8 * 1024 * 1024, "Cache byte bounds.");
                foreach (decimal duration in new[] { 8m, 219.6m })
                {
                    var playbackFixture = Create(mov, wav, duration, 4 * T);
                    var playContext = PreviewContext.Create(playbackFixture.Session.GetProject(), playbackFixture.Sequence).Value!;
                    source.Frames.Clear(); source.Audio.Clear(); watch.Restart();
                    // Real codec work can be measured even when this runner has no output device.
                    // This is preparation latency, explicitly not audible/visible Play acceptance.
                    var firstDecode = source.FrameAsync(playContext, 4 * T, quality, true, default).AsTask();
                    var audioPrime = PrimeAudio();
                    var decoded = await firstDecode; Require(decoded.Success, "Startup evaluated frame.");
                    double frameReady = watch.Elapsed.TotalMilliseconds; double pcmReady = await audioPrime;
                    evidence.Add(new { kind = "forward_startup_decode_only", quality = quality.ToString(), duration,
                        timelineStartSeconds = 4, frameReadyMs = frameReady, pcmReadyMs = pcmReady,
                        frameEntries = source.Frames.Statistics.Entries, audioEntries = source.Audio.Statistics.Entries });
                    Require(source.Frames.Statistics.Entries == 1 && source.Audio.Statistics.Entries == 2, "Startup work must be independent of sequence duration.");
                    async Task<double> PrimeAudio()
                    {
                        for (int block = 0; block < 2; block++)
                        {
                            var pcm = await source.AudioAsync(playContext, 4 * 48000 + block * 4800, 4800, default);
                            Require(pcm.Success, "Startup PCM.");
                        }
                        return watch.Elapsed.TotalMilliseconds;
                    }
                    using var controller = new InteractivePreview(source, () => new WindowsPreviewAudioOutput());
                    controller.SetQuality(quality); controller.SetContext(playContext); await controller.Completion;
                    controller.Scrub(4 * T); await controller.Completion; source.Frames.Clear(); source.Audio.Clear();
                    double? firstFrame = null; watch.Restart();
                    controller.Changed += (_, _) => { if (controller.Frame is not null && firstFrame is null) firstFrame = watch.Elapsed.TotalMilliseconds; };
                    controller.Play(); await Until(() => controller.State is InteractivePreviewState.Playing or InteractivePreviewState.Failed);
                    if (controller.State == InteractivePreviewState.Failed)
                    {
                        // No synthetic timer or fake sink may produce audio/perceptual acceptance evidence.
                        evidence.Add(new { kind = "native_playback_unavailable", quality = quality.ToString(), duration, error = controller.Error });
                    }
                    else
                    {
                        await Until(() => controller.ReadPositionTicks() > 4 * T || controller.State == InteractivePreviewState.Failed);
                        double firstAudio = watch.Elapsed.TotalMilliseconds;
                        await Until(() => controller.ReadPositionTicks() >= 6 * T || controller.State == InteractivePreviewState.Failed);
                        Require(controller.State != InteractivePreviewState.Failed, controller.Error ?? "Playback failed.");
                        evidence.Add(new { kind = "native_device_playback", quality = quality.ToString(), duration, firstFrameMs = firstFrame,
                            firstConsumedAudioMs = firstAudio, elapsedMs = watch.Elapsed.TotalMilliseconds, controller.DroppedVideoFrames, controller.Underruns });
                    }
                    controller.Dispose(); await controller.Completion;
                }
            }
            await ViewerAndStrip(main, mov, wav);
        }
        finally
        {
            Directory.CreateDirectory("artifacts");
            string json = JsonSerializer.Serialize(new { environment = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
                note = "CI runner measurements, not human audiovisual acceptance. Host CPU excludes FFmpeg. No audio-device results are inferred from decode throughput.", results = evidence }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText("artifacts/interactive-preview-metrics.json", json); Console.WriteLine("INTERACTIVE_METRICS " + json);
            Directory.Delete(dir, true);
        }
    }
    private static async Task ViewerAndStrip(MainWindow main, string mov, string wav)
    {
        var fixture = Create(mov, wav, 219.6m, 0);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = (EditorSession)typeof(MainWindow).GetField("session", flags)!.GetValue(main)!;
        Require(session.ReplaceProject(fixture.Session.GetProject().Project!).Success, "Viewer fixture session.");
        typeof(MainWindow).GetField("savedJson", flags)!.SetValue(main, ProjectJson.Serialize(session.GetProject().Project!).Value); // Fixture is clean; smoke teardown must not open a discard dialog.
        typeof(MainWindow).GetField("selectedSequenceId", flags)!.SetValue(main, fixture.Sequence);
        typeof(MainWindow).GetMethod("Refresh", flags)!.Invoke(main, new object?[] { null });
        var timeline = (TimelineSurface)main.FindName("Timeline");
        var viewer = (Image)main.FindName("PreviewImage");
        var controller = (InteractivePreview)typeof(MainWindow).GetField("playback", flags)!.GetValue(main)!;
        await controller.Completion; Require(viewer.Source is BitmapSource, "Viewer receives initial evaluated frame.");
        byte[] before = Pixels((BitmapSource)viewer.Source);
        timeline.SetCursorTicks(T); await controller.Completion;
        Require(controller.Frame?.Tick == T && viewer.Source is BitmapSource, "Playhead change must request the exact timeline time.");
        Require(!before.SequenceEqual(Pixels((BitmapSource)viewer.Source)), "Scrubbing actual source updates viewer pixels.");
        for (int i = 0; i < 100; i++) timeline.SetCursorTicks(T + i * T / 100);
        await controller.Completion; Require(controller.Frame?.Tick == T + 99 * T / 100, "Rapid scrub latest frame wins.");
        await Until(() => timeline.VisualizationWorkers == 0);
        var canvas = (Canvas)timeline.FindName("TimelineCanvas");
        int images = Descendants(canvas).OfType<Image>().Count(i => i.Source is BitmapSource);
        Require(images > 1, "MOV strip contains multiple actual source thumbnails.");
        foreach (decimal zoom in new[] { 2m, .5m, 4m, .25m })
        {
            timeline.ZoomBy(zoom); ((ScrollBar)timeline.FindName("HorizontalScroll")).Value = 50;
            Require(timeline.VisualizationWorkers <= 2 && timeline.PendingVisualizations <= 96, "Viewport work is bounded.");
        }
        await Until(() => timeline.VisualizationWorkers == 0);
        Require(timeline.VisualizationCache.Bytes <= 16 * 1024 * 1024, "Thumbnail cache is bounded.");
        controller.Dispose(); await controller.Completion; timeline.DisposeVisualizations();
        Console.WriteLine("WPF viewer playhead -> evaluated MOV pixels, latest scrub, thumbnail strip/work bounds: PASS");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    private static byte[] Pixels(BitmapSource bitmap) { var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes; }
    private static long Memory(Process process) { using (process) { try { return process.WorkingSet64; } catch { return 0; } } }
    private static (EditorSession Session, Guid Sequence) Create(string mov, string wav, decimal duration, long start)
    {
        var session = new EditorSession(); var seq = Guid.NewGuid(); var v = Guid.NewGuid(); var a = Guid.NewGuid(); var vt = Guid.NewGuid(); var at = Guid.NewGuid();
        Require(session.Execute(new([
            new CreateProject(Guid.NewGuid(), "Interactive fixture"), new CreateSequence(seq, "Landscape", SequenceSettings.Landscape, TimelineTime.SecondsToTicks(duration)),
            new RegisterMedia(new(v, "Test MOV", mov, MediaKind.Mov, 3 * T)), new RegisterMedia(new(a, "Test WAV", wav, MediaKind.Wav, 3 * T, 48000, 1)),
            new AddTrack(seq, at, "A1", TrackKind.Audio), new AddTrack(seq, vt, "V1", TrackKind.Video),
            new InsertClip(seq, vt, new(Guid.NewGuid(), v, start, 0, 3 * T, true, ClipAppearance.Default, AudioProperties.Default)),
            new InsertClip(seq, at, new(Guid.NewGuid(), a, start, 0, 3 * T, true, ClipAppearance.Default, AudioProperties.Default))
        ])).Success, "Interactive fixture is valid."); return (session, seq);
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new Exception("Interactive check timed out."); await Task.Delay(10); }
    }
    private static async Task Ffmpeg(string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1" }.Concat(args)) info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!; var error = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); Require(p.ExitCode == 0, await error);
    }
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
}
