using Flamoris.Mcp.Core;
using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpTests
{
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
}
