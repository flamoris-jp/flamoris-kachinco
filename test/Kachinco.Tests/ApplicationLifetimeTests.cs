using Flamoris.Mcp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kachinco.Tests;
[TestClass]
public sealed class ApplicationLifetimeTests
{
    [TestMethod]
    public async Task OpenCloseAndSameIdReplacementKeepTheGrantAndRejectOldGuards()
    {
        using var h = new McpCoreHarness(new() { ApplicationLifetime = true });
        using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        string credential = grant.ExportCredential(); var old = h.Guard(); var project = h.Fixture.Project;
        await h.Human(() => h.Fixture.Session.ReplaceProject(project));
        Assert.IsTrue(grant.Authenticate(credential));
        Assert.AreEqual(McpErrors.StaleDocument, (await h.Call(grant, "edit_batch", h.Batch(), old)).Error);
        await h.Human(() => h.Fixture.Session.ReplaceProject(null));
        Assert.IsTrue(grant.IsActive);
        Assert.AreEqual(McpErrors.NoDocument, (await h.Call(grant, "get_project")).Error);
        await h.Human(() => h.Fixture.Session.ReplaceProject(project));
        Assert.IsFalse((await h.Call(grant, "get_project")).IsError);
        h.Host.Shutdown(); Assert.IsFalse(grant.IsActive);
    }
}
