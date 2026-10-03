using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class InspectorPropertyTests
{
    [TestMethod]
    public void NativeCandidatesAreIsolatedAndReleaseIsOneUndoStep()
    {
        var f = new Fixture(); var baseline = f.Session.GetProject();
        var edit = new ClipPropertyEdit(baseline, f.SequenceId, f.ClipId, ClipNumericProperty.Opacity);
        string before = NativeProjectCodec.Serialize(f.Project).Value!;
        foreach (double value in new[] { .8, .6, .4 })
        {
            var candidate = edit.Preview(value); Assert.IsTrue(candidate.Success);
            using var evaluator = TimelineEvaluator.Create(candidate.Value!.Project!, f.SequenceId).Value!;
            Assert.AreEqual(value, evaluator.Evaluate(0).Value!.VideoLayers[0].Appearance.Opacity);
            Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
            Assert.AreEqual(baseline.Revision, f.Session.GetProject().Revision);
        }
        Assert.IsTrue(f.Session.Execute(edit.Batch(.4)).Success);
        Assert.AreEqual(baseline.Revision + 1, f.Session.GetProject().Revision);
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(.4, f.VideoClip.Appearance.Opacity);
    }
    [TestMethod]
    public void StaleGestureCannotOverwriteConcurrentEditAndInvalidCandidateIsRejected()
    {
        var f = new Fixture();
        var edit = new ClipPropertyEdit(f.Session.GetProject(), f.SequenceId, f.ClipId, ClipNumericProperty.ScaleX);
        Assert.IsTrue(edit.IsUnchanged(1)); Assert.IsFalse(edit.Preview(0).Success); Assert.IsFalse(edit.Preview(double.NaN).Success);
        Assert.IsTrue(edit.Preview(12).Success);
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, f.ClipId, true,
            f.VideoClip.Appearance with { Opacity = .7 }, f.VideoClip.Audio)).Success);
        Assert.IsFalse(f.Session.Execute(edit.Batch(2)).Success);
        Assert.AreEqual(.7, f.VideoClip.Appearance.Opacity); Assert.AreEqual(1d, f.VideoClip.Appearance.Transform.ScaleX);
    }
    [TestMethod]
    public void SinglePropertyCommandPreservesOtherValuesAndStableIdentity()
    {
        var f = new Fixture();
        var edit = new ClipPropertyEdit(f.Session.GetProject(), f.SequenceId, f.AudioClipId, ClipNumericProperty.Gain);
        var result = edit.Preview(.5); Assert.IsTrue(result.Success);
        var original = f.Project.Sequences[0].Tracks[1].Clips[0];
        var candidate = result.Value!.Project!.Sequences[0].Tracks[1].Clips[0];
        Assert.AreEqual(original with { Audio = original.Audio with { Gain = .5 } }, candidate);
    }
}
