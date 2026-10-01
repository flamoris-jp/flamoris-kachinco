using Kachinco.Core;
using System.Diagnostics;
using Flamoris.Logging;

namespace Kachinco.Infrastructure;

public interface IPreviewAudioOutput : IDisposable
{
    // Stereo sample FRAMES, consumed by the device since this output was opened.
    long PlayedFrames { get; }
    long QueuedFrames { get; }
    void Enqueue(RenderedAudioBlock block);
    void Play();
    void Pause();
}

public enum InteractivePreviewState { Stopped, Scrubbing, Buffering, Playing, Paused, Failed }

// Host calls/callbacks are serialized (Dispatcher in WPF). One mailbox runner, one video
// request and one audio request; replacing intent cancels and joins old work before starting.
public sealed class InteractivePreview(IInteractivePreviewSource source, Func<IPreviewAudioOutput> outputFactory,
    FlamorisLogger? logger = null) : IDisposable
{
    public const int AudioBlockFrames = 4800;
    public const int StartupFrames = 9600;
    public const int MaximumQueuedFrames = 24000;
    public const int ForwardVideoFrames = 3;
    public event EventHandler? Changed;
    public InteractivePreviewState State { get; private set; }
    public RenderedVideoFrame? Frame { get; private set; }
    public PreviewPresentation? Presentation { get; private set; }
    public PreviewWorkMetrics ConversionPerformance { get; } = new();
    public PreviewWorkMetrics PresentationPerformance { get; } = new();
    public int ReadyVideoFrames { get; private set; }
    public int MaximumVideoFrames { get; private set; }
    public long RequestedVideoTick { get; private set; }
    public string? Error { get; private set; }
    public long PositionTicks { get; private set; }
    public long DroppedVideoFrames { get; private set; }
    public long Underruns { get; private set; }
    public PreviewQuality Quality { get; private set; } = PreviewQuality.Half;
    public bool HasPendingRequest => pending is not null;
    public Task Completion => runner ?? Task.CompletedTask;
    private PreviewContext? context;
    private Request? pending;
    private CancellationTokenSource? active;
    private Task? runner;
    private bool running, disposed, wantPlay;
    private long generation;
    private readonly Kachinco.Native.NativePlayback native = new();
    private Kachinco.Native.NativePlaybackTicket ticket;
    private IPreviewAudioOutput? output;
    private bool playSession;
    private Guid? playbackSessionId;
    private sealed record Request(long Generation, long Tick, bool Play, InteractivePreviewState After);

    public void SetContext(PreviewContext? value)
    {
        if (disposed || ReferenceEquals(context, value)) return;
        var old = context;
        long tick = ReadPositionTicks();
        if (value is not null && old is not null && playSession &&
            value.Sequence.Id == old.Sequence.Id && value.Project.Id == old.Project.Id &&
            tick < value.Sequence.DurationTicks && old.WindowKey(tick, SaturatingEnd(tick)) == value.WindowKey(tick, SaturatingEnd(tick)))
        { context = value; return; }
        context = value;
        if (value is null) { Cancel(); Frame = null; Presentation = null; PositionTicks = 0; SetState(InteractivePreviewState.Stopped); return; }
        bool resume = (playSession || pending?.Play == true) && wantPlay && old?.Sequence.Id == value.Sequence.Id && old.Project.Id == value.Project.Id;
        long next = old?.Sequence.Id == value.Sequence.Id && old.Project.Id == value.Project.Id ? Math.Min(tick, value.Sequence.DurationTicks - 1) : 0;
        RequestFrame(next, resume, resume ? InteractivePreviewState.Playing : InteractivePreviewState.Paused);
    }
    private static long SaturatingEnd(long tick) => tick > long.MaxValue - TimelineTime.TicksPerSecond ? long.MaxValue : tick + TimelineTime.TicksPerSecond;

    public void Scrub(long tick) => RequestFrame(tick, false, InteractivePreviewState.Paused);
    public void Play()
    {
        if (context is null || disposed) return;
        RequestFrame(PositionTicks, true, InteractivePreviewState.Playing);
    }
    public void Pause()
    {
        wantPlay = false;
        try
        {
            output?.Pause();
            long position = ReadPositionTicks();
            if (playSession || pending?.Play == true || running)
            {
                RequestFrame(position, false, InteractivePreviewState.Paused);
                SetState(InteractivePreviewState.Paused);
            }
        }
        catch (Exception e) { Fail(e); }
    }
    public void Stop() => RequestFrame(0, false, InteractivePreviewState.Stopped);
    public void SetQuality(PreviewQuality value)
    {
        if (value is not (PreviewQuality.Full or PreviewQuality.Half or PreviewQuality.Quarter)) throw new ArgumentOutOfRangeException(nameof(value));
        if (Quality == value) return;
        Quality = value; bool resume = (playSession || pending?.Play == true) && wantPlay;
        RequestFrame(ReadPositionTicks(), resume, resume ? InteractivePreviewState.Playing : InteractivePreviewState.Paused);
    }
    public long ReadPositionTicks()
    {
        try
        {
            if (playSession && output is not null && context is not null)
                PositionTicks = native.Clock(generation, output.PlayedFrames);
        }
        catch (Exception e) { Fail(e); }
        return PositionTicks;
    }
    private void RequestFrame(long tick, bool play, InteractivePreviewState after)
    {
        if (disposed) return;
        Cancel(); Error = null; Frame = null; Presentation = null;
        if (context is null) { PositionTicks = 0; SetState(InteractivePreviewState.Stopped); return; }
        ticket = native.Request(context.Sequence.DurationTicks, context.Sequence.Settings.FrameRate.Numerator,
            context.Sequence.Settings.FrameRate.Denominator, tick, play);
        generation = ticket.Generation; PositionTicks = ticket.Position;
        long renderTick = ticket.RenderTick;
        wantPlay = play;
        pending = new(generation, renderTick, play, after);
        logger?.Debug("preview.playback", play ? "Preview playback requested" : "Preview frame requested", Properties(renderTick));
        SetState(play ? InteractivePreviewState.Buffering : InteractivePreviewState.Scrubbing);
        if (!running) { running = true; runner = RunMailboxAsync(); }
    }
    private void Cancel()
    {
        if (active is not null || pending is not null || playSession)
            logger?.Debug("preview.playback", "Preview work cancelled", Properties(PositionTicks));
        generation = native.Cancel(); pending = null; active?.Cancel(); wantPlay = false; playSession = false;
        // Stop sound immediately, but let the joined runner own disposal.
        try { output?.Pause(); } catch { }
    }
    private async Task RunMailboxAsync()
    {
        try
        {
            while (pending is { } request && !disposed)
            {
                pending = null;
                using var cancellation = new CancellationTokenSource(); active = cancellation;
                try
                {
                    if (request.Play) await RunPlaybackAsync(request, cancellation.Token);
                    else
                    {
                        var prepared = await PrepareFrame(request.Tick, false, cancellation.Token);
                        if (!native.Accept(request.Generation) || cancellation.IsCancellationRequested) continue;
                        Publish(prepared); SetState(request.After);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { logger?.Debug("preview.playback", "Preview request observed cancellation", Properties(request.Tick)); }
                catch (Exception e) { if (native.Accept(request.Generation)) Fail(e); }
                finally { if (ReferenceEquals(active, cancellation)) active = null; }
            }
        }
        finally { running = false; if (disposed) native.Dispose(); }
    }
    private async Task RunPlaybackAsync(Request request, CancellationToken token)
    {
        var sessionId = Guid.NewGuid(); playbackSessionId = sessionId;
        logger?.Info("preview.playback", "Preview playback session started", Properties(request.Tick));
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = sessionCancellation.Token;
        using var device = outputFactory(); output = device; playSession = true;
        var playbackTicket = ticket;
        long startSample = playbackTicket.StartSample;
        var audioReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? audioTask = null, videoTask = null;
        var ready = new List<PreviewPresentation>(ForwardVideoFrames);
        try
        {
            audioTask = ProduceAudio();
            var first = await PrepareFrame(TimelineTime.SampleToTicks(startSample, 48000), true, ct);
            ct.ThrowIfCancellationRequested(); Publish(first); Notify();
            await audioReady.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (wantPlay) { device.Play(); SetState(InteractivePreviewState.Playing); }
            else SetState(InteractivePreviewState.Paused);
            videoTask = ProduceVideo();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var step = native.Presentation(request.Generation, device.PlayedFrames,
                    ready.Count > 0 ? ready[0].Frame.Tick : -1, ready.Count > 1 ? ready[1].Frame.Tick : -1);
                PositionTicks = step.Position; DroppedVideoFrames += step.Dropped;
                if (step.Ended != 0)
                {
                    RequestFrame(context!.Sequence.DurationTicks, false, InteractivePreviewState.Stopped);
                    break;
                }
                if (step.Dropped != 0 || step.Present != 0)
                {
                    var candidate = ready[0]; ready.RemoveAt(0); ReadyVideoFrames = ready.Count;
                    if (step.Present != 0) { Publish(candidate); Notify(); }
                    ReportPerformance();
                    continue;
                }
                ReportPerformance();
                await Task.Delay(5, ct);
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            try
            {
                // Observe and join both producers, even when one faults or ignores cancellation.
                try { await Task.WhenAll(new[] { audioTask, videoTask }.OfType<Task>()); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                ReadyVideoFrames = 0;
                ReportPerformance(force: true);
                logger?.Info("preview.playback", "Preview playback session stopped", Properties(PositionTicks));
                if (playbackSessionId == sessionId) playbackSessionId = null;
                if (ReferenceEquals(output, device)) { output = null; playSession = false; }
            }
        }

        async Task ProduceVideo()
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // -1 suppresses presentation: only the consumer may remove ready frames.
                    var step = native.Video(request.Generation, device.PlayedFrames, ready.Count, -1);
                    DroppedVideoFrames += step.Dropped;
                    if (step.Ended != 0) break;
                    if (step.VideoTick < 0) { await Task.Delay(5, ct); continue; }
                    MaximumVideoFrames = Math.Max(MaximumVideoFrames, ready.Count + 1);
                    var prepared = await PrepareFrame(step.VideoTick, true, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!native.Accept(request.Generation)) break;
                    ready.Add(prepared); ReadyVideoFrames = ready.Count;
                }
            }
            catch { sessionCancellation.Cancel(); throw; }
        }

        async Task ProduceAudio()
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = native.Audio(request.Generation, device.QueuedFrames);
                    if (step.Underrun != 0 && State == InteractivePreviewState.Playing)
                    { device.Pause(); Underruns++; SetState(InteractivePreviewState.Buffering); }
                    if (step.Count == 0)
                    {
                        if (step.FirstSample == playbackTicket.TotalSamples) { audioReady.TrySetResult(); break; }
                        await Task.Delay(5, ct); continue;
                    }
                    var result = await source.AudioAsync(context!, step.FirstSample, step.Count, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!native.Accept(request.Generation)) break;
                    Require(result); device.Enqueue(result.Value!);
                    if (step.Ready != 0) audioReady.TrySetResult();
                    if (State == InteractivePreviewState.Buffering && Frame is not null && wantPlay && step.Resume != 0)
                    { device.Play(); SetState(InteractivePreviewState.Playing); }
                }
            }
            catch (Exception) { audioReady.TrySetCanceled(); sessionCancellation.Cancel(); throw; }
        }
    }
    private async Task<PreviewPresentation> PrepareFrame(long tick, bool forward, CancellationToken token)
    {
        RequestedVideoTick = tick;
        var result = await source.FrameAsync(context!, tick, Quality, forward, token);
        token.ThrowIfCancellationRequested(); Require(result);
        long started = Stopwatch.GetTimestamp();
        var prepared = await PreviewPresentation.PrepareAsync(result.Value!, token);
        ConversionPerformance.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return prepared;
    }
    private void Publish(PreviewPresentation prepared) { Frame = prepared.Frame; Presentation = prepared; }
    public void RecordPresentation(double milliseconds) => PresentationPerformance.Record(milliseconds);
    private void ReportPerformance(bool force = false)
    {
        if (logger is null || !force && !PresentationPerformance.ShouldReport()) return;
        var properties = Properties(PositionTicks);
        properties["requestedTick"] = RequestedVideoTick; properties["presentedTick"] = Frame?.Tick;
        properties["queueDepth"] = ReadyVideoFrames; properties["maximumVideoFrames"] = MaximumVideoFrames;
        properties["droppedFrames"] = DroppedVideoFrames; properties["audioUnderruns"] = Underruns;
        properties["conversion"] = ConversionPerformance.Statistics;
        properties["presentation"] = PresentationPerformance.Statistics;
        if (source is InteractivePreviewSource measured)
        { properties["videoPreparation"] = measured.VideoPerformance.Statistics; properties["audioPreparation"] = measured.AudioPerformance.Statistics; }
        logger.Info("preview.performance", force ? "Preview performance session summary" : "Preview playback performance", properties);
    }
    public static long FirstSample(long tick) => Kachinco.Native.NativePlayback.FirstSample(tick);
    private static void Require<T>(Result<T> result) { if (!result.Success) throw new InvalidDataException(string.Join(" / ", result.Diagnostics.Select(d => $"[{d.Code}] {d.Message}"))); }
    private void Fail(Exception e)
    {
        logger?.Error("preview.playback", "Preview playback failed", e, Properties(PositionTicks));
        Cancel(); Error = e.Message; Frame = null; Presentation = null; SetState(InteractivePreviewState.Failed);
    }
    private Dictionary<string, object?> Properties(long tick) => new()
    {
        ["playbackSessionId"] = playbackSessionId, ["generation"] = generation,
        ["sequenceId"] = context?.Sequence.Id, ["timelineTick"] = tick,
        ["quality"] = Quality.ToString(), ["state"] = State.ToString(),
    };
    private void SetState(InteractivePreviewState value) { State = value; Notify(); }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { if (disposed) return; disposed = true; Cancel(); Frame = null; Presentation = null; if (!running) native.Dispose(); }
}
