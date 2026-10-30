using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class GlyphTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "glyph-test-" + Guid.NewGuid().ToString("N"));
        public string Manifest => Path.Combine(Root, "manifest.json");
        public Fixture()
        {
            string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "glyph-demo");
            Directory.CreateDirectory(Path.Combine(Root, "glyphs"));
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(path, Path.Combine(Root, Path.GetRelativePath(source, path)));
        }
        public JsonObject Data => JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
        public void Write(JsonObject value) => File.WriteAllText(Manifest, value.ToJsonString(), new UTF8Encoding(false));
        public JsonObject Import() => GlyphBundleImporter.Read(Manifest, KineticComposition.New(320, 180));
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static Project ProjectWith(JsonObject document)
    {
        long duration = KineticComposition.Duration(document);
        var asset = new MediaAsset(Guid.NewGuid(), "Glyph", "absent.ktproj", MediaKind.KineticText, duration) { TextComposition = document.ToJsonString() };
        var clip = new Clip(Guid.NewGuid(), asset.Id, 0, 0, duration, true, ClipAppearance.Default, AudioProperties.Default);
        return new(Guid.NewGuid(), "Glyph", [asset], [new(Guid.NewGuid(), "Main", SequenceSettings.Landscape, duration,
            [new(Guid.NewGuid(), "Glyph", TrackKind.Video, true, [clip], [])])]);
    }
    [TestMethod]
    public void ActualGlyphExportImportsAllOccurrencesAndPreservesCueTimesAndColor()
    {
        using var fixture = new Fixture(); var document = fixture.Import();
        Assert.AreEqual(2, document["version"]!.GetValue<int>());
        Assert.AreEqual(3, document["clips"]!.AsArray().Count);
        Assert.AreEqual(TimelineTime.TicksPerSecond, KineticComposition.Tick(document["clips"]![0]!, "startTicks"));
        Assert.AreEqual(3, document["clips"]![0]!["glyphs"]!["units"]!.AsArray().Count);
        var first = document["clips"]![0]!["glyphs"]!["units"]![0]!;
        var repeated = document["clips"]![1]!["glyphs"]!["units"]![0]!;
        Assert.AreEqual("赤", first["character"]!.GetValue<string>());
        Assert.IsFalse(JsonNode.DeepEquals(first["paths"], repeated["paths"]));
        Assert.IsTrue(first["paths"]![0]!["commands"]![0]![0]!.GetValue<string>() == "M");
        Assert.IsFalse(document.ToJsonString().Contains(".svg", StringComparison.Ordinal));
        Assert.IsFalse(document.ToJsonString().Contains(fixture.Root, StringComparison.Ordinal));
    }
    [TestMethod]
    public void GeometryIsSelfContainedAndSurvivesNativeHistoryAndHostReopen()
    {
        JsonObject document;
        using (var fixture = new Fixture()) document = fixture.Import(); // Original bundle is now deleted.
        using var text = new NativeKineticDocument();
        Assert.IsTrue(text.Edit(document, 0, true)["success"]!.GetValue<bool>());
        var changed = (JsonObject)document.DeepClone(); changed["clips"]![0]!["fontSize"] = 90;
        Assert.IsTrue(text.Edit(changed, 1)["success"]!.GetValue<bool>());
        Assert.IsTrue(JsonNode.DeepEquals(document, text.Request(new() { ["action"] = "undo", ["expectedRevision"] = 2 })["document"]));
        Assert.IsTrue(JsonNode.DeepEquals(changed, text.Request(new() { ["action"] = "redo", ["expectedRevision"] = 3 })["document"]));
        var project = ProjectWith(document); string encoded = ProjectJson.Serialize(project).Value!;
        using var host = new EditorSession(); var loaded = host.ReplaceProject(project);
        Assert.IsTrue(loaded.Success, string.Join("; ", loaded.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.IsTrue(host.Execute(new([new SetTextComposition(project.Assets[0].Id, changed.ToJsonString())], host.GetProject().Revision)).Success);
        Assert.IsTrue(host.Undo().Success); Assert.AreEqual(document.ToJsonString(), host.GetProject().Project!.Assets[0].TextComposition);
        Assert.IsTrue(host.Redo().Success); Assert.AreEqual(changed.ToJsonString(), host.GetProject().Project!.Assets[0].TextComposition);
        var restored = ProjectJson.Deserialize(encoded); Assert.IsTrue(restored.Success);
        Assert.IsTrue(JsonNode.DeepEquals(document, KineticComposition.Parse(restored.Value!.Assets[0].TextComposition!)));
        using var renderer = new NativeKineticRenderer(OperatingSystem.IsWindows());
        var pixels = renderer.Render(document.ToJsonString(), TimelineTime.SecondsToTicks(1.5m), 320, 180);
        Assert.IsTrue(pixels.Rgba.Where((_, i) => i % 4 == 3).Any(v => v > 0));
        Assert.AreEqual(OperatingSystem.IsWindows() ? "D3D11-WARP" : "Pango-Cairo-CPU", renderer.Info["backend"]!.GetValue<string>());
    }
    [TestMethod]
    public void MalformedTamperedAndEscapingBundlesFailBeforeCommit()
    {
        foreach (var kind in new[] { "schema", "path", "hash", "occurrence", "stroke", "palette", "missing" })
        {
            using var fixture = new Fixture(); var data = fixture.Data;
            switch (kind)
            {
                case "schema": data["schema_version"] = 99; break;
                case "path": data["glyphs"]![0]!["asset"] = "../secret.svg"; break;
                case "hash": data["glyphs"]![0]!["sha256"] = new string('0', 64); break;
                case "occurrence": data["occurrences"]![0]!["character"] = "靴"; break;
                case "stroke": data["glyphs"]![0]!["strokes"]![0]!["element_id"] = "wrong"; break;
                case "palette": data["style"]!["palette"] = new JsonArray("#000001"); break;
                case "missing": data.Remove("cues"); break;
            }
            fixture.Write(data); Assert.ThrowsExactly<InvalidDataException>(() => fixture.Import(), kind);
        }
    }
    [TestMethod]
    public void ActiveExternalAndOpenSvgAreRejectedEvenWithMatchingHashes()
    {
        foreach (string content in new[] { "<script/>", "<image href='file:///secret'/>", "<path id='stroke001' fill='#C72F42' d='M10 10L20 20'/>" })
        {
            using var fixture = new Fixture(); var data = fixture.Data; var entry = data["glyphs"]![0]!;
            byte[] svg = Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000'>{content}</svg>");
            File.WriteAllBytes(Path.Combine(fixture.Root, entry["asset"]!.GetValue<string>()), svg);
            entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(svg)); fixture.Write(data);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Import());
        }
    }
    [TestMethod]
    public void UntimedCuesNeedAnExplicitDurationAndEditedTextCannotRetainOldShapes()
    {
        using var fixture = new Fixture(); var data = fixture.Data;
        foreach (var cue in data["cues"]!.AsArray()) { cue!["start_ms"] = null; cue["end_ms"] = null; }
        fixture.Write(data); Assert.ThrowsExactly<InvalidDataException>(() => fixture.Import());
        var document = GlyphBundleImporter.Read(fixture.Manifest, KineticComposition.New(), TimelineTime.TicksPerSecond);
        document["clips"]![0]!["text"] = "別の文字";
        Assert.ThrowsExactly<InvalidDataException>(() => KineticComposition.Validate(document));
    }
    [TestMethod]
    public void SymlinkAssetsCannotBeImportedFromOutsideTheBundle()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(); var data = fixture.Data;
        string path = Path.Combine(fixture.Root, data["glyphs"]![0]!["asset"]!.GetValue<string>());
        string outside = Path.Combine(Path.GetTempPath(), "outside-glyph-" + Guid.NewGuid().ToString("N") + ".svg");
        try { File.Move(path, outside); File.CreateSymbolicLink(path, outside); Assert.ThrowsExactly<InvalidDataException>(() => fixture.Import()); }
        finally { File.Delete(outside); }
    }
    [TestMethod]
    public void NumericSvgCommandsNormalizeForBothNativeBackends()
    {
        using var fixture = new Fixture(); var data = fixture.Data; var entry = data["glyphs"]![0]!;
        string path = "M200 200h200v100q100 100 200 0c20 0 20 50 0 50L200 600Z";
        byte[] svg = Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 1000'><path id='stroke001' fill='#C72F42' d='{path}'/></svg>");
        File.WriteAllBytes(Path.Combine(fixture.Root, entry["asset"]!.GetValue<string>()), svg);
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(svg)); entry["strokes"] = new JsonArray(new JsonObject { ["element_id"] = "stroke001" }); fixture.Write(data);
        var document = fixture.Import(); var commands = document["clips"]![0]!["glyphs"]!["units"]![0]!["paths"]![0]!["commands"]!.AsArray();
        CollectionAssert.AreEqual(new[] { "M", "L", "L", "C", "C", "L", "Z" }, commands.Select(c => c![0]!.GetValue<string>()).ToArray());
    }
    [TestMethod]
    public async Task LinuxSharedTimelineDrawsEmbeddedGlyphsThroughOrdinaryRenderer()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture(); var project = ProjectWith(fixture.Import());
        var evaluation = TimelineEvaluator.Create(project, project.Sequences[0].Id);
        Assert.IsTrue(evaluation.Success, string.Join("; ", evaluation.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        using var evaluator = evaluation.Value!;
        var evaluated = evaluator.Evaluate(TimelineTime.SecondsToTicks(1.5m)).Value! with { Settings = new(320, 180, new(30, 1)) };
        using var decoder = new FfmpegMediaDecoder();
        var rendered = await new SharedFrameRenderer(decoder).RenderAsync(project, evaluated, 45, default);
        Assert.IsTrue(rendered.Success, string.Join("; ", rendered.Diagnostics.Select(d => d.Message)));
        Assert.IsTrue(rendered.Value!.Rgba8.Where((_, i) => i % 4 != 3).Any(v => v > 0));
    }
}
