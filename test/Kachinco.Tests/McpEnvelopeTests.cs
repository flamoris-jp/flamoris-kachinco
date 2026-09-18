using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpEnvelopeTests
{
    [TestMethod]
    public async Task InvalidEnvelopesAreBoundedInNormalAndBusyPaths()
    {
        string[] inputs = ["[]", "null", "1", "\"x\"", "{", "{}",
            "{\"jsonrpc\":2,\"method\":\"ping\",\"id\":1}",
            "{\"jsonrpc\":\"2.0\",\"method\":null,\"id\":1}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":null}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":[]}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1.5}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1,\"id\":2}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"id\":1,\"params\":null}"];
        var f = new Fixture(); var before = f.Session.GetProject();
        var adapter = new McpEditorAdapter(f.Session, () => new { }, () => Assert.Fail(), new(McpPermission.ReadOnly));
        foreach (bool busy in new[] { false, true })
            foreach (string input in inputs)
            {
                using var response = JsonDocument.Parse((await adapter.HandleAsync(input, busy: busy))!);
                Assert.IsTrue(response.RootElement.TryGetProperty("error", out _), input);
            }
        Assert.AreEqual(before, f.Session.GetProject());
        using var ping = JsonDocument.Parse((await adapter.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}"))!);
        Assert.IsTrue(ping.RootElement.TryGetProperty("result", out _));
    }
}
