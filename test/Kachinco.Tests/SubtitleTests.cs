using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class SubtitleTests
{
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
