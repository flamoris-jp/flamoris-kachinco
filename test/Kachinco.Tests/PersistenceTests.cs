using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PersistenceTests
{
    [TestMethod]
    public void HeadlessMovWavEditSaveOpenProofPreservesTimelineAndIdentity()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.ClipId, 3*Fixture.T, Fixture.Id(11)),
            new TrimClip(f.SequenceId, f.AudioClipId, 2*Fixture.T, Fixture.T, 4*Fixture.T),
            new SetClipProperties(f.SequenceId, Fixture.Id(11), true,
                new(new(15, 22, 2, 1, 45), 0.75, BlendMode.Screen), new(0.5, false))).Success);
        string json = ProjectJson.Serialize(f.Project).Value!;
        var loaded = ProjectJson.Deserialize(json);
        Assert.IsTrue(loaded.Success, string.Join(";", loaded.Diagnostics));
        Assert.AreEqual(json, ProjectJson.Serialize(loaded.Value!).Value);
        Assert.AreEqual("存在薄明\nBefore the first", loaded.Value!.Sequences[0].Tracks[2].Captions[0].Text);
        var before = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        var after = TimelineEvaluator.Create(loaded.Value, f.SequenceId).Value!;
        foreach (long tick in new[] { 0, Fixture.T, 3*Fixture.T, 8*Fixture.T - 1 })
        {
            var a = before.Evaluate(tick).Value!; var b = after.Evaluate(tick).Value!;
            CollectionAssert.AreEqual(a.VideoLayers.ToArray(), b.VideoLayers.ToArray());
            CollectionAssert.AreEqual(a.Audio.ToArray(), b.Audio.ToArray());
            CollectionAssert.AreEqual(a.Captions.ToArray(), b.Captions.ToArray());
        }
        var session = new EditorSession();
        Assert.IsTrue(session.ReplaceProject(loaded.Value).Success);
        Assert.IsFalse(session.GetProject().CanUndo);
    }
    [TestMethod]
    public void FutureSchemaDispatchPrecedesMalformedPayload()
    {
        var result = ProjectJson.Deserialize("{\"schemaVersion\":99,\"project\":null}");
        Assert.IsFalse(result.Success); Assert.AreEqual("SCHEMA_UNSUPPORTED", result.Diagnostics[0].Code);
    }
    [TestMethod]
    [DataRow("{}")] [DataRow("null")] [DataRow("[]")] [DataRow("{")]
    [DataRow("{\"schemaVersion\":\"1\"}")]
    public void MalformedEnvelopesAreStructured(string json) => Assert.IsFalse(ProjectJson.Deserialize(json).Success);

    [TestMethod]
    public void UnknownFieldsAndNullRequiredMembersCannotSilentlyDisappear()
    {
        var root = JsonNode.Parse(ProjectJson.Serialize(new Fixture().Project).Value!)!;
        root["project"]!["clappers"] = new JsonArray();
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
        root["project"]!.AsObject().Remove("clappers");
        root["project"]!["assets"] = null;
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
    }
    [TestMethod]
    public void MissingConstructorFieldsAndNumericEnumsAreRejected()
    {
        var root = JsonNode.Parse(ProjectJson.Serialize(new Fixture().Project).Value!)!;
        root["project"]!["sequences"]![0]!["tracks"]![0]!["enabled"] = null;
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
        root = JsonNode.Parse(ProjectJson.Serialize(new Fixture().Project).Value!)!;
        root["project"]!["assets"]![0]!.AsObject().Remove("durationTicks");
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
        root = JsonNode.Parse(ProjectJson.Serialize(new Fixture().Project).Value!)!;
        root["project"]!["assets"]![0]!["kind"] = 0;
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
    }
    [TestMethod]
    public void DuplicatePropertiesTimebaseAndDuplicateIdsFail()
    {
        var f = new Fixture(); var json = ProjectJson.Serialize(f.Project).Value!;
        Assert.IsFalse(ProjectJson.Deserialize(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")).Success);
        var root = JsonNode.Parse(json)!; root["timebase"] = "120000";
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
        root = JsonNode.Parse(json)!; root["project"]!["assets"]![0]!["id"] = f.ProjectId.ToString();
        Assert.IsFalse(ProjectJson.Deserialize(root.ToJsonString()).Success);
    }
    [TestMethod]
    public void TickStringsPreserveValuesBeyondJavaScriptIntegerPrecision()
    {
        var project = new Project(Fixture.Id(1), "long", [],
            [new(Fixture.Id(2), "long", SequenceSettings.Landscape, 9007199254740993L, [])]);
        string json = ProjectJson.Serialize(project).Value!;
        StringAssert.Contains(json, "\"9007199254740993\"");
        Assert.AreEqual(9007199254740993L, ProjectJson.Deserialize(json).Value!.Sequences[0].DurationTicks);
        Assert.IsFalse(ProjectJson.Deserialize(json.Replace("\"9007199254740993\"", "9007199254740993")).Success);
    }
    [TestMethod]
    public async Task FileRoundTripAndCancelledSavePreserveExistingFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "project.fkproj");
        try
        {
            var f = new Fixture(); var store = new ProjectFileStore();
            Assert.IsTrue((await store.SaveAsync(path, f.Project)).Success);
            string saved = await File.ReadAllTextAsync(path);
            Assert.IsTrue((await store.LoadAsync(path)).Success);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.IsFalse((await store.SaveAsync(path, f.Project with { Name = "changed" }, cancelled.Token)).Success);
            Assert.AreEqual(saved, await File.ReadAllTextAsync(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            Assert.IsFalse((await store.SaveAsync(path, f.Project with { Name = "" })).Success);
            Assert.AreEqual(saved, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [TestMethod]
    public async Task MissingFileAndInvalidUtf8AreReportedWithoutChangingSession()
    {
        var f = new Fixture(); var before = f.Session.GetProject(); var store = new ProjectFileStore();
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fkproj");
        Assert.IsFalse((await store.LoadAsync(path)).Success);
        try
        {
            await File.WriteAllBytesAsync(path, [0xff, 0xff]);
            Assert.AreEqual("INVALID_PROJECT_FILE", (await store.LoadAsync(path)).Diagnostics[0].Code);
        }
        finally { File.Delete(path); }
        Assert.AreEqual(before, f.Session.GetProject());
    }
    [TestMethod]
    public void OversizedJsonIsRejectedBeforeParsing() =>
        Assert.AreEqual("PROJECT_TOO_LARGE", ProjectJson.Deserialize(new string('x', ProjectJson.MaxFileBytes + 1)).Diagnostics[0].Code);
}
