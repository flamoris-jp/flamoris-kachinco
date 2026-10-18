using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class CaptionStyleTests
{
    [TestMethod]
    public void BulkPatchPreservesIndividualFieldsTimingAndOneHistoryEntry()
    {
        var f = new Fixture(); var second = Guid.NewGuid();
        Assert.IsTrue(f.Edit(new AddCaption(f.SequenceId,f.SubtitleTrackId,new(second,4*Fixture.T,Fixture.T,"English 日本語")),
            new SetCaptionStyle(f.SequenceId,second,new(Color:"#FFCC00",X:.25,Y:.2))).Success);
        var before = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(f.Edit(new PatchCaptionStyles(f.SequenceId,[f.CaptionId,second],new(FontFamily:"Arial",FontSize:64))).Success);
        var captions = f.Project.Sequences[0].Tracks[2].Captions;
        Assert.AreEqual("#FFFFFF",captions[0].Style!.Color); Assert.AreEqual("#FFCC00",captions[1].Style!.Color);
        Assert.AreEqual(.25,captions[1].Style!.X); Assert.AreEqual(.2,captions[1].Style!.Y);
        Assert.AreEqual(4*Fixture.T,captions[1].StartTicks); Assert.AreEqual("English 日本語",captions[1].Text);
        Assert.IsTrue(captions.All(c=>c.Style!.FontFamily=="Arial" && c.Style.FontSize==64));
        var after = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before,ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(after,ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void ConditionalV7RoundtripResetLegacyAndUnknownFields()
    {
        var f = new Fixture();var legacy=ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(2,JsonDocument.Parse(legacy).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.IsFalse(legacy.Contains("\"style\""));
        Assert.IsTrue(f.Edit(new SetCaptionStyle(f.SequenceId,f.CaptionId,new(ShadowEnabled:true,ShadowColor:"#99001122",Alignment:"Right",VerticalAnchor:"Top"))).Success);
        var styled=ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(7,JsonDocument.Parse(styled).RootElement.GetProperty("schemaVersion").GetInt32());
        var loaded=ProjectJson.Deserialize(styled);Assert.IsTrue(loaded.Success);
        Assert.AreEqual(styled,ProjectJson.Serialize(loaded.Value!).Value);
        Assert.IsFalse(ProjectJson.Deserialize(styled.Replace("\"fontFamily\":", "\"surprise\":0,\"fontFamily\":")).Success);
        var oldSchema=System.Text.Json.Nodes.JsonNode.Parse(styled)!;oldSchema["schemaVersion"]=6;
        Assert.IsFalse(ProjectJson.Deserialize(oldSchema.ToJsonString()).Success);
        Assert.IsTrue(f.Edit(new SetCaptionStyle(f.SequenceId,f.CaptionId,null)).Success);
        Assert.AreEqual(legacy,ProjectJson.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void InvalidPatchIsAtomicAndEvaluationAndCacheIncludeStyle()
    {
        var f=new Fixture();var baseline=ProjectJson.Serialize(f.Project).Value;var revision=f.Session.GetProject().Revision;
        foreach(var patch in new[] { new CaptionStylePatch(FontSize:double.NaN),new(FontSize:0),new(Color:"red"),new(X:2),new(MaxWidth:0),new(Alignment:"Other"),new(FontFamily:new string('x',257)) })
        {
            Assert.IsFalse(f.Edit(new PatchCaptionStyles(f.SequenceId,[f.CaptionId],patch)).Success);
            Assert.AreEqual(baseline,ProjectJson.Serialize(f.Project).Value);Assert.AreEqual(revision,f.Session.GetProject().Revision);
        }
        Assert.IsFalse(f.Edit(new PatchCaptionStyles(f.SequenceId,[f.CaptionId,Guid.NewGuid()],new(Color:"#FF0000"))).Success);
        Assert.IsFalse(f.Edit(new PatchCaptionStyles(f.SequenceId,[f.CaptionId,f.CaptionId],new(Color:"#FF0000"))).Success);
        Assert.IsFalse(f.Edit(new PatchCaptionStyles(f.SequenceId,[],new(Color:"#FF0000"))).Success);
        var context=PreviewContext.Create(f.Session.GetProject(),f.SequenceId).Value!;var key=context.VideoKey(Fixture.T,PreviewQuality.Half);
        Assert.IsTrue(f.Edit(new PatchCaptionStyles(f.SequenceId,[f.CaptionId],new(FontSize:80,Color:"#AAEEFF"))).Success);
        var next=PreviewContext.Create(f.Session.GetProject(),f.SequenceId).Value!;
        Assert.AreNotEqual(key,next.VideoKey(Fixture.T,PreviewQuality.Half));
        Assert.AreEqual(80d,next.Evaluator.Evaluate(Fixture.T).Value!.Captions[0].Style!.FontSize);
        Assert.AreEqual("#AAEEFF",next.Evaluator.Evaluate(Fixture.T).Value!.Captions[0].Style!.Color);
        var srt=SrtCodec.Write(f.Project.Sequences[0].Tracks[2].Captions).Value!;
        Assert.IsTrue(SrtCodec.Parse(srt).Value.All(c=>c.Style is null));
    }
    [TestMethod]
    public async Task McpUsesSharedHistoryGuardsAndPartialPatchSchema()
    {
        using var h=new McpCoreHarness();using var grant=await h.Boundary.EnableAsync(McpPermission.Edit);
        var old=h.Guard();
        var result=await h.Call(grant,"edit_batch",new { commands=new[] { new { type="PatchCaptionStyles",sequenceId=h.Fixture.SequenceId,captionIds=new[] {h.Fixture.CaptionId},patch=new {fontFamily="Arial",color="#FF00FF"} } } },old);
        Assert.IsFalse(result.IsError);Assert.AreEqual("Arial",h.Fixture.Project.Sequences[0].Tracks[2].Captions[0].Style!.FontFamily);
        var stale=await h.Call(grant,"edit_batch",h.Batch(),old);Assert.IsTrue(stale.IsError);
        Assert.IsFalse((await h.Call(grant,"undo",guard:h.Guard())).IsError);
        Assert.IsNull(h.Fixture.Project.Sequences[0].Tracks[2].Captions[0].Style);
        Assert.IsFalse((await h.Call(grant,"redo",guard:h.Guard())).IsError);
        Assert.AreEqual("#FF00FF",h.Fixture.Project.Sequences[0].Tracks[2].Captions[0].Style!.Color);
        Assert.ThrowsExactly<JsonException>(()=>McpTypedSchema.Validate(typeof(PatchCaptionStyles),JsonSerializer.SerializeToElement(new {sequenceId=h.Fixture.SequenceId,captionIds=new[] {h.Fixture.CaptionId},patch=new {unexpected=true}})));
    }
}
