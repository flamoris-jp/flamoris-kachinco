using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class EditorUxTests
{
    [TestMethod]
    public void SharedRowBoundariesAndHitAreasAreHalfOpenAtEveryHeight()
    {
        foreach (double height in new[] { 32d, 72d, 104d })
        {
            var geometry = new TimelineTrackGeometry(20, height);
            for (int i = 0; i < geometry.Count; i++)
            {
                var row = geometry.Row(i);
                Assert.AreEqual(i, geometry.HitRow(row.Top));
                Assert.AreEqual(i, geometry.HitRow(row.Bottom - .01));
                Assert.AreEqual(i + 1 < geometry.Count ? i + 1 : -1, geometry.HitRow(row.Bottom));
                Assert.IsTrue(row.ClipTop >= row.Top && row.ClipTop + row.ClipHeight <= row.Bottom);
            }
            Assert.AreEqual(-1, geometry.HitRow(-1));
            Assert.AreEqual(-1, geometry.HitRow(double.NaN));
        }
    }

    [TestMethod]
    public void ZoomScrollPointerRulerPlayheadAndClipShareTimeZero()
    {
        foreach (decimal zoom in new[] { .25m, 4m, 80m, 1600m })
        foreach (decimal scroll in new[] { 0m, 37.5m, 5000m })
        {
            var coordinates = new TimelineCoordinates(new(zoom), scroll);
            long t = TimelineTime.SecondsToTicks(120.375m);
            Assert.AreEqual(t, coordinates.ViewXToTicks(coordinates.ViewX(t)));
            Assert.AreEqual(t, coordinates.SurfaceXToTicks(coordinates.SurfaceX(t)));
            Assert.AreEqual(coordinates.ContentX(t) - scroll, coordinates.ViewX(t));
            Assert.AreEqual((decimal)TimelineTrackGeometry.HeaderWidth, coordinates.SurfaceX(t) - coordinates.ViewX(t));
            long end = t + TimelineTime.FrameToTicks(1, new(30000, 1001));
            Assert.IsTrue(Math.Abs(coordinates.ContentX(end) - coordinates.ContentX(t) - coordinates.Viewport.TicksToPixels(end - t)) < .000000000000000001m);
        }
    }

    [TestMethod]
    public void ScrolledDropUsesQuantizedPointerTimeAndUndoRestoresPlacement()
    {
        var f = new Fixture();
        var before = ProjectJson.Serialize(f.Project).Value;
        var coordinates = new TimelineCoordinates(new(125m), 187.5m);
        long expected = TimelineTime.SecondsToTicks(4m);
        long ticks = coordinates.PlacementTicks(312.5m, new(30, 1), false, []);
        Assert.AreEqual(expected, ticks);
        var plan = TimelineEditPlanner.Place(f.Project, f.SequenceId, f.MovId, f.VideoTrackId, Guid.NewGuid(), ticks);
        Assert.IsTrue(f.Session.Execute(plan.Value!).Success);
        var after = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(ProjectJson.Deserialize(after!).Success);
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success);
        Assert.AreEqual(after, ProjectJson.Serialize(f.Project).Value);
        var rational = new FrameRate(30000, 1001);
        Assert.AreEqual(TimelineSnapping.QuantizeToFrame(expected, rational), coordinates.PlacementTicks(312.5m, rational, false, []));
        Assert.AreEqual(expected, coordinates.PlacementTicks(313m, new(30, 1), true, [expected]));
    }

    [TestMethod]
    public void FirstImportCreatesOnlyProjectAndMediaInOneUndoableBatch()
    {
        var session = new EditorSession();
        var asset = new Fixture().Project.Assets[0];
        Assert.AreEqual(EditorGuidance.ImportMedia, EditorStartup.Guidance(null, null));
        var result = session.Execute(EditorStartup.Import(session.GetProject(), asset, Guid.NewGuid(), "test"));
        Assert.IsTrue(result.Success);
        Assert.IsTrue(session.GetProject().Project!.Sequences.IsEmpty);
        Assert.AreEqual(EditorGuidance.CreateSequence, EditorStartup.Guidance(session.GetProject().Project, null));
        string json = ProjectJson.Serialize(session.GetProject().Project!).Value!;
        Assert.IsTrue(ProjectJson.Deserialize(json).Success);
        Assert.IsTrue(session.Undo().Success); Assert.IsNull(session.GetProject().Project);
        Assert.IsTrue(session.Redo().Success); Assert.AreEqual(json, ProjectJson.Serialize(session.GetProject().Project!).Value);
    }

    [TestMethod]
    public void ImportIntoExistingProjectKeepsIdentitySequencesAndRevisionGuard()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        var asset = f.Project.Assets[0] with { Id = Guid.NewGuid(), Name = "second" };
        var batch = EditorStartup.Import(before, asset, Guid.NewGuid(), "ignored");
        Assert.IsTrue(f.Session.Execute(batch).Success);
        Assert.AreEqual(before.Project!.Id, f.Project.Id);
        CollectionAssert.AreEqual(before.Project.Sequences.ToArray(), f.Project.Sequences.ToArray());
        Assert.IsFalse(f.Session.Execute(batch).Success);
        Assert.AreEqual(EditorGuidance.Edit, EditorStartup.Guidance(f.Project, f.Project.Sequences[0]));
    }
}
