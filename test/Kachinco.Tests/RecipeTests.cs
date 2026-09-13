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
