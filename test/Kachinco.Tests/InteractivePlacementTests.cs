using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class InteractivePlacementTests
{
    [TestMethod]
    public void NewAudioAndVideoTracksPlaceAtomicallyWithOneUndoAndSurviveSaveOpen()
    {
        foreach (bool video in new[] {false, true})
        {
            var f = new Fixture(); var before = f.Session.GetProject(); var id = Fixture.Id(800); var clip = Fixture.Id(801);
            var plan = TimelineEditPlanner.PlaceOnNewTrack(f.Project, f.SequenceId, video ? f.MovId : f.WavId, id, clip, Fixture.T, before.Revision);
            Assert.IsTrue(plan.Success); Assert.IsTrue(f.Session.Execute(plan.Value!).Success);
            var after = f.Session.GetProject(); Assert.AreEqual(before.Revision + 1, after.Revision);
            var lanes = TimelineLanes.Create(after.Project!.Sequences[0]);
            int added = Array.FindIndex(lanes, l => l.TrackId == id);
            int original = Array.FindIndex(lanes, l => l.TrackId == (video ? f.VideoTrackId : f.AudioTrackId));
            Assert.AreEqual(original + 1, added); Assert.IsNull(lanes[added + 1].TrackId);
            Assert.AreEqual(video ? "V2" : "A2", after.Project.Sequences[0].Tracks.First(t => t.Id == id).Name);
            Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before.Project, f.Session.GetProject().Project);
            Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(after.Project, f.Session.GetProject().Project);
            var json = ProjectJson.Serialize(after.Project).Value!; var opened = ProjectJson.Deserialize(json);
            Assert.IsTrue(opened.Success); Assert.IsTrue(opened.Value!.Sequences[0].Tracks.Any(t => t.Id == id && t.Clips.Single().Id == clip));
        }
    }
    [TestMethod]
    public void ExistingTrackIsExactAndAccidentalOverlapIsRejectedWithoutMutation()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        var overlap = TimelineEditPlanner.Place(f.Project, f.SequenceId, f.WavId, f.AudioTrackId, Fixture.Id(900), 0);
        Assert.IsFalse(overlap.Success); Assert.AreEqual("CLIP_OVERLAP", overlap.Diagnostics[0].Code); Assert.AreEqual(before, f.Session.GetProject());
        var free = TimelineEditPlanner.Place(f.Project, f.SequenceId, f.WavId, f.AudioTrackId, Fixture.Id(901), 8 * Fixture.T);
        Assert.IsTrue(free.Success); Assert.IsTrue(f.Session.Execute(free.Value!).Success);
        Assert.AreEqual(3, f.Project.Sequences[0].Tracks.Length); Assert.AreEqual(2, f.Project.Sequences[0].Tracks.Single(t => t.Id == f.AudioTrackId).Clips.Length);
    }
    [TestMethod]
    public void ThumbnailPlanUsesVisibleSourceRangeWithHardWorkBound()
    {
        var f = new Fixture(); var clip = f.VideoClip;
        var a = ThumbnailStrip.Plan(clip, new(80), 320, 320);
        Assert.IsTrue(a.Length > 1); Assert.IsTrue(a.All(s => s.SourceTicks >= clip.SourceInTicks + 4 * Fixture.T && s.SourceTicks < clip.SourceInTicks + clip.DurationTicks));
        var huge = ThumbnailStrip.Plan(clip, new(1600), 0, 100000);
        Assert.IsTrue(huge.Length <= ThumbnailStrip.MaximumSlots);
        Assert.AreEqual(clip.SourceInTicks, huge[0].SourceTicks);
        Assert.AreEqual(0, ThumbnailStrip.Plan(clip, new(80), 9000, 100).Length);
    }
}
