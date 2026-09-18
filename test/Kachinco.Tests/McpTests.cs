using System.Text.Json;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpTests
{
    [TestMethod]
    public async Task FramingRejectsOversizedInvalidUtf8AndTruncatedInputWithoutThrowing()
    {
        var oversized = new McpBoundedLineReader(new MemoryStream("123456789\n"u8.ToArray()), maximumBytes: 8, bufferSize: 4);
        Assert.AreEqual(McpFrameStatus.Oversized, (await oversized.ReadAsync()).Status);

        var invalid = new McpBoundedLineReader(new MemoryStream([0xff, (byte)'\n']), bufferSize: 2);
        Assert.AreEqual(McpFrameStatus.InvalidUtf8, (await invalid.ReadAsync()).Status);

        var truncated = new McpBoundedLineReader(new MemoryStream("{}"u8.ToArray()), bufferSize: 2);
        Assert.AreEqual(McpFrameStatus.Truncated, (await truncated.ReadAsync()).Status);
    }

    [TestMethod]
    public async Task FramingBuffersReadsAndPreservesTheNextLine()
    {
        var reader = new McpBoundedLineReader(new MemoryStream("one\r\ntwo\n"u8.ToArray()), bufferSize: 16);
        Assert.AreEqual("one", (await reader.ReadAsync()).Line);
        Assert.AreEqual("two", (await reader.ReadAsync()).Line);
        Assert.AreEqual(McpFrameStatus.EndOfStream, (await reader.ReadAsync()).Status);
    }

    [TestMethod]
    public async Task McpEditsTheSameSessionAndRejectsStaleCommands()
    {
        var f = new Fixture(); int changes = 0;
        var adapter = new McpEditorAdapter(f.Session, () => new { }, () => changes++, new(f.Session, McpPermission.Edit));
        await adapter.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}");
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
