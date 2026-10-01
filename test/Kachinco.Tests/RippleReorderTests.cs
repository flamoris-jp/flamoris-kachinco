using System.Collections.Immutable;
using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class RippleReorderTests
{
    private static readonly Guid[] Ids = Enumerable.Range(20, 5).Select(Fixture.Id).ToArray();
    private static Fixture Create()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new SetSequenceDuration(f.SequenceId, 20 * Fixture.T),
            new DeleteClip(f.SequenceId, f.ClipId),
            new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Ids[0], f.MovId, 0, Fixture.T, 3 * Fixture.T)),
            new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Ids[1], f.MovId, 3 * Fixture.T, 2 * Fixture.T, 5 * Fixture.T)),
            new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Ids[2], f.MovId, 8 * Fixture.T, 3 * Fixture.T, 2 * Fixture.T)),
            new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Ids[3], f.MovId, 10 * Fixture.T, Fixture.T, 4 * Fixture.T)),
            new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Ids[4], f.MovId, 16 * Fixture.T, Fixture.T, 2 * Fixture.T))).Success);
        return f;
    }
    private static Clip[] Clips(Project p) => TimelineQueries.ListClips(p.Sequences[0].Tracks[0]).ToArray();

    [TestMethod]
    public void EveryForwardAndBackwardInsertionPreservesRangesAndOneHistoryUnit()
    {
        var f = Create(); using var session = f.Session;
        var original = f.Project;
        for (int origin = 0; origin < 4; origin++)
        for (int slot = 0; slot <= 4; slot++)
        {
            if (slot == origin || slot == origin + 1) continue;
            Assert.IsTrue(session.ReplaceProject(original).Success);
            var before = session.GetProject();
            var plan = TimelineEditPlanner.Reorder(original, f.SequenceId, Ids[origin], slot < 4 ? Ids[slot] : null, before.Revision);
            Assert.IsTrue(plan.Success);
            Assert.AreSame(before, session.GetProject()); // Planning/cancel does not mutate history.
            Assert.IsTrue(session.Execute(plan.Value!.Batch).Success);
            var after = session.GetProject();
            Assert.AreEqual(before.Revision + 1, after.Revision);
            var order = Ids.Take(4).ToList(); order.Remove(Ids[origin]);
            order.Insert(slot > origin ? slot - 1 : slot, Ids[origin]);
            CollectionAssert.AreEqual(order.Append(Ids[4]).ToArray(), Clips(f.Project).Select(c => c.Id).ToArray());
            long cursor = 0;
            foreach (var c in Clips(f.Project).Take(4))
            {
                Assert.AreEqual(cursor, c.StartTicks);
                Assert.AreEqual(Clips(original).Single(x => x.Id == c.Id) with { StartTicks = cursor }, c);
                Assert.AreEqual(cursor, plan.Value.Starts[c.Id]);
                cursor += c.DurationTicks;
            }
            Assert.AreEqual(14 * Fixture.T, cursor);
            Assert.AreEqual(Clips(original)[4], Clips(f.Project)[4]);
            CollectionAssert.AreEqual(original.Sequences[0].Tracks[1].Clips.ToArray(), f.Project.Sequences[0].Tracks[1].Clips.ToArray());
            CollectionAssert.AreEqual(original.Sequences[0].Tracks[2].Captions.ToArray(), f.Project.Sequences[0].Tracks[2].Captions.ToArray());
            Assert.IsTrue(session.Undo(after.Revision).Success);
            Assert.AreEqual(NativeProjectCodec.Serialize(original).Value, NativeProjectCodec.Serialize(f.Project).Value);
            Assert.IsFalse(session.GetProject().CanUndo);
            Assert.IsTrue(session.Redo(session.GetProject().Revision).Success);
            Assert.AreEqual(NativeProjectCodec.Serialize(after.Project!).Value, NativeProjectCodec.Serialize(f.Project).Value);
            Assert.IsFalse(session.GetProject().CanRedo);
            var encoded = NativeProjectCodec.Serialize(f.Project);
            Assert.AreEqual(encoded.Value, NativeProjectCodec.Serialize(NativeProjectCodec.Deserialize(encoded.Value).Value!).Value);
        }
    }

    [TestMethod]
    public void InsertionHitTestingAndAdjacentActionsUseTheSameNativePlan()
    {
        var f = Create(); using var session = f.Session;
        var track = f.Project.Sequences[0].Tracks[0];
        Assert.AreEqual(Ids[1], TimelineEditPlanner.InsertionTarget(track, Ids[2], 4 * Fixture.T).Value);
        Assert.AreEqual(Ids[2], TimelineEditPlanner.InsertionTarget(track, Ids[0], 6 * Fixture.T).Value);
        Assert.IsFalse(TimelineEditPlanner.InsertionTarget(track, Ids[2], 15 * Fixture.T).Success);
        var pointer = TimelineEditPlanner.ReorderAt(f.Project, f.SequenceId, Ids[2], 4 * Fixture.T);
        var earlier = TimelineEditPlanner.ReorderAdjacent(f.Project, f.SequenceId, Ids[2], true);
        Assert.AreEqual(pointer.Value!.Batch.Commands[0], earlier.Value!.Batch.Commands[0]);
        Assert.AreEqual(3 * Fixture.T, pointer.Value.InsertionTicks);
        Assert.AreEqual(5 * Fixture.T, pointer.Value.Starts[Ids[1]]);
        var later = TimelineEditPlanner.ReorderAdjacent(f.Project, f.SequenceId, Ids[0], false);
        Assert.AreEqual(5 * Fixture.T, later.Value!.InsertionTicks);
        Assert.IsFalse(TimelineEditPlanner.ReorderAdjacent(f.Project, f.SequenceId, Ids[3], false).Success);
        Assert.IsFalse(TimelineEditPlanner.ReorderAdjacent(f.Project, f.SequenceId, Ids[4], true).Success);
    }

    [TestMethod]
    public void GapWrongTrackOverlapAndNoopRejectWithoutTouchingHistory()
    {
        var f = Create(); using var session = f.Session;
        var snapshot = session.GetProject();
        foreach (var target in new[] { Ids[4], f.AudioClipId, Guid.NewGuid(), Ids[2], Ids[3] })
        {
            Assert.IsFalse(session.Execute(new([new RippleReorderClip(f.SequenceId, Ids[2], target)], snapshot.Revision)).Success);
            Assert.AreSame(snapshot, session.GetProject());
        }
        Assert.IsTrue(f.Edit(new InsertClip(f.SequenceId, f.VideoTrackId,
            Fixture.Clip(Fixture.Id(99), f.MovId, Fixture.T, 0, Fixture.T))).Success);
        snapshot = session.GetProject();
        var result = session.Execute(new([new RippleReorderClip(f.SequenceId, Ids[2], Ids[0])]));
        Assert.AreEqual("CLIP_OVERLAP", result.Diagnostics[0].Code);
        Assert.AreSame(snapshot, session.GetProject());
    }

    [TestMethod]
    public void DryRunCancellationAndStaleRevisionLeaveTheSessionUntouched()
    {
        var f = Create(); using var session = f.Session;
        var snapshot = session.GetProject();
        var plan = TimelineEditPlanner.Reorder(f.Project, f.SequenceId, Ids[2], Ids[0], snapshot.Revision).Value!;
        Assert.IsTrue(session.Execute(plan.Batch with { DryRun = true }).Success);
        Assert.AreSame(snapshot, session.GetProject());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => session.Execute(plan.Batch, cancelled.Token));
        Assert.AreSame(snapshot, session.GetProject());
        Assert.IsTrue(f.Edit(new SetTrackEnabled(f.SequenceId, f.AudioTrackId, false)).Success);
        snapshot = session.GetProject();
        Assert.AreEqual("REVISION_CONFLICT", session.Execute(plan.Batch).Diagnostics[0].Code);
        Assert.AreSame(snapshot, session.GetProject());
    }

    [TestMethod]
    public void FreeSpaceAndCompatibleCrossTrackMovesRemainOrdinaryMoves()
    {
        var f = Create(); using var session = f.Session;
        Assert.IsFalse(TimelineEditPlanner.ReorderAt(f.Project, f.SequenceId, Ids[2], 18 * Fixture.T + 1).Success);
        var free = TimelineEditPlanner.Move(f.Project, f.SequenceId, Ids[2], f.VideoTrackId, 18 * Fixture.T);
        Assert.IsTrue(free.Success); Assert.IsTrue(f.Edit(free.Value!).Success);
        var other = Fixture.Id(100);
        Assert.IsTrue(f.Edit(new AddTrack(f.SequenceId, other, "V2", TrackKind.Video)).Success);
        var cross = TimelineEditPlanner.Move(f.Project, f.SequenceId, Ids[2], other, 0);
        Assert.IsTrue(cross.Success); Assert.IsTrue(f.Edit(cross.Value!).Success);
        Assert.IsFalse(TimelineEditPlanner.Move(f.Project, f.SequenceId, Ids[0], f.AudioTrackId, 0).Success);
    }

    [TestMethod]
    public void ReorderUsesExactTicksNearInt64LimitAndInAudioRuns()
    {
        var f = Create(); using var session = f.Session;
        var p = f.Project; var seq = p.Sequences[0];
        long anchor = long.MaxValue - 20;
        var clips = seq.Tracks[0].Clips.Take(4).Select((c, i) => c with {
            StartTicks = anchor + new long[] { 0, 3, 8, 10 }[i],
            DurationTicks = new long[] { 3, 5, 2, 4 }[i], MediaAssetId = f.WavId }).ToImmutableArray();
        var audio = seq.Tracks[1] with { Clips = clips };
        p = p with { Sequences = [seq with { DurationTicks = long.MaxValue,
            Tracks = [seq.Tracks[0] with { Clips = [] }, audio, seq.Tracks[2]] }] };
        Assert.IsTrue(session.ReplaceProject(p).Success);
        var plan = TimelineEditPlanner.Reorder(f.Project, seq.Id, Ids[2], Ids[1]);
        Assert.IsTrue(plan.Success); Assert.IsTrue(session.Execute(plan.Value!.Batch).Success);
        var ordered = TimelineQueries.ListClips(f.Project.Sequences[0].Tracks[1]);
        CollectionAssert.AreEqual(new[] { anchor, anchor + 3, anchor + 5, anchor + 10 }, ordered.Select(c => c.StartTicks).ToArray());
        Assert.AreEqual(long.MaxValue, f.Project.Sequences[0].DurationTicks);
    }

    [TestMethod]
    public void DuplicateKeepsAllPropertiesFindsFreeSpaceAndExtendsInOneBatch()
    {
        var f = Create(); using var session = f.Session;
        var source = Clips(f.Project)[1] with { Enabled = false,
            Appearance = new(new(12, -7, 2, 3, 19), .4, BlendMode.Screen), Audio = new(2, true) };
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, source.Id, source.Enabled, source.Appearance, source.Audio)).Success);
        source = Clips(f.Project)[1];
        var snapshot = session.GetProject(); var duplicateId = Fixture.Id(110);
        var plan = TimelineEditPlanner.Duplicate(f.Project, f.SequenceId, source.Id, duplicateId, snapshot.Revision);
        Assert.IsTrue(plan.Success); Assert.IsTrue(session.Execute(plan.Value!).Success);
        Assert.AreEqual(source with { Id = duplicateId, StartTicks = 18 * Fixture.T }, Clips(f.Project).Single(c => c.Id == duplicateId));
        Assert.AreEqual(23 * Fixture.T, f.Project.Sequences[0].DurationTicks);
        Assert.IsTrue(session.Undo().Success);
        Assert.AreEqual(NativeProjectCodec.Serialize(snapshot.Project!).Value, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsFalse(TimelineEditPlanner.Duplicate(f.Project, f.SequenceId, source.Id, source.Id).Success);
        // A shorter duplicate fills the real free interval without shifting later clips.
        var shortPlan = TimelineEditPlanner.Duplicate(f.Project, f.SequenceId, Ids[2], duplicateId);
        Assert.IsTrue(session.Execute(shortPlan.Value!).Success);
        Assert.AreEqual(14 * Fixture.T, Clips(f.Project).Single(c => c.Id == duplicateId).StartTicks);
        Assert.AreEqual(16 * Fixture.T, Clips(f.Project).Single(c => c.Id == Ids[4]).StartTicks);
    }
}
