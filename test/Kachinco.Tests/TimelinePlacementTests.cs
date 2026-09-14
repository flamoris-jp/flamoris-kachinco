using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class TimelinePlacementTests
{
    [TestMethod]
    public void PlacementExtendsSequenceWithoutTruncationAndUndoesAtomically()
    {
        var f = new Fixture();
        var before = ProjectJson.Serialize(f.Project).Value;
        var snapshot = f.Session.GetProject();
        var planned = TimelineEditPlanner.Place(f.Project, f.SequenceId, f.MovId,
            f.VideoTrackId, Fixture.Id(20), 70 * Fixture.T, snapshot.Revision);
        Assert.IsTrue(planned.Success);
        Assert.IsTrue(f.Session.Execute(planned.Value!).Success);
        Assert.AreEqual(80 * Fixture.T, f.Project.Sequences[0].DurationTicks);
        var clip = f.Project.Sequences[0].Tracks[0].Clips.Single(x => x.Id == Fixture.Id(20));
        Assert.AreEqual(10 * Fixture.T, clip.DurationTicks);
        var after = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(ProjectJson.Deserialize(after!).Success);
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success);
        Assert.AreEqual(after, ProjectJson.Serialize(f.Project).Value);
    }

    [TestMethod]
    public void InvalidPlacementAndDurationShrinkPreserveState()
    {
        var f = new Fixture();
        var before = f.Session.GetProject();
        Assert.IsFalse(TimelineEditPlanner.Place(f.Project, f.SequenceId, f.MovId,
            f.AudioTrackId, Fixture.Id(20), 0).Success);
        Assert.IsFalse(TimelineEditPlanner.Place(f.Project, f.SequenceId, f.MovId,
            f.VideoTrackId, Fixture.Id(20), long.MaxValue).Success);
        Assert.IsFalse(f.Edit(new SetSequenceDuration(f.SequenceId, Fixture.T)).Success);
        Assert.AreEqual(before, f.Session.GetProject());
    }

    [TestMethod]
    public void StalePlacementDoesNotExtendSequenceOrInsertClip()
    {
        var f = new Fixture();
        var planned = TimelineEditPlanner.Place(f.Project, f.SequenceId, f.WavId,
            f.AudioTrackId, Fixture.Id(20), 70 * Fixture.T, f.Session.GetProject().Revision);
        Assert.IsTrue(f.Edit(new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false)).Success);
        var before = f.Session.GetProject();
        Assert.IsFalse(f.Session.Execute(planned.Value!).Success);
        Assert.AreEqual(before, f.Session.GetProject());
    }
}
