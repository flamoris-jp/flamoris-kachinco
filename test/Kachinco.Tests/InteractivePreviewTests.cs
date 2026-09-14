using System.Collections.Concurrent;
using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class InteractivePreviewTests
{
    [TestMethod]
    public void ScrubMailboxIsBoundedLatestWinsAndNeverMutatesProject()
    {
        using var pump = new Pump(); var f = new Fixture(); var snapshot = f.Session.GetProject();
        var source = new Source { Hold = true }; using var p = new InteractivePreview(source, () => new Device());
        p.SetContext(Context(f)); var first = source.Pending!;
        for (int i = 1; i <= 1000; i++) p.Scrub(i * 100);
        Assert.AreEqual(1, source.Video.Count); Assert.IsTrue(source.Token.IsCancellationRequested);
        Assert.IsTrue(p.HasPendingRequest); Assert.IsNull(p.Frame);
        source.Hold = false; first.SetResult(Frame(0)); pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(2, source.Video.Count); Assert.AreEqual(100000L, p.Frame!.Tick);
        Assert.AreEqual(snapshot, f.Session.GetProject());
        source.Fail = true; p.Scrub(Fixture.T); pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, p.State); Assert.IsNull(p.Frame);
        StringAssert.Contains(p.Error!, "missing media");
    }
    [TestMethod]
    public void CurrentPositionStartupIsIndependentOfDurationAndUsesOnlyConsumedPcm()
    {
        using var pump = new Pump();
        foreach (decimal duration in new[] { 8m, 219.6m })
        {
            var f = new Fixture(); Assert.IsTrue(f.Edit(new SetSequenceDuration(f.SequenceId, TimelineTime.SecondsToTicks(duration))).Success);
            var source = new Source(); var device = new Device(); using var p = new InteractivePreview(source, () => device);
            p.SetContext(Context(f)); p.Scrub(4 * Fixture.T); source.Video.Clear(); source.Audio.Clear();
            p.Play(); pump.Until(() => p.State == InteractivePreviewState.Playing);
            Assert.IsTrue(source.Video.All(t => t >= 4 * Fixture.T));
            Assert.IsTrue(source.Video.Count <= 1 + InteractivePreview.ForwardVideoFrames);
            Assert.IsTrue(source.Audio.All(t => t >= 4 * 48000)); Assert.IsTrue(device.QueuedFrames <= 24000);
            Assert.AreEqual(4 * Fixture.T, p.ReadPositionTicks()); pump.Drain(); Assert.AreEqual(4 * Fixture.T, p.ReadPositionTicks());
            device.Advance(4800); Assert.AreEqual(TimelineTime.SecondsToTicks(4.1m), p.ReadPositionTicks());
            p.Pause(); Assert.AreEqual(InteractivePreviewState.Paused, p.State); Assert.IsFalse(device.Running);
            p.Play(); Assert.AreEqual(InteractivePreviewState.Playing, p.State);
            p.Scrub(6 * Fixture.T); Assert.AreEqual(6 * Fixture.T, p.ReadPositionTicks(), "Old device cannot overwrite a new seek.");
            pump.Until(() => p.Completion.IsCompleted); Assert.AreEqual(6 * Fixture.T, p.Frame!.Tick); Assert.IsTrue(device.Disposed);
            p.Stop(); pump.Until(() => p.Completion.IsCompleted); Assert.AreEqual(InteractivePreviewState.Stopped, p.State); Assert.AreEqual(0L, p.Frame!.Tick);
        }
    }
    [TestMethod]
    public void PreparationPauseAndEditRejectOldCallbacksAndKeepUnaffectedPlayback()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source(); var devices = new List<Device>();
        using var p = new InteractivePreview(source, () => { var d = new Device(); devices.Add(d); return d; });
        p.SetContext(Context(f)); source.Hold = true; p.Play(); var late = source.Pending!;
        p.Pause(); source.Hold = false; late.SetResult(Frame(0)); pump.Until(() => p.State == InteractivePreviewState.Paused);
        Assert.IsFalse(devices[0].Running); p.Play(); Assert.IsTrue(devices[0].Running);
        Assert.IsTrue(f.Edit(new MoveClip(f.SequenceId, f.AudioClipId, f.AudioTrackId, 2 * Fixture.T)).Success);
        p.SetContext(Context(f)); Assert.AreEqual(1, devices.Count, "Edit outside queued window preserves transport.");
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, f.ClipId, true, ClipAppearance.Default with { Opacity = .5 }, AudioProperties.Default)).Success);
        p.SetContext(Context(f)); pump.Until(() => devices.Count == 2 && p.State == InteractivePreviewState.Playing);
        Assert.IsTrue(devices[0].Disposed); p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }
    [TestMethod]
    public void DeviceAndAudioFailuresClearViewerAndDisposeTransport()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source();
        using var p = new InteractivePreview(source, () => throw new IOException("audio device unavailable"));
        p.SetContext(Context(f)); p.Play(); pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, p.State); Assert.IsNull(p.Frame); StringAssert.Contains(p.Error!, "audio device unavailable");
        var device = new Device(); source.AudioFail = true;
        using var a = new InteractivePreview(source, () => device); a.SetContext(Context(f)); a.Play(); pump.Until(() => a.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, a.State); Assert.IsNull(a.Frame); Assert.IsTrue(device.Disposed); StringAssert.Contains(a.Error!, "PCM failed");
    }
    [TestMethod]
    public void SampleMappingRoundsForwardWithoutInventingASecondTimebase()
    {
        foreach (long tick in new[] { 0L, 1L, Fixture.T, Fixture.T + 1, 219 * Fixture.T })
        {
            long sample = InteractivePreview.FirstSample(tick); long actual = TimelineTime.SampleToTicks(sample, 48000);
            Assert.IsTrue(actual >= tick && actual - tick < 735);
        }
    }
    internal static PreviewContext Context(Fixture f) => PreviewContext.Create(f.Session.GetProject(), f.SequenceId).Value!;
    private static Result<RenderedVideoFrame> Frame(long tick) => Result<RenderedVideoFrame>.Ok(new(0, tick, 1, 1, [1, 2, 3, 255]));
    private sealed class Source : IInteractivePreviewSource
    {
        public readonly List<long> Video = [], Audio = [];
        public bool Hold, Fail, AudioFail; public CancellationToken Token; public TaskCompletionSource<Result<RenderedVideoFrame>>? Pending;
        public ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext c, long tick, PreviewQuality q, bool forward, CancellationToken token)
        {
            Video.Add(tick); Token = token;
            if (Hold) { Pending = new(); return new(Pending.Task); } // Deliberately ignores cancellation: stale rejection must still work.
            return ValueTask.FromResult(Fail ? Result<RenderedVideoFrame>.Fail(Diagnostic.Error("MISSING", "missing media")) : Frame(tick));
        }
        public ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext c, long first, int count, CancellationToken token)
        {
            Audio.Add(first);
            return ValueTask.FromResult(AudioFail ? Result<RenderedAudioBlock>.Fail(Diagnostic.Error("AUDIO", "PCM failed")) :
                Result<RenderedAudioBlock>.Ok(new(first, 48000, 2, new float[count * 2].ToImmutableArray())));
        }
    }
    private sealed class Device : IPreviewAudioOutput
    {
        public long PlayedFrames { get; private set; } public long QueuedFrames => submitted - PlayedFrames;
        private long submitted; public bool Running, Disposed;
        public void Advance(long samples) { if (Running) PlayedFrames = Math.Min(submitted, PlayedFrames + samples); }
        public void Enqueue(RenderedAudioBlock block) { submitted += block.Samples.Length / 2; Assert.IsTrue(QueuedFrames <= 24000); }
        public void Play() => Running = true; public void Pause() => Running = false;
        public void Dispose() { Running = false; Disposed = true; }
    }
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? previous = Current;
        private readonly ConcurrentQueue<Action> queue = new();
        public Pump() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) => queue.Enqueue(() => callback(state));
        public void Drain() { while (queue.TryDequeue(out var callback)) callback(); }
        public void Until(Func<bool> condition)
        {
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Drain(); if (DateTime.UtcNow > limit) Assert.Fail("Preview did not reach expected state."); Thread.Sleep(1); }
            Drain();
        }
        public void Dispose() => SetSynchronizationContext(previous);
    }
}
