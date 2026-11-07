using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class SubtitleTests
{
    [TestMethod]
    public void ImportAtPlayheadPreservesGapsAndIsOneUndoableBatch()
    {
        using var session = new EditorSession();
        var sequence = Guid.NewGuid();
        Assert.IsTrue(session.Execute(new([new CreateProject(Guid.NewGuid(), "SRT"),
            new CreateSequence(sequence, "S", SequenceSettings.Landscape, 2 * Fixture.T)])).Success);
        var before = ProjectJson.Serialize(session.GetProject().Project!).Value;
        var cues = SrtCodec.Parse("1\n00:00:01,000 --> 00:00:02,000\none\n\n2\n00:00:04,000 --> 00:00:05,000\ntwo\n").Value;
        var snapshot = session.GetProject();
        var batch = CaptionImport.Plan(snapshot.Project!.Sequences[0], cues, 10 * Fixture.T, snapshot.Revision);
        Assert.IsTrue(session.Execute(batch).Success);
        var result = session.GetProject().Project!.Sequences[0];
        Assert.AreEqual(15 * Fixture.T, result.DurationTicks);
        Assert.AreEqual(11 * Fixture.T, result.Tracks[0].Captions[0].StartTicks);
        Assert.AreEqual(14 * Fixture.T, result.Tracks[0].Captions[1].StartTicks);
        Assert.AreEqual(Fixture.T, result.Tracks[0].Captions[1].DurationTicks);
        Assert.AreEqual(cues[0].Id, result.Tracks[0].Captions[0].Id);
        var saved = ProjectJson.Serialize(session.GetProject().Project!).Value;
        Assert.IsTrue(ProjectJson.Deserialize(saved!).Success);
        Assert.IsTrue(session.Undo().Success);
        Assert.AreEqual(before, ProjectJson.Serialize(session.GetProject().Project!).Value);
        Assert.IsTrue(session.Redo().Success);
        Assert.AreEqual(saved, ProjectJson.Serialize(session.GetProject().Project!).Value);
    }

    [TestMethod]
    public void ImportReusesSubtitleTrackAndRejectsOverflowBeforeMutation()
    {
        var f = new Fixture();
        var sequence = f.Project.Sequences[0];
        var cues = SrtCodec.Parse("1\n00:00:00,000 --> 00:00:01,000\nnew\n").Value;
        var original = ProjectJson.Serialize(f.Project).Value;
        Assert.ThrowsExactly<OverflowException>(() => CaptionImport.Plan(sequence, cues, long.MaxValue, f.Session.GetProject().Revision));
        Assert.AreEqual(original, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Execute(CaptionImport.Plan(sequence, cues, 0, f.Session.GetProject().Revision)).Success);
        Assert.AreEqual(sequence.Tracks.Length, f.Project.Sequences[0].Tracks.Length);
    }

    [TestMethod]
    public void MultilineJapaneseSrtRoundTripsAtMillisecondPrecision()
    {
        const string srt = "1\n00:00:01,234 --> 00:00:03,456\n存在薄明\nBefore the first\n\n";
        var parsed = SrtCodec.Parse(srt);
        Assert.IsTrue(parsed.Success);
        Assert.AreEqual(TimelineTime.SecondsToTicks(1.234m), parsed.Value[0].StartTicks);
        Assert.AreEqual(srt, SrtCodec.Write(parsed.Value).Value);
        Assert.IsFalse(SrtCodec.Parse("1\n00:00:03,000 --> 00:00:01,000\nbad").Success);
        Assert.IsFalse(SrtCodec.Parse("1\n00:61:00,000 --> 00:62:00,000\nbad").Success);
    }
    [TestMethod]
    public void CaptionEditKeepsIdentityAndUndoAndPersistence()
    {
        var f = new Fixture();
        var before = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(f.Edit(new UpdateCaption(f.SequenceId, f.CaptionId, 2 * Fixture.T, Fixture.T, "グエー", true)).Success);
        var current = f.Project.Sequences[0].Tracks[2].Captions[0];
        Assert.AreEqual(f.CaptionId, current.Id); Assert.AreEqual("グエー", current.Text);
        Assert.IsTrue(ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!).Success);
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
    }
}
