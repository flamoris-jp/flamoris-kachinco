using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class KineticTests
{
    private static Project ProjectWith(MediaAsset asset)
    {
        var clip = new Clip(Guid.NewGuid(), asset.Id, 0, 0, asset.DurationTicks, true, ClipAppearance.Default, AudioProperties.Default);
        var track = new Track(Guid.NewGuid(), "Kinetic", TrackKind.Video, true, [clip], []);
        var sequence = new Sequence(Guid.NewGuid(), "Sequence", SequenceSettings.Landscape, asset.DurationTicks, [track]);
        return new(Guid.NewGuid(), "Project", [asset], [sequence]);
    }
    [TestMethod]
    public void NativeDocumentCommandsRejectStaleAndMalformedStateWithoutChangingHistory()
    {
        using var session = new NativeKineticDocument(); var document = KineticComposition.New();
        Assert.IsTrue(session.Edit(document, 0, true)["success"]!.GetValue<bool>());
        var changed = (JsonObject)document.DeepClone(); changed["clips"]![0]!["seed"] = 400;
        Assert.IsFalse(session.Edit(changed, 0)["success"]!.GetValue<bool>());
        Assert.IsTrue(session.Edit(changed, 1)["success"]!.GetValue<bool>());
        var undo = session.Request(new() { ["action"] = "undo", ["expectedRevision"] = 2 });
        Assert.IsTrue(JsonNode.DeepEquals(document, undo["document"]));
        var redo = session.Request(new() { ["action"] = "redo", ["expectedRevision"] = 3 });
        Assert.IsTrue(JsonNode.DeepEquals(changed, redo["document"]));
        var invalid = (JsonObject)changed.DeepClone(); invalid["clips"]![0]!["program"]!["version"] = 55;
        Assert.IsFalse(session.Edit(invalid, 4)["success"]!.GetValue<bool>());
        Assert.IsTrue(JsonNode.DeepEquals(changed, session.Get()["document"]));
        var excessive=(JsonObject)document.DeepClone();excessive["clips"]![0]!["program"]!["ops"]=new JsonArray(
            new JsonObject{["op"]="scale",["value"]=100},new JsonObject{["op"]="scale",["value"]=100});
        Assert.ThrowsExactly<InvalidDataException>(()=>KineticComposition.Validate(excessive));
        Assert.ThrowsExactly<InvalidDataException>(() => KineticComposition.Parse(document.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1")));
    }
    [TestMethod]
    public void EmbeddedTextUsesConditionalSchemaAndSurvivesUndoAndReopenWithoutDraftFile()
    {
        var document = KineticComposition.New();
        var asset = new MediaAsset(Guid.NewGuid(), "Text", "missing.ktproj", MediaKind.KineticText, KineticComposition.Duration(document)) { TextComposition = document.ToJsonString() };
        var project = ProjectWith(asset); using var session = new EditorSession(); var replacement = session.ReplaceProject(project); Assert.IsTrue(replacement.Success, string.Join(";", replacement.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        var encoded = ProjectJson.Serialize(project); Assert.IsTrue(encoded.Success); StringAssert.Contains(encoded.Value!, "\"schemaVersion\": 8");
        var decoded = ProjectJson.Deserialize(encoded.Value); Assert.IsTrue(decoded.Success); Assert.AreEqual(asset.TextComposition, decoded.Value!.Assets[0].TextComposition);
        Assert.IsTrue(MediaReferenceResolver.Inspect(decoded.Value, null)[0].IsAvailable);
        document["clips"]![0]!["text"] = "changed";
        Assert.IsTrue(session.Execute(new([new SetTextComposition(asset.Id, document.ToJsonString())], session.GetProject().Revision)).Success);
        Assert.IsTrue(session.Undo().Success); Assert.AreEqual(asset.TextComposition, session.GetProject().Project!.Assets[0].TextComposition);
        var plain = project with { Assets = [], Sequences = [] }; var legacy = ProjectJson.Serialize(plain); StringAssert.Contains(legacy.Value!, "\"schemaVersion\": 2"); Assert.IsFalse(legacy.Value!.Contains("textComposition"));
        Assert.IsFalse(ProjectJson.Deserialize(encoded.Value!.Replace("\"schemaVersion\": 8", "\"schemaVersion\": 9")).Success);
    }
    [TestMethod]
    public void ShortenedTextCannotSilentlyInvalidateHostClipsAndReturnIdentityIsBound()
    {
        var doc = KineticComposition.New();var asset = new MediaAsset(Guid.NewGuid(), "Text", "a.ktproj", MediaKind.KineticText, KineticComposition.Duration(doc)) { TextComposition = doc.ToJsonString() };
        using var session = new EditorSession(); var p = ProjectWith(asset); session.ReplaceProject(p);
        var request = KineticHandoff.Create(p.Id, session.GetProject().Revision, asset.Id, doc);
        var response = KineticHandoff.Response(request, doc); KineticHandoff.ValidateResponse(request, response);
        response["assetId"] = Guid.NewGuid().ToString(); Assert.ThrowsExactly<InvalidDataException>(() => KineticHandoff.ValidateResponse(request, response));
        doc["durationTicks"] = TimelineTime.TicksPerSecond.ToString(); doc["clips"]![0]!["durationTicks"] = TimelineTime.TicksPerSecond.ToString();
        Assert.IsFalse(session.Execute(new([new SetTextComposition(asset.Id, doc.ToJsonString())], session.GetProject().Revision)).Success);
        Assert.AreEqual(asset.TextComposition, session.GetProject().Project!.Assets[0].TextComposition);
    }
    [TestMethod]
    public void LyricsImportConvertsOnceRetainsProvenanceAndDoesNotNeedAnalyzer()
    {
        var srt = KineticComposition.ImportLyrics("1\n00:00:01,200 --> 00:00:02,300\n愛乃🐱\n", true, KineticComposition.New());
        Assert.AreEqual(TimelineTime.SecondsToTicks(1.2m), KineticComposition.Tick(srt["clips"]![0]!, "startTicks"));
        var ponpon = KineticComposition.ImportLyrics("{\"format\":\"ponpon_export\",\"lines\":[{\"id\":42,\"text\":\"緑の重力\",\"pressTime\":12.125,\"releaseTime\":13.25}]}", false, srt);
        Assert.AreEqual("ponpon:42", ponpon["clips"]![0]!["provenance"]!.GetValue<string>());
        Assert.AreEqual(TimelineTime.SecondsToTicks(13.25m), KineticComposition.Duration(ponpon));
        Assert.ThrowsExactly<InvalidDataException>(() => KineticComposition.ImportLyrics("[{\"text\":\"bad\",\"start\":4,\"end\":3}]", false, srt));
    }
    [TestMethod]
    public async Task RestrictedPythonCompilesToNativeIrAndRejectsExecutableSource()
    {
        var compiler = new KineticProgramCompiler(); var ir = await compiler.CompileAsync("stagger(delayTicks='1234800')\nflight(x=-300,y=0,durationTicks='17640000')\nshake(amplitude=8,frequency=12)");
        var doc = KineticComposition.New(); doc["clips"]![0]!["program"] = ir; KineticComposition.Validate(doc);
        foreach (string source in new[] { "import os", "__import__('os').system('x')", "shake(amplitude=1+2)", "while True: pass", "shake(**{})" })
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => compiler.CompileAsync(source));
    }
    [TestMethod]
    public async Task ScriptCompilationIsReadOnlyAndDoesNotChangeHistory()
    {
        using var h = new McpCoreHarness(additional: [KineticScriptMcpTools.Create()]);
        using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        var before = h.Fixture.Session.GetProject();
        var result = await h.Call(grant, "compile_script", new { source = KineticProgramCompiler.ExitExample });
        Assert.IsFalse(result.IsError, result.Error);
        Assert.AreEqual(2, result.Value!.Value.GetProperty("program").GetProperty("version").GetInt32());
        Assert.AreEqual(before, h.Fixture.Session.GetProject());
        Assert.AreEqual(McpErrors.InvalidRequest, (await h.Call(grant, "compile_script", new { source = "x", path = "file" })).Error);
    }
    [TestMethod]
    public void ScriptCompilationCatalogMatchesProducer()
    {
        var describe = typeof(McpBoundary).Assembly.GetType("Flamoris.Mcp.Core.McpProtocol")!
            .GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var tool = (ModelContextProtocol.Protocol.Tool)describe.Invoke(null, [KineticScriptMcpTools.Create()])!;
        var actual = JsonSerializer.SerializeToElement(new[] { new { name = tool.Name, description = tool.Description, inputSchema = tool.InputSchema } });
        if (Environment.GetEnvironmentVariable("KINETAI_EXPORT_SCRIPT_CATALOG") is { } export)
            File.WriteAllText(export, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kinetai-script-tools.json"));
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())));
    }
    [TestMethod]
    public async Task ProgrammableSourceAndIrSurviveHostUndoAndProjectReopen()
    {
        var source = KineticProgramCompiler.ExitExample;
        var ir = await new KineticProgramCompiler().CompileAsync(source);
        Assert.AreEqual(2, ir["version"]!.GetValue<int>());
        var doc = KineticComposition.New(); doc["clips"]![0]!["source"] = source;
        doc["clips"]![0]!["program"] = ir; KineticComposition.Validate(doc);
        var asset = new MediaAsset(Guid.NewGuid(), "Scripted", "absent.ktproj", MediaKind.KineticText,
            KineticComposition.Duration(doc)) { TextComposition = doc.ToJsonString() };
        using var session = new EditorSession(); Assert.IsTrue(session.ReplaceProject(ProjectWith(asset)).Success);
        var saved = ProjectJson.Serialize(session.GetProject().Project!); Assert.IsTrue(saved.Success);
        var reopened = ProjectJson.Deserialize(saved.Value!); Assert.IsTrue(reopened.Success);
        var restored = KineticComposition.Parse(reopened.Value!.Assets[0].TextComposition!);
        Assert.AreEqual(source, restored["clips"]![0]!["source"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(doc, restored));
        var old = asset.TextComposition;
        doc["clips"]![0]!["program"] = KineticComposition.Preset("none");
        Assert.IsTrue(session.Execute(new([new SetTextComposition(asset.Id, doc.ToJsonString())], session.GetProject().Revision)).Success);
        Assert.IsTrue(session.Undo().Success); Assert.AreEqual(old, session.GetProject().Project!.Assets[0].TextComposition);
        Assert.IsTrue(session.Redo().Success);
    }
    [TestMethod]
    public async Task MaskedVideoSharesTrimSpeedSourceMappingAndKeepsBlackForeground()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kinetai-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var rgb = Path.Combine(directory,"rgb.mp4"); var mask = Path.Combine(directory,"mask.mp4"); File.WriteAllText(rgb,"fixture"); File.WriteAllText(mask,"fixture");
            var asset = new MediaAsset(Guid.NewGuid(), "Pair", rgb, MediaKind.MaskedVideo, TimelineTime.SecondsToTicks(4)) { MaskSourcePath = mask };
            var p = ProjectWith(asset); var s = p.Sequences[0]; var t=s.Tracks[0]; var c=t.Clips[0] with { SourceInTicks=TimelineTime.TicksPerSecond, DurationTicks=TimelineTime.TicksPerSecond, PlaybackRate=2, SourceDurationTicks=2*TimelineTime.TicksPerSecond };
            p=p with { Sequences=[s with { Tracks=[t with { Clips=[c] }] }] };
            var serialized = ProjectJson.Serialize(p); Assert.IsTrue(serialized.Success, string.Join(";", serialized.Diagnostics.Select(d => d.Code + ":" + d.Message))); var reopened = ProjectJson.Deserialize(serialized.Value!); Assert.IsTrue(reopened.Success, string.Join(";", reopened.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            using var evaluation = TimelineEvaluator.Create(p,s.Id).Value!; var frame=evaluation.Evaluate(TimelineTime.SecondsToTicks(0.5m)).Value! with { Settings = new(32,32,new(30,1)) };
            var decoder=new MaskDecoder(mask); var result=await new SharedFrameRenderer(decoder).RenderAsync(p,frame,15,default);
            Assert.IsTrue(result.Success); CollectionAssert.AreEqual(new[]{2*TimelineTime.TicksPerSecond,2*TimelineTime.TicksPerSecond},decoder.Ticks.ToArray());
            Assert.AreEqual((byte)64,result.Value!.Rgba8[0]); Assert.AreEqual((byte)255,result.Value.Rgba8[3]);
            byte[] black=NativeKineticRenderer.ApplyMask([0,0,0,255],[128,128,128,255]);Assert.AreEqual((byte)128,black[3]);Assert.AreEqual((byte)0,black[0]);
        }
        finally {Directory.Delete(directory,true);}
    }
    [TestMethod]
    public void SongLengthIsIndependentOfOldRecipeLimitAndActiveClipsAreBounded()
    {
        var d=KineticComposition.New();var clips=new JsonArray();
        for(int i=0;i<300;++i) clips.Add(KineticComposition.Clip("歌詞 "+i,TimelineTime.SecondsToTicks(i*2),TimelineTime.SecondsToTicks(2)));
        d["clips"]=clips;d["durationTicks"]=TimelineTime.SecondsToTicks(600).ToString();KineticComposition.Validate(d);
        var overlap=new JsonArray();for(int i=0;i<129;++i)overlap.Add(KineticComposition.Clip("overlap",0,TimelineTime.TicksPerSecond));
        d["clips"]=overlap;Assert.ThrowsExactly<InvalidDataException>(()=>KineticComposition.Validate(d));
    }
    [TestMethod]
    public async Task RealMaskPairImportRejectsChangedFilesAndMismatchedFrameCounts()
    {
        var directory=Path.Combine(Path.GetTempPath(),"kinetai-codec-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {
            async Task Encode(string name,bool mask,int frames) {
                var info=new System.Diagnostics.ProcessStartInfo("ffmpeg"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardError=true};
                foreach(var argument in new[]{"-v","error","-y","-f","rawvideo","-pixel_format",mask?"gray":"rgb24","-video_size","32x32","-framerate","30/1","-i","pipe:0","-an","-c:v","libx264","-crf","0","-pix_fmt",mask?"gray":"yuv444p","-color_range","pc","-colorspace","bt709","-threads","1",Path.Combine(directory,name)})info.ArgumentList.Add(argument);
                using var process=System.Diagnostics.Process.Start(info)!;var error=process.StandardError.ReadToEndAsync();
                await process.StandardInput.BaseStream.WriteAsync(Enumerable.Repeat((byte)128,32*32*(mask?1:3)*frames).ToArray());process.StandardInput.Close();await process.WaitForExitAsync();Assert.AreEqual(0,process.ExitCode,await error);
            }
            JsonObject Entry(string name) {var bytes=File.ReadAllBytes(Path.Combine(directory,name));return new(){["file"]=name,["bytes"]=bytes.Length.ToString(),["sha256"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))};}
            await Encode("rgb.mp4",false,3);await Encode("mask.mp4",true,3);
            var doc=KineticComposition.New(32,32);doc["durationTicks"]=TimelineTime.SecondsToTicks(0.1m).ToString();doc["clips"]![0]!["durationTicks"]=doc["durationTicks"]!.DeepClone();
            var manifest=new JsonObject{["format"]="flamoris-kinetic-pair",["version"]=1,["pairId"]=Guid.NewGuid().ToString(),["width"]=32,["height"]=32,["fpsNumerator"]=30,["fpsDenominator"]=1,["originTicks"]="0",["durationTicks"]=doc["durationTicks"]!.DeepClone(),["frameCount"]="3",["rgbAssociation"]="premultiplied-black",["maskRange"]="full",["color"]="SDR-BT709-full",["rgb"]=Entry("rgb.mp4"),["mask"]=Entry("mask.mp4"),["composition"]=doc};
            string path=Path.Combine(directory,"text.ktpair.json");File.WriteAllText(path,manifest.ToJsonString());
            var imported=await KineticPair.ImportAsync(path);Assert.AreEqual(MediaKind.MaskedVideo,imported.Kind);Assert.AreEqual(TimelineTime.SecondsToTicks(0.1m),imported.DurationTicks);
            await File.AppendAllTextAsync(Path.Combine(directory,"mask.mp4"),"changed");await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>KineticPair.ImportAsync(path));
            await Encode("mask.mp4",true,2);manifest["mask"]=Entry("mask.mp4");File.WriteAllText(path,manifest.ToJsonString());await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>KineticPair.ImportAsync(path));
        } finally {Directory.Delete(directory,true);}
    }
    private sealed class MaskDecoder(string mask) : IMediaDecoder
    {
        public List<long> Ticks { get; } = [];
        public Task<ImmutableArray<byte>> VideoAsync(string path,long sourceTicks,int width,int height,CancellationToken token) {
            Ticks.Add(sourceTicks);var bytes=new byte[width*height*4];for(int i=0;i<bytes.Length;i+=4){bytes[i]=path==mask?(byte)128:(byte)64;bytes[i+1]=path==mask?(byte)128:(byte)0;bytes[i+2]=path==mask?(byte)128:(byte)0;bytes[i+3]=255;}return Task.FromResult(ImmutableArray.CreateRange(bytes));
        }
        public Task<ImmutableArray<float>> AudioAsync(string path,long sourceTicks,int count,int rate,int channels,CancellationToken token)=>throw new NotSupportedException();
    }
}
