using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class EffectLibraryTests
{
    private static EffectDefinition Effect() => new(1, Guid.NewGuid(), "Akino pan", "Reusable visual motion", 1, "1",
        [new(VisualProperty.X, [new(0, 0), new(1, 100)])], new());
    private static string Folder() { string path = Path.Combine(Path.GetTempPath(), "kachinco-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    [TestMethod]
    public async Task SerializationVersionConflictRenameAndDeleteAreBounded()
    {
        string root = Folder();
        try
        {
            var library = new EffectLibrary(root); var item = Effect();
            Assert.IsTrue((await library.SaveAsync(item)).Success);
            Assert.IsFalse((await library.SaveAsync(item)).Success);
            Assert.AreEqual(item.Name, library.Get(item.Id).Value!.Name);
            Assert.AreEqual(1, library.List().Value.Length);
            var renamed = await library.SaveAsync(item with { Name = "Akino soft pan" }, 1);
            Assert.IsTrue(renamed.Success); Assert.AreEqual(2, renamed.Value!.Version);
            Assert.IsFalse((await library.SaveAsync(item, 1)).Success);
            Assert.IsFalse(library.Delete(item.Id, 1).Success);
            Assert.IsTrue(library.Delete(item.Id, 2).Success); Assert.IsFalse(library.Get(item.Id).Success);
            Assert.IsFalse((await library.SaveAsync(item with { ApiVersion = "99" })).Success);
            Assert.IsFalse((await library.SaveAsync(item with { Curves = [new(VisualProperty.ScaleX, [new(0, -1)])] })).Success);
            Assert.IsFalse((await library.SaveAsync(item with { Curves = [new(VisualProperty.X, [new(0, 1), new(0, 2)])] })).Success);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void InvalidDuplicateUnknownAndOversizedItemsDoNotHideValidSiblings()
    {
        string root = Folder();
        try
        {
            var item = Effect(); var bytes = JsonSerializer.SerializeToUtf8Bytes(item, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            Assert.IsTrue(EffectLibrary.Decode(bytes).Success);
            string source = Encoding.UTF8.GetString(bytes);
            Assert.IsFalse(EffectLibrary.Decode(Encoding.UTF8.GetBytes(source.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"))).Success);
            Assert.IsFalse(EffectLibrary.Decode(Encoding.UTF8.GetBytes(source.Replace("\"schemaVersion\":1", "\"unknown\":1,\"schemaVersion\":1"))).Success);
            Assert.IsFalse(EffectLibrary.Decode(new byte[EffectLibrary.MaximumItemBytes + 1]).Success);
            File.WriteAllBytes(Path.Combine(root, item.Id.ToString("N") + ".effect.json"), bytes);
            File.WriteAllText(Path.Combine(root, Guid.NewGuid().ToString("N") + ".effect.json"), "{}");
            var list = new EffectLibrary(root).List(); Assert.IsFalse(list.Success); Assert.AreEqual(1, list.Value.Length);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task LinkAndMissingUnwritablePathsFailWithoutOverwritingOtherFiles()
    {
        if (OperatingSystem.IsWindows()) return; // Windows link privilege is separate; ordinary path/error cases run elsewhere.
        string root = Folder();
        try
        {
            var item = Effect(); string outside = Path.Combine(root, "keep.txt"); File.WriteAllText(outside, "keep");
            File.CreateSymbolicLink(Path.Combine(root, item.Id.ToString("N") + ".effect.json"), outside);
            Assert.IsFalse((await new EffectLibrary(root).SaveAsync(item)).Success);
            Assert.AreEqual("keep", File.ReadAllText(outside));
            Assert.IsFalse(new EffectLibrary(outside).List().Success);
            string missing = Path.Combine(root, "missing"); var library = new EffectLibrary(missing);
            Assert.IsTrue(library.List().Success); Assert.IsFalse(library.Get(item.Id).Success);
            Assert.IsTrue((await library.SaveAsync(item)).Success);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void PathPreferencesAreExecutableRelativeAndSwitchingDoesNotMoveAssets()
    {
        string root = Folder();
        try
        {
            var preferences = new EffectLibraryPreferences(Path.Combine(root, "preferences.json"));
            Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "library"), preferences.Load().Value);
            var saved = preferences.Save("my-effects"); Assert.IsTrue(saved.Success);
            Assert.AreEqual(Path.GetFullPath("my-effects", AppContext.BaseDirectory), preferences.Load().Value);
            Assert.IsTrue(preferences.Save(Path.Combine(root, "second")).Success);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "second")));
            File.WriteAllText(Path.Combine(root, "preferences.json"), "{\"effectLibraryPath\":\"x\",\"surprise\":true}");
            Assert.IsFalse(preferences.Load().Success);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void ParameterizedApplyCopiesProjectStateAndSharesUndoRedoAndDryRun()
    {
        var f = new Fixture(); var before = ProjectJson.Serialize(f.Project).Value!; var snapshot = f.Session.GetProject();
        var plan = EffectComposition.Plan(snapshot, f.SequenceId, f.ClipId, Effect(), new(.5, .5)); Assert.IsTrue(plan.Success);
        Assert.IsTrue(f.Session.Execute(plan.Value! with { DryRun = true }).Success); Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Execute(plan.Value!).Success);
        using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(25, evaluator.Evaluate(2 * Fixture.T).Value!.VideoLayers[0].Appearance.Transform.X);
        Assert.AreEqual(50, evaluator.Evaluate(4 * Fixture.T).Value!.VideoLayers[0].Appearance.Transform.X);
        var applied = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(applied, ProjectJson.Serialize(f.Project).Value);
        Assert.IsFalse(f.Session.Execute(plan.Value!).Success); // Captured expected revision is stale.
        Assert.IsFalse(EffectComposition.Plan(f.Session.GetProject(), f.SequenceId, f.AudioClipId, Effect()).Success);
    }
    [TestMethod]
    public void CaptureClippedAutomationUsesNativeVisibleEndpoints()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId, new(VisualProperty.X,
            [new(Guid.NewGuid(), -Fixture.T, 0), new(Guid.NewGuid(), 9 * Fixture.T, 100)]))).Success);
        var captured = EffectComposition.Capture(f.Session.GetProject(), f.SequenceId, f.ClipId, "pan"); Assert.IsTrue(captured.Success);
        Assert.AreEqual(10, captured.Value!.Curves[0].Samples[0].Value);
        Assert.AreEqual(90, captured.Value.Curves[0].Samples[^1].Value, 1e-5);
        Assert.AreEqual(0, captured.Value.Curves[0].Samples[0].Time); Assert.AreEqual(1, captured.Value.Curves[0].Samples[^1].Time);
    }
    [TestMethod]
    public async Task StoredRecipesRemainRestrictedAndCanBindToOrdinaryAuthoring()
    {
        string root = Folder();
        try
        {
            var library = new EffectLibrary(root);
            var item = new EffectDefinition(1, Guid.NewGuid(), "particles", "", 1, "1", [], new(), "particles(count=2, x=0, y=0, size=4)", 7);
            Assert.IsTrue((await library.SaveAsync(item)).Success);
            Assert.IsFalse((await library.SaveAsync(item with { Id = Guid.NewGuid(), RecipeSource = "import os\nos.system('echo unsafe')" })).Success);
            var f = new Fixture(); var clapper = Fixture.Id(130);
            Assert.IsTrue(f.Edit(new AddClapper(f.SequenceId, new(clapper, "region", 0, Fixture.T, null, f.VideoTrackId, null, ""))).Success);
            var plan = EffectComposition.PlanRecipe(f.Session.GetProject(), f.SequenceId, clapper, item); Assert.IsTrue(plan.Success);
            Assert.IsTrue(f.Session.Execute(plan.Value!).Success);
            Assert.AreEqual(item.RecipeSource, f.Project.Sequences[0].Recipes.Single().Source);
            Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.Project.Sequences[0].Recipes.IsEmpty);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task ExplicitLibraryToolsRespectReadOnlyGrantRevisionAndSharedHistory()
    {
        string root = Folder();
        try
        {
            var library = new EffectLibrary(root); var item = Effect(); Assert.IsTrue((await library.SaveAsync(item)).Success);
            var f = new Fixture(); using var host = new KachincoMcpHost(f.Session, () => false, (action, token) => { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; });
            using var boundary = new McpBoundary(host, EffectLibraryMcpTools.Create(f.Session, library, () => { }), new(), new McpDiagnostics(KachincoLogging.Create().Logger));
            var apply = JsonSerializer.SerializeToElement(new { id = item.Id, sequenceId = f.SequenceId, clipId = f.ClipId });
            RequestGuard Guard() => new(host.Snapshot.RuntimeId, host.Snapshot.DocumentToken, host.Snapshot.Revision);
            using var read = await boundary.EnableAsync(McpPermission.ReadOnly);
            Assert.AreEqual(McpErrors.Forbidden, (await boundary.InvokeAsync(read, "effect_apply", apply, Guard())).Error);
            Assert.IsFalse((await boundary.InvokeAsync(read, "effect_get", JsonSerializer.SerializeToElement(new { id = item.Id }), null)).IsError);
            using var edit = await boundary.EnableAsync(McpPermission.Edit); var stale = Guard();
            Assert.IsFalse((await boundary.InvokeAsync(edit, "effect_apply", apply, Guard())).IsError);
            Assert.AreEqual(1, f.VideoClip.Appearance.Automation.Length);
            Assert.AreEqual(McpErrors.StaleRevision, (await boundary.InvokeAsync(edit, "effect_apply", apply, stale)).Error);
            Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.VideoClip.Appearance.Automation.IsEmpty);
            var deletion = JsonSerializer.SerializeToElement(new { id = item.Id, expectedVersion = 1 });
            Assert.IsFalse((await boundary.InvokeAsync(edit, "effect_delete", deletion, Guard())).IsError);
            Assert.IsFalse(library.Get(item.Id).Success);
        }
        finally { Directory.Delete(root, true); }
    }
}
