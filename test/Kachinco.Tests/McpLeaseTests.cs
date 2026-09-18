using System.Collections.Immutable;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpLeaseTests
{
    internal const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"tests","version":"1"}}}""";
    internal static string Call(string name, object args) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name, arguments = args } });

    [TestMethod]
    public async Task RevokedPreparedWorkCannotCommitUnderNewLease()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        using var oldLease = new McpAccessLease(McpPermission.Edit);
        var prepared = new TaskCompletionSource<PreparedGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () =>
        {
            var work = new PreparedGeneration("not-published.mov", new([new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false)], before.Revision), Guid.NewGuid(), Guid.NewGuid());
            prepared.SetResult(work);
            await resume.Task;
            oldLease.Commit(f.Session, work.Batch);
        });
        await prepared.Task;
        oldLease.Revoke();
        using var freshLease = new McpAccessLease(McpPermission.Edit);
        resume.SetResult();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await worker);
        Assert.AreEqual(before, f.Session.GetProject());
        Assert.IsTrue(freshLease.Commit(f.Session, new([new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false)], before.Revision)).Success);
        Assert.IsTrue(f.Session.Undo().Success);
    }

    [TestMethod]
    public async Task PermissionsRejectDirectCallsAndFileCommandsWithoutMutating()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        foreach (var permission in new[] { McpPermission.ReadOnly, McpPermission.Edit })
        {
            using var lease = new McpAccessLease(permission);
            var adapter = new McpEditorAdapter(f.Session, () => new { }, () => Assert.Fail(), lease);
            await adapter.HandleAsync(Initialize);
            string[] denied = [Call("export_start", new { expectedRevision = "1", sequenceId = f.SequenceId, outputPath = "denied.mp4" }),
                Call("recipe_generate", new { }), Call("job_status", new { jobId = Guid.NewGuid() }),
                Call("edit_batch", new { expectedRevision = before.Revision.ToString(), commands = new object[] { new { type = "RelinkMedia", mediaAssetId = Guid.NewGuid(), sourcePath = "secret.mov", durationTicks = "1" } } }),
                Call("edit_batch", new { expectedRevision = before.Revision.ToString(), commands = new object[] { new { type = "CreateProject", projectId = Guid.NewGuid(), name = "unauthorized" } } })];
            foreach (var request in denied)
            {
                using var response = JsonDocument.Parse((await adapter.HandleAsync(request))!);
                Assert.AreEqual(-32001, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
            if (permission == McpPermission.ReadOnly)
            {
                using var response = JsonDocument.Parse((await adapter.HandleAsync(Call("undo", new { expectedRevision = before.Revision.ToString() })))!);
                Assert.AreEqual(-32001, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
            lease.Revoke();
            using var revoked = JsonDocument.Parse((await adapter.HandleAsync(Call("get_project", new { })))!);
            Assert.IsTrue(revoked.RootElement.TryGetProperty("error", out _));
        }
        Assert.AreEqual(before, f.Session.GetProject());
    }

    [TestMethod]
    public void CancelledCommitAndHistoryNeverChangeTheSession()
    {
        var f = new Fixture(); var before = f.Session.GetProject(); using var lease = new McpAccessLease(McpPermission.Edit);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => lease.Commit(f.Session,
            new([new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false)], before.Revision), cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => f.Session.Undo(before.Revision, cancellation.Token));
        Assert.AreEqual(before, f.Session.GetProject());
    }
}
