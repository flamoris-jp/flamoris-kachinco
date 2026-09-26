using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpLeaseTests
{
    [TestMethod]
    [DataRow("cancel")]
    [DataRow("timeout")]
    [DataRow("revoke")]
    [DataRow("replace")]
    public async Task PreparedGenerationCannotCommitAfterTerminalBoundary(string reason)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        McpCoreHarness? fixture = null;
        var tool = new HostTool<int>("prepared", "Test-only prepared command barrier.", JsonSerializer.SerializeToElement(new { type = "object" }),
            OperationKind.Mutation, _ => 0, async (context, _, token) => {
                var f = fixture!.Fixture;
                var prepared = new PreparedGeneration("not-published.mov", new([new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false)], f.Session.GetProject().Revision), Guid.NewGuid(), Guid.NewGuid());
                entered.SetResult(); await resume.Task; // deliberately uncooperative preparation
                try { return await context.CommitAsync(() => JsonSerializer.SerializeToElement(f.Session.Execute(prepared.Batch, token))); }
                finally { exited.SetResult(); }
            });
        using var h = fixture = new(new McpOptions { RequestTimeoutMs = reason == "timeout" ? 500 : 5000 }, [tool]);
        using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        using var cancellation = new CancellationTokenSource();
        var before = h.Fixture.Session.GetProject();
        var call = h.Call(grant, "prepared", guard: h.Guard(), token: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reason == "cancel") cancellation.Cancel();
        if (reason == "revoke") h.Boundary.Disable();
        if (reason == "replace") await h.Human(() => h.Fixture.Session.ReplaceProject(h.Fixture.Project));
        var terminal = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(terminal.IsError);
        using var fresh = await h.Boundary.EnableAsync(McpPermission.Edit);
        var after = h.Fixture.Session.GetProject();
        resume.SetResult(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(after, h.Fixture.Session.GetProject());
        Assert.IsTrue(h.Fixture.Project.Sequences[0].Tracks[0].Enabled);
        Assert.AreEqual(0, h.Boundary.Status.Current.ForegroundCount);
        Assert.IsFalse((await h.Call(fresh, "edit_batch", h.Batch(), h.Guard())).IsError);
    }

    [TestMethod]
    public async Task PermissionsRejectFileCommandsAndUnregisteredJobs()
    {
        using var h = new McpCoreHarness(); var before = h.Fixture.Session.GetProject();
        foreach (var permission in new[] { McpPermission.ReadOnly, McpPermission.Edit })
        {
            using var grant = await h.Boundary.EnableAsync(permission);
            foreach (string name in new[] { "export_start", "recipe_generate", "job_status", "job_cancel", "file.read", "process.run", "eval" })
                Assert.AreEqual(McpErrors.UnsupportedCapability, (await h.Call(grant, name)).Error);
            foreach (string type in new[] { "CreateProject", "RegisterMedia", "RelinkMedia", "SetGeneratedProvenance" })
                Assert.AreEqual(McpErrors.Forbidden, (await h.Call(grant, "edit_batch", new { commands = new[] { new { type } } }, h.Guard())).Error);
            if (permission == McpPermission.ReadOnly)
                foreach (string name in new[] { "undo", "redo", "edit_batch" })
                    Assert.AreEqual(McpErrors.Forbidden, (await h.Call(grant, name, h.Batch(), h.Guard())).Error);
        }
        Assert.AreEqual(before, h.Fixture.Session.GetProject());
    }

    [TestMethod]
    [DataRow(McpPermission.Edit, false)]
    [DataRow(McpPermission.Edit, true)]
    [DataRow(McpPermission.ReadOnly, false)]
    public async Task DocumentLossRevokesAndHumanRedoCannotRevive(McpPermission permission, bool mcpUndo)
    {
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(permission);
        string credential = grant.ExportCredential(), oldToken = h.Fixture.Session.DocumentToken;
        if (mcpUndo) await h.Call(grant, "undo", guard: h.Guard());
        else await h.Human(() => h.Fixture.Session.Undo());
        // Revocation can finish the transport response inside the committing callback.
        // Observe domain state on the editor lane, as WPF does, after that callback.
        Assert.IsNull((await h.Human(() => h.Fixture.Session.GetProject())).Project);
        Assert.IsFalse(grant.IsActive); Assert.IsTrue(grant.Revoked.IsCancellationRequested);
        Assert.AreNotEqual(oldToken, h.Fixture.Session.DocumentToken);
        await h.Human(() => h.Fixture.Session.Redo());
        Assert.AreEqual(h.Fixture.ProjectId, h.Fixture.Project.Id);
        Assert.IsFalse(grant.Authenticate(credential));
        Assert.AreEqual(McpErrors.Unauthorized, (await h.Call(grant, "get_project")).Error);
        await h.Human(() => h.Fixture.Session.Undo());
        var asset = new MediaAsset(Guid.NewGuid(), "import", "import.mov", MediaKind.Mov, Fixture.T);
        await h.Human(() => h.Fixture.Session.Execute(EditorStartup.Import(h.Fixture.Session.GetProject(), asset, Guid.NewGuid(), "New import")));
        Assert.AreEqual(McpErrors.Unauthorized, (await h.Call(grant, "get_project")).Error);
        using var fresh = await h.Boundary.EnableAsync(permission);
        Assert.IsFalse((await h.Call(fresh, "get_project")).IsError);
    }

    [TestMethod]
    public async Task RotationSameIdReplacementStopAndShutdownRevoke()
    {
        using var h = new McpCoreHarness();
        using var a = await h.Boundary.EnableAsync(McpPermission.Edit);
        string secret = a.ExportCredential();
        Assert.IsFalse(a.Authenticate(null)); Assert.IsFalse(a.Authenticate(new string('0', 64)));
        Assert.IsTrue(a.Authenticate(secret)); Assert.IsFalse(a.ToString().Contains(secret));
        using var b = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        Assert.IsFalse(a.Authenticate(secret)); Assert.AreNotEqual(secret, b.ExportCredential());
        string token = h.Fixture.Session.DocumentToken;
        // Failed replacement leaves the valid attachment intact.
        Assert.IsFalse((await h.Human(() => h.Fixture.Session.ReplaceProject(h.Fixture.Project, -1))).Success);
        Assert.IsTrue(b.IsActive); Assert.AreEqual(token, h.Fixture.Session.DocumentToken);
        await h.Human(() => h.Fixture.Session.ReplaceProject(h.Fixture.Project));
        Assert.IsFalse(b.IsActive); Assert.AreNotEqual(token, h.Fixture.Session.DocumentToken);
        using var c = await h.Boundary.EnableAsync(McpPermission.Edit);
        h.Boundary.Disable(); Assert.IsFalse(c.IsActive);
        using var d = await h.Boundary.EnableAsync(McpPermission.Edit);
        h.Host.Shutdown(); Assert.IsFalse(d.IsActive);
        await Assert.ThrowsExactlyAsync<McpFault>(async () => await h.Boundary.EnableAsync(McpPermission.Edit));
    }
}
