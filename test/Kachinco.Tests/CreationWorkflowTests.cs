using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class CreationWorkflowTests
{
    [TestMethod]
    public void UnregisterSharesHistoryAndNeverDeletesSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "source.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var f = new Fixture(); var id = Fixture.Id(100);
            Assert.IsTrue(f.Edit(new RegisterMedia(new(id, "unused", path, MediaKind.Image, Fixture.T))).Success);
            string before = ProjectJson.Serialize(f.Project).Value!;
            Assert.IsTrue(f.Edit(new UnregisterMedia(id)).Success);
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(f.Project.Assets.Any(a => a.Id == id));
            Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
            Assert.IsTrue(f.Session.Redo().Success);
            Assert.IsTrue(ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!).Success);
            string after = ProjectJson.Serialize(f.Project).Value!;
            Assert.IsFalse(f.Edit(new UnregisterMedia(f.MovId)).Success);
            Assert.IsFalse(f.Edit(new UnregisterMedia(Guid.NewGuid())).Success);
            Assert.AreEqual(after, ProjectJson.Serialize(f.Project).Value);
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public void TitleMetadataIsConditionalStrictAndRoundTripsExactCapturedTicks()
    {
        var f = new Fixture(); string legacy = ProjectJson.Serialize(f.Project).Value!;
        var image = Fixture.Id(100);
        Assert.IsTrue(f.Edit(new RegisterMedia(new(image, "title", "title.png", MediaKind.Image, Fixture.T)),
            new SetTitleImage(f.SequenceId, new(image, 9_007_199_254_740_993))).Success);
        string json = ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(6, JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
        StringAssert.Contains(json, "9007199254740993");
        var read = ProjectJson.Deserialize(json); Assert.IsTrue(read.Success);
        Assert.AreEqual(json, ProjectJson.Serialize(read.Value!).Value);
        var bad = JsonNode.Parse(json)!;
        bad["project"]!["sequences"]![0]!["titleImage"]!["unexpected"] = true;
        Assert.IsFalse(ProjectJson.Deserialize(bad.ToJsonString()).Success);
        bad = JsonNode.Parse(json)!;
        bad["project"]!["sequences"]![0]!["titleImage"]!["sourceFrameTicks"] = 123;
        Assert.IsFalse(ProjectJson.Deserialize(bad.ToJsonString()).Success);
        Assert.IsFalse(f.Edit(new UnregisterMedia(image)).Success);
        Assert.IsTrue(f.Edit(new SetTitleImage(f.SequenceId, null), new UnregisterMedia(image)).Success);
        Assert.AreEqual(legacy, ProjectJson.Serialize(f.Project).Value);
        Assert.IsFalse(f.Edit(new SetTitleImage(f.SequenceId, new(f.MovId))).Success);
        Assert.IsFalse(f.Edit(new SetTitleImage(f.SequenceId, new(Guid.NewGuid()))).Success);
    }
    [TestMethod]
    public void TitleSlotShiftsAllContentOnceAndReleaseReversesWithoutChangingSources()
    {
        foreach (var fps in new[] { new FrameRate(30, 1), new FrameRate(30000, 1001), new FrameRate(24, 1) })
        {
            var f = new Fixture();
            var original = f.Project.Sequences[0];
            Assert.IsTrue(f.Session.ReplaceProject(f.Project with { Sequences = [original with { Settings = original.Settings with { FrameRate = fps } }] }).Success);
            Assert.IsTrue(f.Edit(new AddClapper(f.SequenceId, new(Fixture.Id(100), "range", Fixture.T, Fixture.T, null, f.VideoTrackId, f.ClipId, ""))).Success);
            string before = ProjectJson.Serialize(f.Project).Value!;
            var baseline = f.Project.Sequences[0];
            long frame = TimelineTime.FrameToTicks(1, fps);
            var reserve = new ReserveTitleFrame(f.SequenceId, Fixture.Id(101), Fixture.Id(102));
            Assert.IsTrue(f.Edit(reserve).Success);
            var shifted = f.Project.Sequences[0];
            Assert.AreEqual(baseline.DurationTicks + frame, shifted.DurationTicks);
            Assert.AreEqual(frame, f.VideoClip.StartTicks); Assert.AreEqual(Fixture.T, f.VideoClip.SourceInTicks);
            Assert.AreEqual(Fixture.T + frame, shifted.Tracks[1].Clips[0].StartTicks);
            Assert.AreEqual(Fixture.T + frame, shifted.Tracks[2].Captions[0].StartTicks);
            Assert.AreEqual(Fixture.T + frame, shifted.Clappers[0].StartTicks);
            string once = ProjectJson.Serialize(f.Project).Value!;
            Assert.IsTrue(f.Edit(reserve).Success); Assert.AreEqual(once, ProjectJson.Serialize(f.Project).Value);
            Assert.IsTrue(f.Edit(new ReleaseTitleFrame(f.SequenceId)).Success);
            Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
            Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(once, ProjectJson.Serialize(f.Project).Value);
            Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        }
    }
    [TestMethod]
    public void TitleImageReplacementUpdatesTheOneFrameSlotAndSharedEvaluation()
    {
        var f = new Fixture(); var a = Fixture.Id(100); var b = Fixture.Id(101);
        Assert.IsTrue(f.Edit(new RegisterMedia(new(a, "A", "a.png", MediaKind.Image, Fixture.T)),
            new RegisterMedia(new(b, "B", "b.jpg", MediaKind.Image, Fixture.T)),
            new ReserveTitleFrame(f.SequenceId, Fixture.Id(102), Fixture.Id(103)), new SetTitleImage(f.SequenceId, new(a))).Success);
        var slot = f.Project.Sequences[0].Tracks.Last().Clips.Single();
        long frame = TimelineTime.FrameToTicks(1, new(30, 1));
        Assert.AreEqual(frame, slot.DurationTicks); Assert.AreEqual(0L, slot.StartTicks);
        using (var evaluation = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!)
        {
            Assert.AreEqual(a, evaluation.Evaluate(0).Value!.VideoLayers.Single().MediaAssetId);
            Assert.IsTrue(evaluation.Evaluate(frame).Value!.VideoLayers.All(l => l.MediaAssetId != a));
        }
        Assert.IsTrue(f.Edit(new SetTitleImage(f.SequenceId, new(b))).Success);
        Assert.AreEqual(slot.Id, f.Project.Sequences[0].Tracks.Last().Clips.Single().Id);
        Assert.AreEqual(b, f.Project.Sequences[0].Tracks.Last().Clips.Single().MediaAssetId);
        string json = ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(json, ProjectJson.Serialize(ProjectJson.Deserialize(json).Value!).Value);
        Assert.IsFalse(f.Edit(new DeleteClip(f.SequenceId, slot.Id)).Success);
        Assert.IsTrue(f.Edit(new SetTitleImage(f.SequenceId, null)).Success);
        Assert.AreEqual(0, f.Project.Sequences[0].Tracks.Last().Clips.Length);
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.AreEqual(json, ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void SlotReleaseRejectsNewContentAtZeroAndRollsBackEveryTrack()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new ReserveTitleFrame(f.SequenceId, Fixture.Id(100), Fixture.Id(101))).Success);
        Assert.IsTrue(f.Edit(new InsertClip(f.SequenceId, f.VideoTrackId,
            Fixture.Clip(Fixture.Id(102), f.MovId, 0, 0, TimelineTime.FrameToTicks(1, new(30, 1))))).Success);
        string before = ProjectJson.Serialize(f.Project).Value!;
        Assert.IsFalse(f.Edit(new ReleaseTitleFrame(f.SequenceId)).Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void CrossfadeUsesOrdinaryNativeOpacityAndPreservesOtherTimingAndCurves()
    {
        var f = CrossfadeFixture(); var id = Fixture.Id(100); var baseline = f.Project;
        var fade = new ApplyCrossfade(f.SequenceId, f.ClipId, id, Fixture.T, Fixture.Id(101), Fixture.Id(102));
        Assert.IsTrue(f.Edit(fade).Success);
        var sequence = f.Project.Sequences[0];
        Assert.AreEqual(5 * Fixture.T, f.VideoClip.DurationTicks);
        var incoming = sequence.Tracks[0].Clips.Single(c => c.Id == id);
        Assert.AreEqual(4 * Fixture.T, incoming.StartTicks); Assert.AreEqual(4 * Fixture.T, incoming.DurationTicks);
        CollectionAssert.AreEqual(baseline.Sequences[0].Tracks[1].Clips.ToArray(), sequence.Tracks[1].Clips.ToArray());
        using (var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!)
        {
            var layers = evaluator.Evaluate(TimelineTime.SecondsToTicks(4.5m)).Value!.VideoLayers;
            Assert.AreEqual(2, layers.Length); Assert.AreEqual(.5, layers.Last().Appearance.Opacity, 1e-9);
        }
        string json = ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(json, ProjectJson.Serialize(ProjectJson.Deserialize(json).Value!).Value);
        Assert.IsFalse(f.Edit(fade).Success); // existing curve cannot be silently replaced
        Assert.IsTrue(f.Edit(new UpdateClipPropertyPoint(f.SequenceId, id, VisualProperty.Opacity, new(Fixture.Id(102), Fixture.T, .8))).Success);
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, id, new(VisualProperty.Opacity, []))).Success);
        Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.Session.Undo().Success);
        Assert.AreEqual(ProjectJson.Serialize(baseline).Value, ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void CrossfadeInvalidHandlesDurationAndCurveIdsAreAtomic()
    {
        var f = CrossfadeFixture(); var before = ProjectJson.Serialize(f.Project).Value;
        foreach (long duration in new[] { 0L, -1L, 5 * Fixture.T })
            Assert.IsFalse(f.Edit(new ApplyCrossfade(f.SequenceId, f.ClipId, Fixture.Id(100), duration, Fixture.Id(101), Fixture.Id(102))).Success);
        Assert.IsFalse(f.Edit(new ApplyCrossfade(f.SequenceId, f.ClipId, Fixture.Id(100), Fixture.T, Fixture.Id(101), Fixture.Id(101))).Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, 6 * Fixture.T, 4 * Fixture.T)).Success);
        before = ProjectJson.Serialize(f.Project).Value;
        Assert.IsFalse(f.Edit(new ApplyCrossfade(f.SequenceId, f.ClipId, Fixture.Id(100), Fixture.T, Fixture.Id(101), Fixture.Id(102))).Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
    }
    private static Fixture CrossfadeFixture()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, Fixture.T, 4 * Fixture.T),
            new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Fixture.Id(100), f.MovId, 4 * Fixture.T, 0, 4 * Fixture.T))).Success);
        return f;
    }
    [TestMethod]
    public void FollowPagesOnlyAtThresholdAndClampsShortAndFinalViews()
    {
        Assert.AreEqual(0d, TimelineFollow.NextOffset(84, 100, 0, 900));
        Assert.AreEqual(65d, TimelineFollow.NextOffset(85, 100, 0, 900));
        Assert.AreEqual(65d, TimelineFollow.NextOffset(86, 100, 65, 900));
        Assert.AreEqual(900d, TimelineFollow.NextOffset(999, 100, 900, 900));
        Assert.AreEqual(0d, TimelineFollow.NextOffset(5, 100, 200, 900));
        Assert.AreEqual(0d, TimelineFollow.NextOffset(90, 100, 0, 0));
        Assert.AreEqual(20d, TimelineFollow.NextOffset(90, 0, 20, 900));
    }
    [TestMethod]
    public void WindowPreferenceIsBoundedAndSurvivesMalformedOrUnwritableFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "window.json"); var preferences = new WindowPlacementPreferences(path);
            Assert.IsNull(preferences.Load());
            var value = new EditorWindowPlacement(-1280, 10, 1320, 820, true);
            Assert.IsTrue(preferences.Save(value)); Assert.AreEqual(value, preferences.Load());
            Assert.IsFalse(preferences.Save(value with { Width = double.NaN }));
            Assert.AreEqual(value, preferences.Load());
            File.WriteAllText(path, "{bad"); Assert.IsNull(preferences.Load());
            File.WriteAllText(path, new string(' ', 5000)); Assert.IsNull(preferences.Load());
            Assert.IsFalse(new WindowPlacementPreferences(directory).Save(value));
        }
        finally { Directory.Delete(directory, true); }
    }
}
