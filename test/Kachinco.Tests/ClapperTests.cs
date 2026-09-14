using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class ClapperTests
{
    [TestMethod]
    public void ClapperResolvesAfterReopenAndProtectsSourceReferences()
    {
        var f = new Fixture(); var id = Fixture.Id(20);
        var clapper = new Clapper(id, "A-1", Fixture.T, Fixture.T, new(ClapperGeometryKind.Rectangle,10,20,300,200), f.VideoTrackId, f.ClipId, "グエー");
        Assert.IsTrue(f.Edit(new AddClapper(f.SequenceId, clapper)).Success);
        var reopened = ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!);
        Assert.IsTrue(reopened.Success);
        Assert.AreEqual(clapper, ClapperQueries.Resolve(reopened.Value!, f.SequenceId, "A-1").Value);
        var before = f.Session.GetProject();
        Assert.IsFalse(f.Edit(new DeleteClip(f.SequenceId, f.ClipId)).Success);
        Assert.AreEqual(before, f.Session.GetProject());
        Assert.IsFalse(f.Edit(new AddClapper(f.SequenceId, clapper with { Id = Fixture.Id(21) })).Success);
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.IsFalse(ClapperQueries.Resolve(f.Project, f.SequenceId, "A-1").Success);
    }
    [TestMethod]
    public void V1MigratesAndV2NeverSilentlyDropsAuthoring()
    {
        var f = new Fixture();
        var node = JsonNode.Parse(ProjectJson.Serialize(f.Project).Value!)!.AsObject();
        node["schemaVersion"] = 1; node.Remove("authoring"); node.Remove("generatedAssets");
        var v1 = ProjectJson.Deserialize(node.ToJsonString());
        Assert.IsTrue(v1.Success); Assert.IsTrue(v1.Value!.Sequences[0].Clappers.IsEmpty);
        node["schemaVersion"] = 2;
        Assert.IsFalse(ProjectJson.Deserialize(node.ToJsonString()).Success);
        node = JsonNode.Parse(ProjectJson.Serialize(v1.Value).Value!)!.AsObject();
        node["authoring"] = new JsonArray();
        Assert.IsFalse(ProjectJson.Deserialize(node.ToJsonString()).Success);
    }
}
