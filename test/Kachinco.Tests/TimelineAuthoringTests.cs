using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class TimelineAuthoringTests
{
    [TestMethod]
    public void FitProjectsLongSequencesBelowInteractiveZoomFloor()
    {
        long duration = TimelineTime.SecondsToTicks(60 * 60);
        var viewport = TimelineViewport.Fit(duration, 900m);
        Assert.IsTrue(viewport.IsValid);
        Assert.IsTrue(viewport.PixelsPerSecond < TimelineViewport.MinimumInteractivePixelsPerSecond);
        Assert.AreEqual(900m, viewport.TicksToPixels(duration));
    }

    [TestMethod]
    public void ZoomPreservesDirectionAfterFitBelowInteractiveFloor()
    {
        var fitted = TimelineViewport.Fit(TimelineTime.SecondsToTicks(60 * 60), 900m);
        var zoomedOut = fitted.ZoomBy(0.8m);
        var zoomedIn = fitted.ZoomBy(1.25m);
        Assert.IsTrue(zoomedOut.PixelsPerSecond < fitted.PixelsPerSecond);
        Assert.IsTrue(zoomedIn.PixelsPerSecond > fitted.PixelsPerSecond);
        Assert.IsTrue(zoomedIn.PixelsPerSecond < TimelineViewport.MinimumInteractivePixelsPerSecond);
        Assert.AreEqual(TimelineViewport.MinimumInteractivePixelsPerSecond,
            new TimelineViewport(TimelineViewport.MinimumInteractivePixelsPerSecond).ZoomBy(0.8m).PixelsPerSecond);
    }

    [TestMethod]
    public void PixelTimeProjectionIsDeterministicAcrossZoomAndDoesNotAccumulate()
    {
        foreach (var pixelsPerSecond in new decimal[] { 4m, 31.25m, 80m, 1600m })
        {
            var viewport = new TimelineViewport(pixelsPerSecond);
            long ticks = TimelineTime.SecondsToTicks(12.345m);
            decimal pixels = viewport.TicksToPixels(ticks);
            Assert.AreEqual(ticks, viewport.PixelsToTicks(pixels));
            Assert.AreEqual(TimelineTime.SecondsToTicks(1.25m), viewport.DeltaPixelsToTicks(pixelsPerSecond * 1.25m));
        }
    }

    [TestMethod]
    public void SnappingUsesTickDistanceAndStableLowerTargetTieBreak()
    {
        var viewport = new TimelineViewport(100m);
        long candidate = TimelineTime.SecondsToTicks(5m);
        long delta = viewport.PixelsToTicks(5m);
        var snapped = TimelineSnapping.Snap(candidate, 8, viewport, [candidate + delta, candidate - delta]);
        Assert.IsTrue(snapped.Snapped); Assert.AreEqual(candidate - delta, snapped.Ticks);
        Assert.AreEqual(candidate, TimelineSnapping.Snap(candidate, 2, viewport, [candidate + delta]).Ticks);
    }

    [TestMethod]
    public void MoveTrimSplitPlansBecomeTypedCommandsAndUndoRedoCleanly()
    {
        var f = new Fixture(); Guid v2 = Fixture.Id(11);
        Assert.IsTrue(f.Edit(new AddTrack(f.SequenceId, v2, "V2", TrackKind.Video)).Success);
        var move = TimelineEditPlanner.Move(f.Project, f.SequenceId, f.ClipId, v2, 0);
        Assert.IsTrue(move.Success); Assert.IsTrue(f.Edit(move.Value!).Success);
        var trim = TimelineEditPlanner.Trim(f.Project, f.SequenceId, f.ClipId, TrimEdge.End, 5 * Fixture.T);
        Assert.IsTrue(trim.Success); Assert.IsTrue(f.Edit(trim.Value!).Success);
        var right = Fixture.Id(12);
        var split = TimelineEditPlanner.Split(f.Project, f.SequenceId, f.ClipId, 3 * Fixture.T, right);
        Assert.IsTrue(split.Success); Assert.IsTrue(f.Edit(split.Value!).Success);
        CollectionAssert.AreEqual(new[] { f.ClipId, right },
            TimelineQueries.ListClips(f.Project.Sequences[0].Tracks.First(x => x.Id == v2)).Select(x => x.Id).ToArray());
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(1, f.Project.Sequences[0].Tracks.First(x => x.Id == v2).Clips.Length);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(2, f.Project.Sequences[0].Tracks.First(x => x.Id == v2).Clips.Length);
    }

    [TestMethod]
    public void AuthoringPlannerRejectsWrongTracksInvalidEdgesAndDuplicateSplitIds()
    {
        var f = new Fixture();
        Assert.AreEqual("TRACK_MEDIA_MISMATCH", TimelineEditPlanner.Move(f.Project, f.SequenceId, f.ClipId, f.AudioTrackId, 0).Diagnostics[0].Code);
        Assert.AreEqual("INVALID_TIMELINE_RANGE", TimelineEditPlanner.Move(f.Project, f.SequenceId, f.ClipId, f.VideoTrackId, 2 * Fixture.T).Diagnostics[0].Code);
        Assert.AreEqual("INVALID_TRIM", TimelineEditPlanner.Trim(f.Project, f.SequenceId, f.ClipId, TrimEdge.Start, -1).Diagnostics[0].Code);
        Assert.AreEqual("INVALID_ID", TimelineEditPlanner.Split(f.Project, f.SequenceId, f.ClipId, Fixture.T, f.WavId).Diagnostics[0].Code);
    }

    [TestMethod]
    public void SnapTargetsAreOrderedAndExcludeTheDraggedClip()
    {
        var f = new Fixture();
        var targets = TimelineEditPlanner.SnapTargets(f.Project.Sequences[0], f.ClipId, 4 * Fixture.T);
        CollectionAssert.AreEqual(new[] { 0L, Fixture.T, 4 * Fixture.T, 7 * Fixture.T }, targets.ToArray());
    }
}
