using System.Text.Json;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpTests
{
    [TestMethod]
    public async Task McpEditsTheSameSessionAndRejectsStaleCommands()
    {
        var f = new Fixture(); int changes = 0;
        var adapter = new McpEditorAdapter(f.Session, () => new { }, () => changes++);
        await adapter.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
        string request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "edit_batch", arguments = new { expectedRevision = f.Session.GetProject().Revision.ToString(), commands = new[] { new { type = "SetTrackEnabled", sequenceId = f.SequenceId, trackId = f.VideoTrackId, enabled = false } } } } });
        using var result = JsonDocument.Parse((await adapter.HandleAsync(request))!);
        Assert.IsFalse(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.IsFalse(f.Project.Sequences[0].Tracks[0].Enabled); Assert.AreEqual(1, changes);
        using var stale = JsonDocument.Parse((await adapter.HandleAsync(request))!);
        Assert.IsTrue(stale.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.AreEqual(1, changes);
        Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.Project.Sequences[0].Tracks[0].Enabled);
    }
}
