using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class CaptionTimingTests
{
    private static readonly TimelineViewport Viewport = new(100m);

    [TestMethod]
    public void MoveAndBothEdgesPreserveStyleTextAndSingleUndoRedo()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new SetCaptionStyle(f.SequenceId, f.CaptionId, new(Color: "#FFEEDD", FontSize: 58))).Success);
        foreach (TrimEdge? edge in new TrimEdge?[] { null, TrimEdge.Start, TrimEdge.End })
        {
            var sequence = f.Project.Sequences[0];
            var caption = sequence.Tracks[2].Captions[0];
            var before = NativeProjectCodec.Serialize(f.Project).Value;
            var command = CaptionTimingEdits.Plan(sequence, caption, Fixture.T / 2, edge, Viewport, false, 0);
            Assert.IsTrue(f.Edit(command).Success);
            var actual = f.Project.Sequences[0].Tracks[2].Captions[0];
            Assert.AreEqual(caption.Text, actual.Text); Assert.AreEqual(caption.Style, actual.Style);
            Assert.AreEqual(caption.Enabled, actual.Enabled);
            Assert.AreEqual(edge == TrimEdge.End ? caption.StartTicks : caption.StartTicks + Fixture.T / 2, actual.StartTicks);
            long end = caption.StartTicks + caption.DurationTicks;
            Assert.AreEqual(edge == TrimEdge.Start ? end : end + Fixture.T / 2, actual.StartTicks + actual.DurationTicks);
            var after = NativeProjectCodec.Serialize(f.Project).Value;
            Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
            Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(after, NativeProjectCodec.Serialize(f.Project).Value);
            Assert.IsTrue(f.Session.Undo().Success);
        }
    }

    [TestMethod]
    public void BoundariesCannotInvertOrEscapeSequenceAndExtremeDeltasClamp()
    {
        var f = new Fixture(); var s = f.Project.Sequences[0]; var c = s.Tracks[2].Captions[0];
        Assert.AreEqual(0, Plan(long.MinValue, null).StartTicks);
        Assert.AreEqual(s.DurationTicks - c.DurationTicks, Plan(long.MaxValue, null).StartTicks);
        Assert.AreEqual(0, Plan(long.MinValue, TrimEdge.Start).StartTicks);
        var start = Plan(long.MaxValue, TrimEdge.Start);
        Assert.AreEqual(TimelineTime.FrameToTicks(1, s.Settings.FrameRate), start.DurationTicks);
        Assert.AreEqual(c.StartTicks + c.DurationTicks, start.StartTicks + start.DurationTicks);
        var end = Plan(long.MinValue, TrimEdge.End);
        Assert.AreEqual(c.StartTicks, end.StartTicks);
        Assert.AreEqual(start.DurationTicks, end.DurationTicks);
        Assert.AreEqual(s.DurationTicks, Plan(long.MaxValue, TrimEdge.End).StartTicks + Plan(long.MaxValue, TrimEdge.End).DurationTicks);
        Assert.AreEqual(c.DurationTicks, Plan(0, null).DurationTicks);
        UpdateCaption Plan(long delta, TrimEdge? edge) => CaptionTimingEdits.Plan(s, c, delta, edge, Viewport, false, 0);
    }

    [TestMethod]
    public void SnapUsesAdjacentCaptionsAndBothMoveEdgesAndCanBeDisabled()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new AddCaption(f.SequenceId, f.SubtitleTrackId, new(Guid.NewGuid(), 4 * Fixture.T, Fixture.T, "Next"))).Success);
        var s = f.Project.Sequences[0]; var c = s.Tracks[2].Captions[0];
        long delta = TimelineTime.SecondsToTicks(.96m);
        var snapped = CaptionTimingEdits.Plan(s, c, delta, null, Viewport, true, 0);
        Assert.AreEqual(2 * Fixture.T, snapped.StartTicks); // trailing edge touches next start
        Assert.AreEqual(c.DurationTicks, snapped.DurationTicks);
        var free = CaptionTimingEdits.Plan(s, c, delta, null, Viewport, false, 0);
        Assert.AreNotEqual(snapped.StartTicks, free.StartTicks);
        var trimmed = CaptionTimingEdits.Plan(s, c, delta, TrimEdge.End, Viewport, true, 0);
        Assert.AreEqual(4 * Fixture.T, trimmed.StartTicks + trimmed.DurationTicks);
        // Snapping must never collapse a caption onto its fixed opposite edge.
        var tiny = CaptionTimingEdits.Plan(s, c, -2 * Fixture.T, TrimEdge.End, Viewport, true, c.StartTicks);
        Assert.IsTrue(tiny.DurationTicks > 0);
    }

    [TestMethod]
    public void SubframeImportZeroDeltaAndDurationRemainExactAndStaleDropIsRejected()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new UpdateCaption(f.SequenceId, f.CaptionId, Fixture.T + 123, Fixture.T + 457, "lyric", true)).Success);
        var snapshot = f.Session.GetProject(); var s = snapshot.Project!.Sequences[0]; var c = s.Tracks[2].Captions[0];
        var unchanged = CaptionTimingEdits.Plan(s, c, 0, null, Viewport, true, 0);
        Assert.AreEqual(c.StartTicks, unchanged.StartTicks); Assert.AreEqual(c.DurationTicks, unchanged.DurationTicks);
        var command = CaptionTimingEdits.Plan(s, c, Fixture.T, null, Viewport, false, 0);
        Assert.AreEqual(c.DurationTicks, command.DurationTicks);
        Assert.IsTrue(f.Edit(new PatchCaptionStyles(f.SequenceId, [f.CaptionId], new(Color: "#00FFFF"))).Success);
        var before = NativeProjectCodec.Serialize(f.Project).Value;
        Assert.IsFalse(f.Session.Execute(new([command], snapshot.Revision)).Success);
        Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
    }
}
