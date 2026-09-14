using Kachinco.Core;

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
public sealed class InteractivePreview(IInteractivePreviewSource source, Func<IPreviewAudioOutput> outputFactory) : IDisposable
{
    public const int AudioBlockFrames = 4800;
    public const int StartupFrames = 9600;
    public const int MaximumQueuedFrames = 24000;
    public const int ForwardVideoFrames = 3;
    public event EventHandler? Changed;
    public InteractivePreviewState State { get; private set; }
    public RenderedVideoFrame? Frame { get; private set; }
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
    private long generation, startSample;
    private IPreviewAudioOutput? output;
    private bool playSession;
    private sealed record Request(long Generation, long Tick, bool Play, InteractivePreviewState After);

    public void SetContext(PreviewContext? value)
    {
        if (ReferenceEquals(context, value)) return;
        var old = context;
        long tick = ReadPositionTicks();
        if (value is not null && old is not null && playSession &&
            value.Sequence.Id == old.Sequence.Id && value.Project.Id == old.Project.Id &&
            tick < value.Sequence.DurationTicks && old.WindowKey(tick, SaturatingEnd(tick)) == value.WindowKey(tick, SaturatingEnd(tick)))
        { context = value; return; }
        context = value;
        if (value is null) { Cancel(); Frame = null; PositionTicks = 0; SetState(InteractivePreviewState.Stopped); return; }
        bool resume = (playSession || pending?.Play == true) && wantPlay && old?.Sequence.Id == value.Sequence.Id && old.Project.Id == value.Project.Id;
        long next = old?.Sequence.Id == value.Sequence.Id && old.Project.Id == value.Project.Id ? Math.Min(tick, value.Sequence.DurationTicks - 1) : 0;
        RequestFrame(next, resume, resume ? InteractivePreviewState.Playing : InteractivePreviewState.Paused);
    }
    private static long SaturatingEnd(long tick) => tick > long.MaxValue - TimelineTime.TicksPerSecond ? long.MaxValue : tick + TimelineTime.TicksPerSecond;

    public void Scrub(long tick) => RequestFrame(tick, false, InteractivePreviewState.Paused);
    public void Play()
    {
        if (context is null || disposed) return;
        if (playSession && output is not null && State == InteractivePreviewState.Paused)
        {
            wantPlay = true;
            try { if (output.QueuedFrames > 0) { output.Play(); SetState(InteractivePreviewState.Playing); } else SetState(InteractivePreviewState.Buffering); }
            catch (Exception e) { Fail(e); }
            return;
        }
        RequestFrame(FirstSample(PositionTicks) >= TimelineTime.SampleCount(context.Sequence.DurationTicks, 48000) ? 0 : PositionTicks, true, InteractivePreviewState.Playing);
    }
    public void Pause()
    {
        wantPlay = false;
        try { output?.Pause(); ReadPositionTicks(); if (playSession) SetState(InteractivePreviewState.Paused); }
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
                PositionTicks = Math.Min(context.Sequence.DurationTicks, TimelineTime.SampleToTicks(checked(startSample + output.PlayedFrames), 48000));
        }
        catch (Exception e) { Fail(e); }
        return PositionTicks;
    }
    private void RequestFrame(long tick, bool play, InteractivePreviewState after)
    {
        if (disposed) return;
        Cancel(); Error = null; Frame = null;
        if (context is null) { PositionTicks = 0; SetState(InteractivePreviewState.Stopped); return; }
        PositionTicks = Math.Clamp(tick, 0, context.Sequence.DurationTicks);
        // The end cursor is an editing boundary, not a decodable frame at duration - 1 tick.
        // Show the final canonical output frame while retaining the end cursor for replay.
        long renderTick = PositionTicks == context.Sequence.DurationTicks ?
            TimelineTime.FrameToTicks(Math.Max(0, TimelineTime.FrameCount(context.Sequence.DurationTicks, context.Sequence.Settings.FrameRate) - 1), context.Sequence.Settings.FrameRate) : PositionTicks;
        wantPlay = play;
        pending = new(generation, renderTick, play, after);
        SetState(play ? InteractivePreviewState.Buffering : InteractivePreviewState.Scrubbing);
        if (!running) { running = true; runner = RunMailboxAsync(); }
    }
    private void Cancel()
    {
        generation++; pending = null; active?.Cancel(); wantPlay = false; playSession = false;
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
                        var result = await source.FrameAsync(context!, request.Tick, Quality, false, cancellation.Token);
                        if (request.Generation != generation || cancellation.IsCancellationRequested) continue;
                        Require(result); Frame = result.Value; SetState(request.After);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception e) { if (request.Generation == generation) Fail(e); }
                finally { if (ReferenceEquals(active, cancellation)) active = null; }
            }
        }
        finally { running = false; }
    }
    private async Task RunPlaybackAsync(Request request, CancellationToken token)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = sessionCancellation.Token;
        using var device = outputFactory(); output = device; playSession = true;
        startSample = FirstSample(request.Tick);
        long total = TimelineTime.SampleCount(context!.Sequence.DurationTicks, 48000);
        long nextAudio = startSample;
        var audioReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? audioTask = null;
        try
        {
            audioTask = ProduceAudio();
            var first = await source.FrameAsync(context, TimelineTime.SampleToTicks(startSample, 48000), Quality, true, ct);
            ct.ThrowIfCancellationRequested(); Require(first); Frame = first.Value; Notify();
            await audioReady.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (wantPlay) { device.Play(); SetState(InteractivePreviewState.Playing); }
            else SetState(InteractivePreviewState.Paused);
            var fps = context.Sequence.Settings.FrameRate;
            long nextFrame = FrameIndex(request.Tick, fps) + 1;
            var ready = new Queue<RenderedVideoFrame>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long now = ReadPositionTicks();
                if (device.PlayedFrames >= total - startSample) { PositionTicks = context.Sequence.DurationTicks; SetState(InteractivePreviewState.Stopped); break; }
                if (!wantPlay) { await Task.Delay(8, ct); continue; }
                while (ready.TryPeek(out var candidate) && candidate.Tick <= now)
                { Frame = ready.Dequeue(); Notify(); }
                long clockFrame = FrameIndex(now, fps);
                if (nextFrame < clockFrame) { DroppedVideoFrames += clockFrame - nextFrame; nextFrame = clockFrame; }
                long at = TimelineTime.FrameToTicks(nextFrame, fps);
                if (ready.Count >= ForwardVideoFrames || nextFrame > clockFrame + ForwardVideoFrames || at >= context.Sequence.DurationTicks)
                { await Task.Delay(5, ct); continue; }
                var result = await source.FrameAsync(context, Math.Max(request.Tick, at), Quality, true, ct);
                ct.ThrowIfCancellationRequested(); Require(result); ready.Enqueue(result.Value!); nextFrame++;
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            try { if (audioTask is not null) try { await audioTask; } catch (OperationCanceledException) { } }
            finally { if (ReferenceEquals(output, device)) { output = null; playSession = false; } }
        }

        async Task ProduceAudio()
        {
            try
            {
                while (nextAudio < total)
                {
                    ct.ThrowIfCancellationRequested();
                    if (device.QueuedFrames > MaximumQueuedFrames - AudioBlockFrames) { await Task.Delay(5, ct); continue; }
                    bool underrun = audioReady.Task.IsCompleted && wantPlay && device.QueuedFrames == 0;
                    if (underrun && State == InteractivePreviewState.Playing)
                    { device.Pause(); Underruns++; SetState(InteractivePreviewState.Buffering); }
                    int count = (int)Math.Min(AudioBlockFrames, total - nextAudio);
                    var result = await source.AudioAsync(context!, nextAudio, count, ct);
                    ct.ThrowIfCancellationRequested(); Require(result); device.Enqueue(result.Value!); nextAudio += count;
                    if (nextAudio - startSample >= StartupFrames || nextAudio == total) audioReady.TrySetResult();
                    if (State == InteractivePreviewState.Buffering && Frame is not null && wantPlay &&
                        (device.QueuedFrames >= StartupFrames || nextAudio == total))
                    { device.Play(); SetState(InteractivePreviewState.Playing); }
                }
            }
            catch (Exception) { audioReady.TrySetCanceled(); sessionCancellation.Cancel(); throw; }
        }
    }
    public static long FirstSample(long tick) => checked((long)(((System.Numerics.BigInteger)tick * 48000 + TimelineTime.TicksPerSecond - 1) / TimelineTime.TicksPerSecond));
    private static long FrameIndex(long tick, FrameRate fps) => checked((long)((System.Numerics.BigInteger)tick * fps.Numerator / ((System.Numerics.BigInteger)TimelineTime.TicksPerSecond * fps.Denominator)));
    private static void Require<T>(Result<T> result) { if (!result.Success) throw new InvalidDataException(string.Join(" / ", result.Diagnostics.Select(d => $"[{d.Code}] {d.Message}"))); }
    private void Fail(Exception e) { Cancel(); Error = e.Message; Frame = null; SetState(InteractivePreviewState.Failed); }
    private void SetState(InteractivePreviewState value) { State = value; Notify(); }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() { if (disposed) return; disposed = true; Cancel(); Frame = null; }
}
