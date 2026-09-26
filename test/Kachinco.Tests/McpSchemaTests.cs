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
        CollectionAssert.AreEqual(commands, KachincoMcpTools.CommandDispositions.Keys.Order().ToArray());
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(Flamoris.Mcp.Core.McpPermission.Edit);
        var tools = h.Boundary.Tools;
        var schemas = tools["edit_batch"].InputSchema.GetProperty("properties").GetProperty("commands").GetProperty("items").GetProperty("oneOf").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(KachincoMcpTools.CommandDispositions.Where(p => p.Value).Select(p => p.Key).ToArray(), schemas.Select(s => s.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!).ToArray());
        foreach (var schema in schemas)
        {
            Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
            string name = schema.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!;
            var missing = await h.Call(grant, "edit_batch", new { commands = new[] { new { type = name } } }, h.Guard());
            Assert.AreEqual(Flamoris.Mcp.Core.McpErrors.InvalidRequest, missing.Error, name);
        }
        Assert.IsFalse(tools.ContainsKey("export_start") || tools.ContainsKey("recipe_generate"));
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
