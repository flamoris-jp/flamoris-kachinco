using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class VisualAutomationTests
{
    private static PropertyCurve Ramp(VisualProperty property = VisualProperty.X) => new(property,
        [new(Fixture.Id(100), 0, 0), new(Fixture.Id(101), 8 * Fixture.T, 80)]);
    [TestMethod]
    public void V4IsConditionalAndRetainsSignedExactTicksAndStrictFields()
    {
        var f = new Fixture(); string old = ProjectJson.Serialize(f.Project).Value!;
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.X,
            [new(Fixture.Id(100), long.MinValue, -1), new(Fixture.Id(101), 9_007_199_254_740_993, 1)]))).Success);
        string json = ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(4, JsonNode.Parse(json)!["schemaVersion"]!.GetValue<int>());
        Assert.IsTrue(json.Contains("9007199254740993"));
        var reopened = ProjectJson.Deserialize(json); Assert.IsTrue(reopened.Success);
        Assert.AreEqual(json, ProjectJson.Serialize(reopened.Value!).Value);
        foreach (var bad in new[] { json.Replace("\"tick\": \"9007199254740993\"", "\"tick\": 9007199254740993"),
            json.Replace("\"property\": \"X\"", "\"property\": \"X,Y\""), json.Replace("\"schemaVersion\": 4", "\"schemaVersion\": 3"),
            json.Replace("\"automation\": [", "\"surprise\": [], \"automation\": [") })
        { Assert.AreNotEqual(json, bad); Assert.IsFalse(ProjectJson.Deserialize(bad).Success); }
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.X, []))).Success);
        Assert.AreEqual(old, ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void CommandsShareHistoryAndPreserveCurvesAcrossConstantEdits()
    {
        var f = new Fixture(); var baseline = f.Session.GetProject();
        var curve = Ramp();
        Assert.IsTrue(f.Session.Execute(new([new SetClipPropertyCurve(f.SequenceId, f.ClipId, curve)], baseline.Revision, true)).Success);
        Assert.AreEqual(baseline.Revision, f.Session.GetProject().Revision);
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, curve)).Success);
        string before = ProjectJson.Serialize(f.Project).Value!;
        Assert.IsFalse(f.Edit(new UpdateClipPropertyPoint(f.SequenceId, f.ClipId, VisualProperty.X, curve.Points[0] with { Tick = curve.Points[1].Tick })).Success);
        Assert.IsFalse(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.AudioClipId, curve)).Success);
        Assert.IsFalse(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.ScaleX, [new(Guid.NewGuid(), 0, 0)]))).Success);
        Assert.IsFalse(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.Opacity, [new(Guid.NewGuid(), 0, 2)]))).Success);
        Assert.IsFalse(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.Y, default))).Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, f.ClipId, true, ClipAppearance.Default with { Opacity = .5 }, AudioProperties.Default)).Success);
        CollectionAssert.AreEqual(curve.Points.ToArray(), f.VideoClip.Appearance.Automation[0].Points.ToArray());
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(.5, f.VideoClip.Appearance.Opacity);
    }
    [TestMethod]
    public void NativeEvaluationSurvivesSplitTrimAndOverflowRollback()
    {
        var f = new Fixture(); Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, Ramp())).Success);
        using var original = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(40, original.Evaluate(4 * Fixture.T).Value!.VideoLayers[0].Appearance.Transform.X);
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.ClipId, 3 * Fixture.T, Fixture.Id(110))).Success);
        using var split = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        foreach (long tick in new[] { 0, Fixture.T, 3 * Fixture.T, 7 * Fixture.T })
            Assert.AreEqual(original.Evaluate(tick).Value!.VideoLayers[0].Appearance.Transform.X, split.Evaluate(tick).Value!.VideoLayers[0].Appearance.Transform.X);
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.ClipId, Fixture.T, 2 * Fixture.T, 7 * Fixture.T)).Success);
        using var trimmed = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(original.Evaluate(4 * Fixture.T).Value!.VideoLayers[0].Appearance.Transform.X, trimmed.Evaluate(4 * Fixture.T).Value!.VideoLayers[0].Appearance.Transform.X);
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.X,
            [new(Fixture.Id(100), long.MinValue, 0), new(Fixture.Id(101), long.MaxValue, 1)]))).Success);
        string before = ProjectJson.Serialize(f.Project).Value!;
        Assert.IsFalse(f.Edit(new SplitClip(f.SequenceId, f.ClipId, 2 * Fixture.T, Fixture.Id(111))).Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public async Task McpPropertyCommandsUseDecimalTicksAndSharedUndo()
    {
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var f = h.Fixture;
        var response = await h.Call(grant, "edit_batch", new { commands = new[] { new { type = "SetClipPropertyCurve", sequenceId = f.SequenceId, clipId = f.ClipId,
            curve = new { property = "X", points = new[] { new { id = Fixture.Id(100), tick = "0", value = 100.0 } } } } } }, h.Guard());
        Assert.IsFalse(response.IsError); Assert.AreEqual(100, f.VideoClip.Appearance.Automation[0].Points[0].Value);
        Assert.IsFalse((await h.Call(grant, "undo", guard: h.Guard())).IsError);
        Assert.IsTrue(f.VideoClip.Appearance.Automation.IsEmpty);
        Assert.AreEqual(McpErrors.InvalidRequest, (await h.Call(grant, "edit_batch", new { commands = new[] { new { type = "SetClipPropertyCurve", sequenceId = f.SequenceId, clipId = f.ClipId,
            curve = new { property = "X", points = new[] { new { id = Fixture.Id(100), tick = 0, value = 1.0 } } } } } }, h.Guard())).Error);
    }
}
