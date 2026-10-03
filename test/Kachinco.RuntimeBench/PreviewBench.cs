using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;

// Copy this same driver to the instrumented baseline to compare actual implementations.
internal static class PreviewBench
{
    public static async Task Run(string directory, string output, string revision)
    {
        Directory.CreateDirectory(directory);
        string mov = Path.GetFullPath(Path.Combine(directory, "preview.mov"));
        if (!File.Exists(mov))
        {
            var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-f", "lavfi", "-i",
                "testsrc2=s=1920x1080:r=30:d=3", "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-pix_fmt", "yuv420p", mov }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception(await errors);
        }
        using var session = new EditorSession();
        var sequence = Guid.NewGuid(); var track = Guid.NewGuid(); var asset = Guid.NewGuid();
        if (!session.Execute(new([new CreateProject(Guid.NewGuid(), "Preview benchmark"),
            new CreateSequence(sequence, "Video", SequenceSettings.Landscape, 3 * TimelineTime.TicksPerSecond),
            new RegisterMedia(new(asset, "Generated MOV", mov, MediaKind.Mov, 3 * TimelineTime.TicksPerSecond)),
            new AddTrack(sequence, track, "V1", TrackKind.Video),
            new InsertClip(sequence, track, new(Guid.NewGuid(), asset, 0, 0, 3 * TimelineTime.TicksPerSecond, true, ClipAppearance.Default, AudioProperties.Default))])).Success)
            throw new Exception("Invalid benchmark fixture.");
        var context = PreviewContext.Create(session.GetProject(), sequence).Value!;
        var rows = new List<object>();
        var prepare = typeof(InteractivePreview).Assembly.GetType("Kachinco.Infrastructure.PreviewPresentation")?.GetMethod("PrepareAsync");
        foreach (var quality in new[] { PreviewQuality.Full, PreviewQuality.Half, PreviewQuality.Quarter })
        {
            using var source = new InteractivePreviewSource();
            var times = new List<double>(); var conversions = new List<double>(); RenderedVideoFrame? last = null;
            for (int i = 0; i < 60; i++)
            {
                var watch = Stopwatch.StartNew();
                var frame = await source.FrameAsync(context, TimelineTime.FrameToTicks(i, new(30, 1)), quality, true, default);
                if (!frame.Success) throw new Exception(string.Join(" / ", frame.Diagnostics));
                times.Add(watch.Elapsed.TotalMilliseconds); last = frame.Value!;
                watch.Restart();
                if (prepare is null)
                {
                    var bytes = last.Rgba8.ToArray();
                    for (int at = 0; at < bytes.Length; at += 4) (bytes[at], bytes[at + 2]) = (bytes[at + 2], bytes[at]);
                    GC.KeepAlive(bytes);
                }
                else await (Task)prepare.Invoke(null, [last, CancellationToken.None])!;
                conversions.Add(watch.Elapsed.TotalMilliseconds);
            }
            var cacheTimes = new List<double>();
            for (int i = 0; i < 10; i++)
            { var watch = Stopwatch.StartNew(); await source.FrameAsync(context, last!.Tick, quality, true, default); cacheTimes.Add(watch.Elapsed.TotalMilliseconds); }
            rows.Add(new { quality = quality.ToString(), frames = 60, preparation = Timing(times), cacheHit = Timing(cacheTimes),
                pixelPreparation = Timing(conversions), conversionLocation = prepare is null ? "old_managed_UI_pattern" : "native_worker_including_task_and_reflection_overhead",
                perPresentationUiPixelArrayBytes = prepare is null ? last!.Rgba8.Length : 0, cache = source.Frames.Statistics });
        }
        var gate = GatedPresentation(context);
        File.WriteAllText(output, JsonSerializer.Serialize(new { revision, os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
            note = "Generated 1080p H264 MOV; decode and pixel-preparation wall times, no WPF WritePixels or physical A/V claim. Gated playback uses consumed samples from a deterministic test device.", results = rows, gatedPresentation = gate },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(output));
    }
    private static object Timing(List<double> values)
    { var sorted = values.Order().ToArray(); return new { averageMs = values.Average(), p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], maximumMs = values.Max() }; }
    private static object GatedPresentation(PreviewContext context)
    {
        using var pump = new Pump(); var source = new GatedSource(); var device = new Device();
        using var preview = new InteractivePreview(source, () => device);
        preview.SetContext(context); pump.Until(() => preview.Completion.IsCompleted);
        preview.Play(); pump.Until(() => source.Pending is not null);
        device.Advance(1600);
        var watch = Stopwatch.StartNew();
        while (preview.Frame?.Tick != TimelineTime.FrameToTicks(1, new(30, 1)) && watch.ElapsedMilliseconds < 200)
        { pump.Drain(); Thread.Sleep(1); }
        long? displayed = preview.Frame?.Tick; long clock = preview.ReadPositionTicks(); bool blocked = !source.Pending!.Task.IsCompleted;
        source.Hold = false; source.Pending.SetResult(Frame(TimelineTime.FrameToTicks(2, new(30, 1))));
        preview.Dispose(); pump.Until(() => preview.Completion.IsCompleted);
        return new { clockTick = clock, presentedTickWhileNextDecodeBlocked = displayed, producerStillBlocked = blocked,
            preview.DroppedVideoFrames, preview.Underruns };
    }
    private static Result<RenderedVideoFrame> Frame(long tick) => Result<RenderedVideoFrame>.Ok(new(0, tick, 1, 1, [1, 2, 3, 255]));
    private sealed class GatedSource : IInteractivePreviewSource
    {
        public bool Hold = true;
        public TaskCompletionSource<Result<RenderedVideoFrame>>? Pending;
        public ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext c, long tick, PreviewQuality q, bool forward, CancellationToken token)
        {
            if (forward && Hold && tick >= TimelineTime.FrameToTicks(2, new(30, 1)))
            { Pending = new(); return new(Pending.Task); }
            return ValueTask.FromResult(Frame(tick));
        }
        public ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext c, long first, int count, CancellationToken token) =>
            ValueTask.FromResult(Result<RenderedAudioBlock>.Ok(new(first, 48000, 2, new float[count * 2].ToImmutableArray())));
    }
    private sealed class Device : IPreviewAudioOutput
    {
        private long submitted; private bool playing;
        public long PlayedFrames { get; private set; }
        public long QueuedFrames => submitted - PlayedFrames;
        public void Advance(long count) { if (playing) PlayedFrames = Math.Min(submitted, PlayedFrames + count); }
        public void Enqueue(RenderedAudioBlock block) => submitted += block.Samples.Length / 2;
        public void SetMonitoringGain(double gain) { }
        public void Play() => playing = true; public void Pause() => playing = false; public void Dispose() => playing = false;
    }
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? previous = Current;
        private readonly ConcurrentQueue<Action> callbacks = new();
        public Pump() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback d, object? state) => callbacks.Enqueue(() => d(state));
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
        public void Until(Func<bool> complete)
        { var watch = Stopwatch.StartNew(); while (!complete()) { Drain(); if (watch.Elapsed.TotalSeconds > 10) throw new Exception("Benchmark timed out."); Thread.Sleep(1); } Drain(); }
        public void Dispose() => SetSynchronizationContext(previous);
    }
}
