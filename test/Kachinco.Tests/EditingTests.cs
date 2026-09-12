using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class EditingTests
{
    [TestMethod]
    public void CompoundCreationUndoesAndRedoesAsOneActionWithStableIds()
    {
        var f = new Fixture(); var before = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.IsNull(f.Session.GetProject().Project);
        Assert.IsTrue(f.Session.Redo().Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.AreEqual(3L, f.Session.GetProject().Revision);
    }
    [TestMethod]
    public void InvalidLaterCommandRollsBackEntireBatchAndPreservesHistory()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        var result = f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, 0, Fixture.T),
            new MoveClip(f.SequenceId, f.ClipId, Fixture.Id(99), 0));
        Assert.IsFalse(result.Success); Assert.AreEqual("TRACK_NOT_FOUND", result.Diagnostics[0].Code);
        Assert.AreEqual(before, f.Session.GetProject());
    }
    [TestMethod]
    public void StaleRevisionAndDryRunNeverMutateCommittedState()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        Assert.IsFalse(f.Session.Execute(new([new DeleteClip(f.SequenceId, f.ClipId)], 0)).Success);
        Assert.IsTrue(f.Session.Execute(new([new DeleteClip(f.SequenceId, f.ClipId)], before.Revision, true)).Success);
        Assert.AreEqual(before, f.Session.GetProject());
    }
    [TestMethod]
    public void SplitMapsSourceRangesAndKeepsLeftIdentityThroughHistory()
    {
        var f = new Fixture(); var rightId = Fixture.Id(11);
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.ClipId, 3*Fixture.T, rightId)).Success);
        var clips = TimelineQueries.ListClips(f.Project.Sequences[0].Tracks[0]);
        Assert.AreEqual(f.ClipId, clips[0].Id); Assert.AreEqual(rightId, clips[1].Id);
        Assert.AreEqual(3*Fixture.T, clips[0].DurationTicks);
        Assert.AreEqual(4*Fixture.T, clips[1].SourceInTicks);
        Assert.AreEqual(5*Fixture.T, clips[1].DurationTicks);
        Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.Session.Redo().Success);
        Assert.AreEqual(rightId, f.Project.Sequences[0].Tracks[0].Clips[1].Id);
    }
    [TestMethod]
    [DataRow(0L)] [DataRow(282240000L)]
    public void SplitRejectsClipEdges(long tick)
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        Assert.IsFalse(f.Edit(new SplitClip(f.SequenceId, f.ClipId, tick, Fixture.Id(11))).Success);
        Assert.AreEqual(before, f.Session.GetProject());
    }
    [TestMethod]
    public void TrimAndCrossTrackMoveAreExplicitAndValidated()
    {
        var f = new Fixture(); var newTrack = Fixture.Id(11);
        Assert.IsTrue(f.Edit(new AddTrack(f.SequenceId, newTrack, "V2", TrackKind.Video),
            new TrimClip(f.SequenceId, f.ClipId, Fixture.T, 2*Fixture.T, 3*Fixture.T),
            new MoveClip(f.SequenceId, f.ClipId, newTrack, 4*Fixture.T)).Success);
        Assert.AreEqual(0, f.Project.Sequences[0].Tracks[0].Clips.Length);
        var moved = f.Project.Sequences[0].Tracks[3].Clips[0];
        Assert.AreEqual(2*Fixture.T, moved.SourceInTicks); Assert.AreEqual(7*Fixture.T, moved.EndTicks);
        Assert.IsFalse(f.Edit(new MoveClip(f.SequenceId, f.ClipId, f.AudioTrackId, 0)).Success);
    }
    [TestMethod]
    public void SourceCanBeReusedWithoutConflatingClipIdentity()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Fixture.Id(11), f.MovId, 0, 0, Fixture.T))).Success);
        Assert.AreEqual(2, f.Project.Assets.Length);
        Assert.AreEqual(2, f.Project.Sequences[0].Tracks[0].Clips.Length);
    }
    [TestMethod]
    public void DuplicateIdsAcrossDifferentKindsAndSequencesAreRejected()
    {
        var f = new Fixture();
        var result = f.Edit(new CreateSequence(f.MovId, "Collision", SequenceSettings.Portrait, Fixture.T));
        Assert.IsFalse(result.Success); Assert.AreEqual("INVALID_ID", result.Diagnostics[0].Code);
        Assert.IsFalse(f.Edit(new SplitClip(f.SequenceId, f.ClipId, Fixture.T, f.CaptionId)).Success);
    }
    [TestMethod]
    public void MissingMediaInvalidRangesAndWrongFormatsAreStructured()
    {
        var f = new Fixture();
        Assert.IsFalse(f.Edit(new RegisterMedia(new(Fixture.Id(11), "bad", "image.png", MediaKind.Mov, Fixture.T))).Success);
        Assert.IsFalse(f.Edit(new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Fixture.Id(12), Fixture.Id(99), 0, 0, Fixture.T))).Success);
        Assert.IsFalse(f.Edit(new TrimClip(f.SequenceId, f.ClipId, long.MaxValue, 0, 2)).Success);
        Assert.IsFalse(f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, long.MaxValue, 2)).Success);
    }
    [TestMethod]
    public void NewEditClearsRedoButFailedEditDoesNot()
    {
        var f = new Fixture(); f.Edit(new DeleteClip(f.SequenceId, f.ClipId)); f.Session.Undo();
        Assert.IsTrue(f.Session.GetProject().CanRedo);
        Assert.IsFalse(f.Edit(new DeleteClip(f.SequenceId, Fixture.Id(99))).Success);
        Assert.IsTrue(f.Session.GetProject().CanRedo);
        Assert.IsTrue(f.Edit(new DeleteCaption(f.SequenceId, f.CaptionId)).Success);
        Assert.IsFalse(f.Session.GetProject().CanRedo);
    }
    [TestMethod]
    public void HistoryIsBoundedAndReplacementClearsIt()
    {
        var session = new EditorSession(1);
        session.Execute(new([new CreateProject(Fixture.Id(1), "one")]));
        session.Execute(new([new CreateSequence(Fixture.Id(2), "two", SequenceSettings.Landscape, Fixture.T)]));
        Assert.IsTrue(session.Undo().Success); Assert.IsFalse(session.Undo().Success);
        Assert.IsTrue(session.ReplaceProject(new Fixture().Project).Success);
        Assert.IsFalse(session.GetProject().CanUndo); Assert.IsFalse(session.GetProject().CanRedo);
    }
    [TestMethod]
    public void QueriesReturnImmutableSnapshotsAndDeterministicItemOrder()
    {
        var f = new Fixture(); var snapshot = f.Session.GetProject();
        f.Edit(new DeleteClip(f.SequenceId, f.ClipId));
        Assert.AreEqual(1, snapshot.Project!.Sequences[0].Tracks[0].Clips.Length);
        Assert.AreEqual(0, f.Project.Sequences[0].Tracks[0].Clips.Length);
        var track = snapshot.Project.Sequences[0].Tracks[0];
        track = track with { Clips = [track.Clips[0] with { Id = Fixture.Id(20) }, track.Clips[0] with { Id = Fixture.Id(11) }] };
        Assert.AreEqual(Fixture.Id(11), TimelineQueries.ListClips(track)[0].Id);
    }
    [TestMethod]
    public void TrackOrderAndCaptionKindsAreDomainRules()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new ReorderTrack(f.SequenceId, f.VideoTrackId, 2)).Success);
        Assert.AreEqual(f.VideoTrackId, f.Project.Sequences[0].Tracks[2].Id);
        Assert.IsFalse(f.Edit(new AddCaption(f.SequenceId, f.AudioTrackId, new(Fixture.Id(11), 0, Fixture.T, "wrong"))).Success);
        Assert.IsFalse(f.Edit(new ReorderTrack(f.SequenceId, f.VideoTrackId, 3)).Success);
    }
    [TestMethod]
    public void NullAndDefaultCollectionsAreRejectedWithoutThrowing()
    {
        var f = new Fixture();
        Assert.IsFalse(f.Edit(new RegisterMedia(null!)).Success);
        Assert.IsFalse(f.Edit(new InsertClip(f.SequenceId, f.VideoTrackId, null!)).Success);
        Assert.IsFalse(f.Session.ReplaceProject(f.Project with { Assets = default }).Success);
        Assert.IsFalse(f.Session.Execute(new(default)).Success);
        Assert.IsFalse(f.Session.Execute(new([null!])).Success);
    }
    [TestMethod]
    public async Task ConcurrentExpectedRevisionAllowsExactlyOneCommit()
    {
        var f = new Fixture(); var revision = f.Session.GetProject().Revision;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            f.Session.Execute(new([new DeleteClip(f.SequenceId, f.ClipId)], revision)))));
        Assert.AreEqual(1, results.Count(r => r.Success));
        Assert.AreEqual(7, results.Count(r => r.Diagnostics.Any(d => d.Code == "REVISION_CONFLICT")));
    }
    [TestMethod]
    public void NonFinitePropertiesAndUnknownEnumsAreRejected()
    {
        var f = new Fixture();
        Assert.IsFalse(f.Edit(new SetClipProperties(f.SequenceId, f.ClipId, true,
            new(Transform2D.Identity, double.NaN, BlendMode.Normal), AudioProperties.Default)).Success);
        Assert.IsFalse(f.Edit(new SetClipProperties(f.SequenceId, f.ClipId, true,
            ClipAppearance.Default, new(double.PositiveInfinity, false))).Success);
        Assert.IsFalse(f.Edit(new AddTrack(f.SequenceId, Fixture.Id(11), "invalid", (TrackKind)999)).Success);
    }
}
