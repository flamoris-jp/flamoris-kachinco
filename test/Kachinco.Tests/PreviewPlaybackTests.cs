using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PreviewPlaybackTests
{
    [TestMethod]
    public async Task PlayWaitsForNativeOpenAndUsesOnlyActualMediaPosition()
    {
        var f = new Fixture(); var player = new FakePlayer();
        using var playback = new PreviewPlayback(() => player, Complete);
        var states = new List<PreviewState>(); playback.Changed += (_, _) => states.Add(playback.State);
        var preparing = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, Fixture.T, true);
        Assert.AreEqual(PreviewState.Preparing, playback.State);
        Assert.AreEqual(0, player.PlayCalls);
        player.SignalOpen(); await preparing;
        Assert.AreEqual(PreviewState.Playing, playback.State);
        Assert.AreEqual(Fixture.T, playback.ReadPositionTicks());
        player.Position = TimeSpan.FromSeconds(2.125);
        Assert.AreEqual(TimelineTime.SecondsToTicks(2.125m), playback.ReadPositionTicks());
        Assert.AreEqual(playback.ReadPositionTicks(), playback.ReadPositionTicks(), "Repeated redraws must not advance time.");
        playback.Toggle(); Assert.AreEqual(PreviewState.Paused, playback.State);
        playback.Toggle(); Assert.AreEqual(PreviewState.Playing, playback.State);
        playback.Stop(); Assert.AreEqual(PreviewState.Stopped, playback.State); Assert.AreEqual(0L, playback.ReadPositionTicks());
        playback.Toggle(); player.SignalEnd(); Assert.AreEqual(PreviewState.Stopped, playback.State);
        CollectionAssert.Contains(states, PreviewState.Preparing); CollectionAssert.Contains(states, PreviewState.Playing);
    }
    [TestMethod]
    public async Task ExplicitPreparationDoesNotAutoplayAndSeekingWhilePreparingIsHonored()
    {
        var f = new Fixture(); var player = new FakePlayer();
        using var playback = new PreviewPlayback(() => player, Complete);
        var task = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, false);
        playback.Seek(2 * Fixture.T); player.SignalOpen(); await task;
        Assert.AreEqual(0, player.PlayCalls); Assert.AreEqual(PreviewState.Paused, playback.State);
        Assert.AreEqual(2 * Fixture.T, playback.ReadPositionTicks());
    }
    [TestMethod]
    public async Task RenderAndNativeFailuresAreVisibleAndRetryable()
    {
        var f = new Fixture(); var player = new FakePlayer();
        using var failed = new PreviewPlayback(() => player, (_, _, _, _, _) =>
            Task.FromResult(new ExportResult(Guid.NewGuid(), ExportStage.Failed, null, [Diagnostic.Error("MISSING", "source missing")])));
        await failed.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
        Assert.AreEqual(PreviewState.Failed, failed.State); StringAssert.Contains(failed.Error!, "source missing");
        using var native = new PreviewPlayback(() => player, Complete);
        var task = native.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
        player.SignalFailure(); await task;
        Assert.AreEqual(PreviewState.Failed, native.State); StringAssert.Contains(native.Error!, "codec failed");
        Assert.IsTrue(player.Disposed);
    }
    [TestMethod]
    public async Task CancelledLateRenderCannotOpenOrPublishPreview()
    {
        var f = new Fixture(); var player = new FakePlayer();
        var completion = new TaskCompletionSource<ExportResult>(); string? output = null;
        using var playback = new PreviewPlayback(() => player, (_, _, path, _, _) => { output = path; return completion.Task; });
        var task = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
        playback.Stop();
        File.WriteAllText(output!, "late output");
        completion.SetResult(new(Guid.NewGuid(), ExportStage.Completed, output, []));
        await task;
        Assert.AreEqual(PreviewState.Stopped, playback.State); Assert.IsNull(playback.Player);
        Assert.IsFalse(File.Exists(output)); Assert.AreEqual(0, player.PlayCalls);
    }
    [TestMethod]
    public async Task InvalidatedNativeCallbacksCannotAffectNewSnapshot()
    {
        var f = new Fixture(); var old = new FakePlayer(); var next = new FakePlayer(); int count = 0;
        using var playback = new PreviewPlayback(() => count++ == 0 ? old : next, Complete);
        var a = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
        playback.Invalidate();
        var b = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
        old.SignalOpen(); old.SignalFailure(); old.SignalEnd();
        Assert.AreEqual(PreviewState.Preparing, playback.State);
        next.SignalOpen(); await Task.WhenAll(a, b);
        Assert.AreEqual(PreviewState.Playing, playback.State); Assert.AreEqual(0, old.PlayCalls);
        Assert.IsTrue(playback.Matches(f.Session.GetProject().Revision, f.SequenceId));
        Assert.IsFalse(playback.Matches(f.Session.GetProject().Revision + 1, f.SequenceId));
    }
    [TestMethod]
    public async Task NativePositionAndTransportExceptionsBecomeFailures()
    {
        var f = new Fixture();
        foreach (string operation in new[] { "read", "seek", "pause", "stop", "resume" })
        {
            var player = new FakePlayer();
            using var playback = new PreviewPlayback(() => player, Complete);
            var task = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
            player.SignalOpen(); await task;
            if (operation == "resume") playback.Pause();
            player.ThrowOnOperation = true;
            switch (operation)
            {
                case "read": playback.ReadPositionTicks(); break;
                case "seek": playback.Seek(Fixture.T); break;
                case "pause": playback.Pause(); break;
                case "stop": playback.Stop(); break;
                case "resume": playback.Toggle(); break;
            }
            Assert.AreEqual(PreviewState.Failed, playback.State, operation);
            StringAssert.Contains(playback.Error!, "native operation failed");
            Assert.IsTrue(player.Disposed);
        }
    }
    [TestMethod]
    public async Task PauseDuringPreparationCancelsPendingAutoplay()
    {
        var f = new Fixture(); var player = new FakePlayer();
        using var playback = new PreviewPlayback(() => player, Complete);
        var task = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
        playback.Pause(); player.SignalOpen(); await task;
        Assert.AreEqual(PreviewState.Stopped, playback.State);
        Assert.AreEqual(0, player.PlayCalls); Assert.IsTrue(player.Disposed);
    }
    [TestMethod]
    public async Task RenderingProgressPrecedesPlayingAndRejectsLateProgress()
    {
        // A deterministic context lets us drain queued progress without timing-based assertions.
        var context = new QueueContext(); var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var f = new Fixture(); var player = new FakePlayer();
            IProgress<ExportProgress>? report = null;
            var rendered = new TaskCompletionSource<ExportResult>();
            using var playback = new PreviewPlayback(() => player, (_, _, _, progress, _) => { report = progress; return rendered.Task; });
            var task = playback.PrepareAsync(f.Session.GetProject(), f.SequenceId, 0, true);
            report!.Report(new(Guid.NewGuid(), ExportStage.Rendering, 1, 10, 0)); context.Drain();
            Assert.AreEqual(PreviewState.Rendering, playback.State); Assert.AreEqual(1L, playback.Progress!.FramesCompleted);
            playback.Stop();
            report.Report(new(Guid.NewGuid(), ExportStage.Rendering, 2, 10, 0)); context.Drain();
            Assert.AreEqual(PreviewState.Stopped, playback.State);
            rendered.SetResult(new(Guid.NewGuid(), ExportStage.Completed, null, [])); context.Drain();
            Assert.IsTrue(task.IsCompleted);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await Task.CompletedTask;
    }
    private static Task<ExportResult> Complete(ProjectSnapshot snapshot, Guid sequence, string path, IProgress<ExportProgress> progress, CancellationToken token) =>
        Task.FromResult(new ExportResult(Guid.NewGuid(), ExportStage.Completed, path, []));
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<Action> callbacks = [];
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue(() => callback(state));
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }
    private sealed class FakePlayer : IPreviewPlayer
    {
        public event EventHandler? Opened;
        public event EventHandler? Ended;
        public event EventHandler<Exception>? Failed;
        private TimeSpan position;
        public bool ThrowOnOperation { get; set; }
        public TimeSpan Position { get { Check(); return position; } set { Check(); position = value; } }
        public int PlayCalls { get; private set; }
        public bool Disposed { get; private set; }
        public void Open(string path) { }
        public void Play() { Check(); PlayCalls++; }
        public void Pause() => Check();
        private void Check() { if (ThrowOnOperation) throw new InvalidOperationException("native operation failed"); }
        public void Dispose() => Disposed = true;
        public void SignalOpen() => Opened?.Invoke(this, EventArgs.Empty);
        public void SignalEnd() => Ended?.Invoke(this, EventArgs.Empty);
        public void SignalFailure() => Failed?.Invoke(this, new InvalidDataException("codec failed"));
    }
}
