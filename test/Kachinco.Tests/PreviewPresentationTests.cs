using Kachinco.Infrastructure;
using Kachinco.Core;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PreviewPresentationTests
{
    [TestMethod]
    public async Task BgraPreparationPreservesOwnedRgbaExportAndStraightAlpha()
    {
        var frame = new RenderedVideoFrame(7, 123, 2, 1, [251, 2, 3, 17, 5, 200, 7, 255]);
        var prepared = await PreviewPresentation.PrepareAsync(frame, default);
        CollectionAssert.AreEqual(new byte[] { 3, 2, 251, 17, 7, 200, 5, 255 }, prepared.Bgra8.ToArray());
        CollectionAssert.AreEqual(new byte[] { 251, 2, 3, 17, 5, 200, 7, 255 }, frame.Rgba8.ToArray());
        Assert.AreSame(frame, prepared.Frame);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => PreviewPresentation.PrepareAsync(frame, cancelled.Token));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PreviewPresentation.PrepareAsync(frame with { Width = 3 }, default));
    }

    [TestMethod]
    public void NativePresentationDropsOnlyWhenAnotherPreparedFrameIsDueAndDoesNotReserveWork()
    {
        using var p = new NativePlayback();
        var ticket = p.Request(8 * Fixture.T, 30, 1, 0, true);
        long f = TimelineTime.FrameToTicks(1, new(30, 1));
        Assert.AreEqual(0, p.Presentation(ticket.Generation, 0, f, 2 * f).Present);
        Assert.AreEqual(f, p.Video(ticket.Generation, 0, 0, -1).VideoTick);
        var due = p.Presentation(ticket.Generation, 1600, f, 2 * f);
        Assert.AreEqual(1, due.Present); Assert.AreEqual(0L, due.Dropped);
        var late = p.Presentation(ticket.Generation, 3200, f, 2 * f);
        Assert.AreEqual(0, late.Present); Assert.AreEqual(1L, late.Dropped);
        Assert.AreEqual(1, p.Presentation(ticket.Generation, 3200, 2 * f, -1).Present);
        Assert.AreEqual(1, p.Presentation(ticket.Generation, 8 * 48000, -1, -1).Ended);
        p.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => p.Presentation(ticket.Generation, 3200, f, -1));
    }

    [TestMethod]
    public void MetricsKeepLifetimeCacheCountsAndARecentPercentileWindow()
    {
        var metrics = new PreviewWorkMetrics(); metrics.Record(1, true); metrics.Record(3, false);
        Assert.AreEqual(2d, metrics.Statistics.AverageMilliseconds);
        for (int i = 0; i < 10000; i++) metrics.Record(10, true);
        var stats = metrics.Statistics;
        Assert.AreEqual(10002L, stats.Count); Assert.AreEqual(10001L, stats.CacheHits);
        Assert.AreEqual(1L, stats.CacheMisses); Assert.AreEqual(10d, stats.RecentP95Milliseconds);
        Assert.AreEqual(10d, stats.MaximumMilliseconds);
    }
}
