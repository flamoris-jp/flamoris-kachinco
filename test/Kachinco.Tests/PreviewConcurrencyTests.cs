using System.Collections.Concurrent;
using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PreviewConcurrencyTests
{
    [TestMethod]
    public async Task PlaybackWithoutSynchronizationContextSerializesDecisionsAndBoundsTheQueue()
    {
        await Task.Run(async () =>
        {
            Assert.IsNull(SynchronizationContext.Current);
            var fixture = new Fixture(); var snapshot = fixture.Session.GetProject();
            var source = new Source(); var device = new Device();
            using var preview = new InteractivePreview(source, () => device);
            var presented = new ConcurrentQueue<long>();
            preview.Changed += (_, _) => { if (preview.Frame is { } frame) presented.Enqueue(frame.Tick); };
            preview.SetContext(InteractivePreviewTests.Context(fixture));
            await preview.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            preview.Play(); await Until(() => device.Running);

            // Hold a producer/consumer device-clock read inside a decision. A host
            // clock read must wait for that decision, with no Dispatcher to serialize it.
            device.ArmRead();
            await device.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var hostEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostRead = Task.Run(() => { hostEntered.SetResult(); return preview.ReadPositionTicks(); });
            try
            {
                await hostEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreNotSame(hostRead, await Task.WhenAny(hostRead, Task.Delay(100)),
                    "Host decisions must wait for the in-progress producer/consumer decision.");
                Assert.AreEqual(1, device.MaximumConcurrentReads);
            }
            finally { device.ReleaseRead.Set(); }
            await hostRead.WaitAsync(TimeSpan.FromSeconds(10));

            for (int i = 0; i < 45; i++)
            {
                device.Advance(1600);
                preview.ReadPositionTicks();
                await Task.Delay(2);
            }
            await Until(() => presented.Any(t => t >= TimelineTime.FrameToTicks(10, new(30, 1))));
            preview.Pause();
            await preview.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(InteractivePreviewState.Paused, preview.State);
            Assert.AreEqual(preview.PositionTicks, preview.Frame!.Tick);
            Assert.IsNull(preview.Error);
            Assert.IsTrue(device.Disposed);
            Assert.AreEqual(1, device.MaximumConcurrentReads);
            Assert.IsTrue(preview.MaximumVideoFrames <= InteractivePreview.ForwardVideoFrames);
            Assert.AreEqual(0, preview.ReadyVideoFrames);
            Assert.AreEqual(snapshot, fixture.Session.GetProject());
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public async Task NoContextPresentationAndPauseDoNotWaitOnTheDecodeGateOrPublishStaleFrames()
    {
        await Task.Run(async () =>
        {
            Assert.IsNull(SynchronizationContext.Current);
            var fixture = new Fixture(); var source = new Source { HoldSecondForward = true };
            var device = new Device();
            using var preview = new InteractivePreview(source, () => device);
            long oneFrame = TimelineTime.FrameToTicks(1, new(30, 1));
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            preview.Changed += (_, _) => { if (preview.Frame?.Tick == oneFrame) presented.TrySetResult(); };
            preview.SetContext(InteractivePreviewTests.Context(fixture));
            await preview.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            preview.Play();
            await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            device.Advance(1600);
            await presented.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(source.ReleaseDecode.Task.IsCompleted, "A blocked decode cannot hold the decision gate.");
            preview.Pause();
            long frozen = preview.ReadPositionTicks();
            Assert.IsTrue(source.BlockedToken.IsCancellationRequested);
            Assert.IsFalse(device.Running);
            Assert.IsFalse(preview.Completion.IsCompleted, "Pause joins a producer that ignores cancellation.");
            source.ReleaseDecode.SetResult();
            await preview.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(device.Disposed);
            Assert.AreEqual(InteractivePreviewState.Paused, preview.State);
            Assert.AreEqual(frozen, preview.Frame!.Tick);
            Assert.IsNull(preview.Error);
            Assert.IsTrue(preview.MaximumVideoFrames <= InteractivePreview.ForwardVideoFrames);
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    private sealed class Source : IInteractivePreviewSource
    {
        public bool HoldSecondForward;
        public CancellationToken BlockedToken;
        public readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseDecode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext context, long tick,
            PreviewQuality quality, bool forward, CancellationToken token)
        {
            if (HoldSecondForward && forward && tick == TimelineTime.FrameToTicks(2, new(30, 1)))
            {
                HoldSecondForward = false; BlockedToken = token; Blocked.SetResult();
                await ReleaseDecode.Task; // Deliberately ignore cancellation until released.
            }
            else await Task.Delay(1, token);
            return Result<RenderedVideoFrame>.Ok(new(0, tick, 1, 1, [1, 2, 3, 255]));
        }
        public async ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext context, long first,
            int count, CancellationToken token)
        {
            await Task.Delay(1, token);
            return Result<RenderedAudioBlock>.Ok(new(first, 48000, 2, new float[count * 2].ToImmutableArray()));
        }
    }

    private sealed class Device : IPreviewAudioOutput
    {
        private readonly object gate = new();
        private long played, submitted;
        private bool running, disposed;
        private int armed, activeReads, maximumReads;
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ReleaseRead = new(false);
        public int MaximumConcurrentReads => Volatile.Read(ref maximumReads);
        public bool Running { get { lock (gate) return running; } }
        public bool Disposed { get { lock (gate) return disposed; } }
        public long PlayedFrames => Read(queued: false);
        public long QueuedFrames => Read(queued: true);
        public void ArmRead() => Interlocked.Exchange(ref armed, 1);
        private long Read(bool queued)
        {
            int count = Interlocked.Increment(ref activeReads);
            Interlocked.CompareExchange(ref maximumReads, count, count - 1);
            try
            {
                if (Interlocked.Exchange(ref armed, 0) == 1)
                {
                    ReadEntered.SetResult();
                    Assert.IsTrue(ReleaseRead.Wait(TimeSpan.FromSeconds(10)), "Held clock read was not released.");
                }
                lock (gate) return queued ? submitted - played : played;
            }
            finally { Interlocked.Decrement(ref activeReads); }
        }
        public void Advance(long count) { lock (gate) if (running) played = Math.Min(submitted, played + count); }
        public void Enqueue(RenderedAudioBlock block)
        {
            lock (gate)
            {
                Assert.IsFalse(disposed); submitted += block.Samples.Length / 2;
                Assert.IsTrue(submitted - played <= InteractivePreview.MaximumQueuedFrames);
            }
        }
        public void Play() { lock (gate) { Assert.IsFalse(disposed); running = true; } }
        public void Pause() { lock (gate) { Assert.IsFalse(disposed); running = false; } }
        public void Dispose() { lock (gate) { running = false; disposed = true; } }
    }
}
