using Flamoris.Mcp.Core;
using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpTests
{
    [TestMethod]
    public async Task RippleUiPlanAndMcpCommandShareHistoryAndNullableEndInsertion()
    {
        using var h = new McpCoreHarness();
        var f = h.Fixture; var b = Fixture.Id(20); var c = Fixture.Id(21);
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.ClipId, 3 * Fixture.T, b),
            new SplitClip(f.SequenceId, b, 5 * Fixture.T, c)).Success);
        using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var original = NativeProjectCodec.Serialize(f.Project).Value;
        var snapshot = f.Session.GetProject();
        var plan = TimelineEditPlanner.Reorder(f.Project, f.SequenceId, c, b, snapshot.Revision).Value!;
        Assert.IsTrue((await h.Human(() => f.Session.Execute(plan.Batch))).Success);
        Assert.IsFalse((await h.Call(grant, "undo", guard: h.Guard())).IsError);
        Assert.AreEqual(original, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsFalse((await h.Call(grant, "redo", guard: h.Guard())).IsError);
        CollectionAssert.AreEqual(new[] { f.ClipId, c, b }, TimelineQueries.ListClips(f.Project.Sequences[0].Tracks[0]).Select(x => x.Id).ToArray());
        Assert.IsTrue((await h.Human(() => f.Session.Undo())).Success);
        var result = await h.Call(grant, "edit_batch", new { commands = new[] {
            new { type = "RippleReorderClip", sequenceId = f.SequenceId, clipId = f.ClipId, beforeClipId = (Guid?)null } } }, h.Guard());
        Assert.IsFalse(result.IsError); Assert.IsTrue(result.Value!.Value.GetProperty("success").GetBoolean());
        CollectionAssert.AreEqual(new[] { b, c, f.ClipId }, TimelineQueries.ListClips(f.Project.Sequences[0].Tracks[0]).Select(x => x.Id).ToArray());
        Assert.IsTrue((await h.Human(() => f.Session.Undo())).Success);
        Assert.AreEqual(original, NativeProjectCodec.Serialize(f.Project).Value);
    }

    [TestMethod]
    public async Task CoreEditsTheSameSessionAndBothClientsShareHistory()
    {
        using var h = new McpCoreHarness();
        using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var guard = h.Guard();
        var result = await h.Call(grant, "edit_batch", h.Batch(), guard);
        Assert.IsFalse(result.IsError); Assert.IsTrue(result.Value!.Value.GetProperty("success").GetBoolean());
        Assert.IsFalse(h.Fixture.Project.Sequences[0].Tracks[0].Enabled); Assert.AreEqual(1, h.Changes);
        Assert.AreEqual(McpErrors.StaleRevision, (await h.Call(grant, "edit_batch", h.Batch(), guard)).Error);
        Assert.IsTrue((await h.Human(() => h.Fixture.Session.Undo())).Success);
        Assert.IsTrue(h.Fixture.Project.Sequences[0].Tracks[0].Enabled);
        Assert.IsFalse((await h.Call(grant, "redo", guard: h.Guard())).IsError);
        Assert.IsFalse(h.Fixture.Project.Sequences[0].Tracks[0].Enabled);
        Assert.IsTrue((await h.Human(() => h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.VideoTrackId, true)))).Success);
        Assert.IsFalse((await h.Call(grant, "undo", guard: h.Guard())).IsError);
        Assert.IsFalse(h.Fixture.Project.Sequences[0].Tracks[0].Enabled);
        var state = (await h.Call(grant, "get_project")).Value!.Value;
        Assert.AreEqual(h.Fixture.Session.GetProject().Revision.ToString(), state.GetProperty("revision").GetString());
        Assert.AreEqual(h.Fixture.SequenceId, state.GetProperty("context").GetProperty("sequenceId").GetGuid());
    }

    [TestMethod]
    public async Task DryRunAndFailedBatchLeaveHistoryAndRevisionUntouched()
    {
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var before = h.Fixture.Session.GetProject();
        var command = new { type = "SetTrackEnabled", sequenceId = h.Fixture.SequenceId, trackId = h.Fixture.VideoTrackId, enabled = false };
        var dry = await h.Call(grant, "edit_batch", new { commands = new[] { command }, dryRun = true }, h.Guard());
        Assert.IsTrue(dry.Value!.Value.GetProperty("success").GetBoolean());
        var failure = await h.Call(grant, "edit_batch", new { commands = new object[] { command,
            new { type = "DeleteClip", sequenceId = h.Fixture.SequenceId, clipId = Guid.NewGuid() } } }, h.Guard());
        Assert.IsFalse(failure.Value!.Value.GetProperty("success").GetBoolean());
        Assert.AreEqual(before, h.Fixture.Session.GetProject()); Assert.AreEqual(0, h.Changes);
    }

    [TestMethod]
    public async Task StaleIdentityMissingRevisionAndHumanGestureRejectBeforeCommit()
    {
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var before = h.Fixture.Session.GetProject();
        foreach (var (guard, error) in new[] {
            (h.Guard() with { RuntimeId = "old" }, McpErrors.StaleSession),
            (h.Guard() with { DocumentToken = "old" }, McpErrors.StaleDocument),
            (h.Guard() with { ExpectedRevision = null }, McpErrors.InvalidRequest) })
            Assert.AreEqual(error, (await h.Call(grant, "edit_batch", h.Batch(), guard)).Error);
        Assert.AreEqual(McpErrors.InvalidRequest, (await h.Call(grant, "undo")).Error);
        h.Busy = true;
        Assert.AreEqual(McpErrors.Busy, (await h.Call(grant, "edit_batch", h.Batch(), h.Guard())).Error);
        Assert.AreEqual(McpErrors.Busy, (await h.Call(grant, "get_project")).Error);
        Assert.AreEqual(before, h.Fixture.Session.GetProject());
    }
    [TestMethod]
    public async Task ReadOnlyRecipeValidationIsBoundedAndActivityAlwaysClears()
    {
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        var before = h.Fixture.Session.GetProject();
        var compiled = await h.Call(grant, "recipe_validate", new { source = "text(text='x')" });
        Assert.IsFalse(compiled.IsError);
        Assert.IsTrue(compiled.Value!.Value.GetProperty("success").GetBoolean());
        var rejected = await h.Call(grant, "recipe_validate", new { source = "import os" });
        Assert.IsFalse(rejected.Value!.Value.GetProperty("success").GetBoolean());
        Assert.AreEqual(McpErrors.InvalidRequest, (await h.Call(grant, "recipe_validate", new { source = new string('x', 65537) })).Error);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.AreEqual(McpErrors.Cancelled, (await h.Call(grant, "recipe_validate", new { source = "text(text='x')" }, token: cancelled.Token)).Error);
        Assert.AreEqual(before, h.Fixture.Session.GetProject());
        Assert.AreEqual(0, h.Boundary.Status.Current.ForegroundCount);
    }

}
