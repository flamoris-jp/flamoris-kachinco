using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class RecipeTests
{
    [TestMethod]
    public async Task RestrictedPythonCompilesRepeatablyAndRejectsExecutionCapabilities()
    {
        var compiler = new RecipeCompiler();
        const string source = "text(text=\"グエー\", x=10, y=20, vx=80)\nparticles(count=20, x=0, y=50, vy=-30, size=4)";
        var a = await compiler.CompileAsync(source); var b = await compiler.CompileAsync(source);
        Assert.IsTrue(a.Success, string.Join(";", a.Diagnostics.Select(d=>d.Message)));
        Assert.IsTrue(b.Success);
        CollectionAssert.AreEqual(a.Value!.Operations.ToArray(),b.Value!.Operations.ToArray());
        foreach (var hostile in new[] { "import os", "text(text=open('secret').read())", "while True: pass", "text(text=__import__('os').getcwd())", "particles(count=1000000)", "text(text='x', x=float('nan'))", "text(text='x', **{})" })
            Assert.IsFalse((await compiler.CompileAsync(hostile)).Success, hostile);
    }
    [TestMethod]
    public async Task CancelledRecipeCannotCommitAnything()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await new RecipeCompiler().CompileAsync("text(text='x')",cancelled.Token);
        Assert.IsFalse(result.Success);
    }
    [TestMethod]
    public async Task InvalidRasterDimensionsBecomeStructuredGenerationFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kachinco-recipe-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var f = new Fixture();
            var clapper = new Clapper(Fixture.Id(20), "A-1", 0, Fixture.T / 10, null, f.VideoTrackId, null, "");
            Assert.IsTrue(f.Edit(new AddClapper(f.SequenceId, clapper)).Success);
            var recipe = new Recipe(Fixture.Id(21), clapper.Id, "text(text='x')", 1, 42, "1", "1");
            var result = await new RecipeGenerationService(new RecipeCompiler(), new InvalidRecipeRasterizer()).PrepareAsync(
                f.Session.GetProject(), f.SequenceId, recipe, Path.Combine(folder, "invalid.mov"));
            Assert.IsFalse(result.Success);
            Assert.AreEqual("RECIPE_GENERATION_FAILED", result.Diagnostics[0].Code);
            Assert.IsFalse(File.Exists(Path.Combine(folder, "invalid.mov")));
        }
        finally { Directory.Delete(folder, true); }
    }
    [TestMethod]
    public async Task GeneratedClipRegeneratesWithoutLosingManualPlacementAndUndoesOnce()
    {
        var folder=Path.Combine(Path.GetTempPath(),"kachinco-recipe-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var f=new Fixture();var clapper=new Clapper(Fixture.Id(20),"A-1",0,Fixture.T/10,null,f.VideoTrackId,null,"");
            Assert.IsTrue(f.Edit(new AddClapper(f.SequenceId,clapper)).Success);
            var recipe=new Recipe(Fixture.Id(21),clapper.Id,"text(text='x')",1,42,"1","1");
            var service=new RecipeGenerationService(new RecipeCompiler(),new SolidRecipeRasterizer());
            var first=await service.PrepareAsync(f.Session.GetProject(),f.SequenceId,recipe,Path.Combine(folder,"first.mov"));
            Assert.IsTrue(first.Success,string.Join(";",first.Diagnostics.Select(d=>d.Message)));
            Assert.IsTrue(f.Session.Execute(first.Value!.Batch).Success);
            Assert.IsTrue(f.Edit(new MoveClip(f.SequenceId,first.Value.ClipId,f.VideoTrackId,2*Fixture.T)).Success);
            var before=ProjectJson.Serialize(f.Project).Value;
            var second=await service.PrepareAsync(f.Session.GetProject(),f.SequenceId,recipe with {Revision=2,Source="text(text='y')"},Path.Combine(folder,"second.mov"),first.Value.MediaAssetId);
            Assert.IsTrue(second.Success,string.Join(";",second.Diagnostics.Select(d=>d.Message)));
            Assert.IsTrue(f.Session.Execute(second.Value!.Batch).Success);
            var clip=f.Project.Sequences[0].Tracks[0].Clips.Single(c=>c.Id==first.Value.ClipId);
            Assert.AreEqual(2*Fixture.T,clip.StartTicks);Assert.AreEqual(first.Value.MediaAssetId,clip.MediaAssetId);
            Assert.IsTrue(f.Session.Undo().Success);Assert.AreEqual(before,ProjectJson.Serialize(f.Project).Value);
            Assert.IsTrue(File.Exists(first.Value.OutputPath));Assert.IsTrue(File.Exists(second.Value.OutputPath));
        }
        finally { Directory.Delete(folder,true); }
    }
    private sealed class SolidRecipeRasterizer : IRecipeRasterizer
    {
        public ValueTask<System.Collections.Immutable.ImmutableArray<byte>> RenderAsync(RecipeIr ir,Recipe recipe,Clapper clapper,SequenceSettings settings,long localTicks,CancellationToken token)
        {
            var bytes=new byte[settings.Width*settings.Height*4];bytes[0]=255;bytes[3]=255;
            return ValueTask.FromResult(System.Collections.Immutable.ImmutableArray.CreateRange(bytes));
        }
    }
    private sealed class InvalidRecipeRasterizer : IRecipeRasterizer
    {
        public ValueTask<System.Collections.Immutable.ImmutableArray<byte>> RenderAsync(RecipeIr ir, Recipe recipe, Clapper clapper,
            SequenceSettings settings, long localTicks, CancellationToken token) =>
            ValueTask.FromResult(System.Collections.Immutable.ImmutableArray.Create<byte>(0));
    }

    [TestMethod]
    public void RecipeReferencesAndProvenanceSurviveRoundTrip()
    {
        var f=new Fixture(); var clapper=new Clapper(Fixture.Id(20),"A-1",0,Fixture.T,null,f.VideoTrackId,null,"");
        var recipe=new Recipe(Fixture.Id(21),clapper.Id,"text(text='グエー')",1,42,"1","1");
        Assert.IsTrue(f.Edit(new AddClapper(f.SequenceId,clapper),new AddRecipe(f.SequenceId,recipe),
            new SetGeneratedProvenance(f.MovId,new(recipe.Id,1,new string('a',64),new string('b',64)))).Success);
        var result=ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!);
        Assert.IsTrue(result.Success); Assert.AreEqual(recipe,result.Value!.Sequences[0].Recipes[0]);
        Assert.AreEqual(f.Project.Assets[0].Provenance,result.Value.Assets.First(a=>a.Id==f.MovId).Provenance);
        Assert.IsFalse(f.Edit(new DeleteClapper(f.SequenceId,clapper.Id)).Success);
    }
}
