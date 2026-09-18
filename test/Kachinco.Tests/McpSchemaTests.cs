using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpSchemaTests
{
    [TestMethod]
    public async Task EveryCommandHasAnExplicitDispositionAndTypedDiscoveryMatchesDecoder()
    {
        var commands = typeof(EditCommand).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(EditCommand))).Select(t => t.Name).Order().ToArray();
        CollectionAssert.AreEqual(commands, McpEditorAdapter.CommandDispositions.Keys.Order().ToArray());
        var f = new Fixture(); using var lease = new McpAccessLease(f.Session, McpPermission.Edit);
        var adapter = new McpEditorAdapter(f.Session, () => new { }, () => { }, lease);
        await adapter.HandleAsync(McpLeaseTests.Initialize);
        const string list = """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""";
        string first = (await adapter.HandleAsync(list))!;
        Assert.AreEqual(first, await adapter.HandleAsync(list));
        using var doc = JsonDocument.Parse(first);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        var schemas = tools.Single(t => t.GetProperty("name").GetString() == "edit_batch").GetProperty("inputSchema").GetProperty("properties").GetProperty("commands").GetProperty("items").GetProperty("oneOf").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(McpEditorAdapter.CommandDispositions.Where(p => p.Value).Select(p => p.Key).ToArray(), schemas.Select(s => s.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!).ToArray());
        foreach (var schema in schemas)
        {
            Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
            string name = schema.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!;
            using var missing = JsonDocument.Parse((await adapter.HandleAsync(McpLeaseTests.Call("edit_batch", new { expectedRevision = f.Session.GetProject().Revision.ToString(), commands = new[] { new { type = name } } })))!);
            Assert.IsTrue(missing.RootElement.TryGetProperty("error", out _), name);
        }
        Assert.IsFalse(tools.Any(t => t.GetProperty("name").GetString() is "export_start" or "recipe_generate"));
    }

    [TestMethod]
    public void ConstructorSchemaRejectsUnknownComputedPropertiesNullsAndIntegerTicks()
    {
        var id = Guid.NewGuid();
        foreach (var value in new object[] { new { sequenceId = id, durationTicks = 123 }, new { sequenceId = id, durationTicks = "1", surprise = true }, new { sequenceId = id, durationTicks = (string?)null } })
            Assert.ThrowsExactly<JsonException>(() => McpTypedSchema.Validate(typeof(SetSequenceDuration), JsonSerializer.SerializeToElement(value)));
        McpTypedSchema.Validate(typeof(SetSequenceDuration), JsonSerializer.SerializeToElement(new { sequenceId = id, durationTicks = "123" }));
        string schema = McpTypedSchema.Command(typeof(InsertClip)).ToJsonString();
        StringAssert.Contains(schema, "sourceInTicks"); StringAssert.Contains(schema, "rotationDegrees");
        StringAssert.Contains(schema, "Screen"); Assert.IsFalse(schema.Contains("endTicks"));
    }
}
