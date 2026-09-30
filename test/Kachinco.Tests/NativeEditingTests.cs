using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Kachinco.Tests.Oracles;
using Kachinco.Tests.PersistenceOracles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class NativeEditingTests
{
    [TestMethod]
    public void SeededCommandsBatchesHistoryAndDiagnosticsMatchReviewedManagedOracle()
    {
        using var native = new EditorSession(7);
        var managed = new ManagedSessionOracle(7);
        var initial = new Fixture().Project;
        native.ReplaceProject(initial); managed.ReplaceProject(initial);
        var random = new Random(353128);
        var clapper = new Clapper(Fixture.Id(31), "cue", 0, Fixture.T, null, null, null, "notes");
        var recipe = new Recipe(Fixture.Id(32), clapper.Id, "k.rect(0,0,1,1)", 1, 7, "1", "1");
        EditCommand[] commands = [
            new CreateProject(Fixture.Id(90), "exists"),
            new CreateSequence(Fixture.Id(40), "extra", SequenceSettings.Portrait, Fixture.T),
            new SetSequenceDuration(Fixture.Id(2), 9 * Fixture.T),
            new RegisterMedia(new(Fixture.Id(41), "extra", "image.MP4", MediaKind.Mov, 9 * Fixture.T)),
            new RelinkMedia(Fixture.Id(3), "new.MOV", 10 * Fixture.T),
            new AddTrack(Fixture.Id(2), Fixture.Id(42), "V2", TrackKind.Video),
            new InsertClip(Fixture.Id(2), Fixture.Id(5), Fixture.Clip(Fixture.Id(43), Fixture.Id(3), 0, 0, Fixture.T)),
            new MoveClip(Fixture.Id(2), Fixture.Id(8), Fixture.Id(5), 0),
            new TrimClip(Fixture.Id(2), Fixture.Id(8), 0, 0, 7 * Fixture.T),
            new SplitClip(Fixture.Id(2), Fixture.Id(8), Fixture.T, Fixture.Id(44)),
            new DeleteClip(Fixture.Id(2), Fixture.Id(44)),
            new SetClipProperties(Fixture.Id(2), Fixture.Id(8), true, new(new(1,2,2,3,45),0.6,BlendMode.Screen),new(0.5,true)),
            new SetTrackEnabled(Fixture.Id(2), Fixture.Id(5), false),
            new ReorderTrack(Fixture.Id(2), Fixture.Id(5), 1),
            new AddCaption(Fixture.Id(2), Fixture.Id(7), new(Fixture.Id(45), 0, Fixture.T, "日本語😀")),
            new UpdateCaption(Fixture.Id(2), Fixture.Id(10), 0, Fixture.T, "updated", false),
            new DeleteCaption(Fixture.Id(2), Fixture.Id(45)),
            new AddClapper(Fixture.Id(2), clapper), new UpdateClapper(Fixture.Id(2), clapper with { Notes = "changed" }),
            new DeleteClapper(Fixture.Id(2), clapper.Id), new AddRecipe(Fixture.Id(2), recipe),
            new UpdateRecipe(Fixture.Id(2), recipe with { Revision = 2 }),
            new SetGeneratedProvenance(Fixture.Id(3), new(recipe.Id,1,new('a',64),new('b',64))),
            new TrimClip(Fixture.Id(2), Fixture.Id(8), long.MaxValue, 0, 2),
            new AddTrack(Fixture.Id(2), Fixture.Id(46), "unknown", (TrackKind)999),
            new SetClipProperties(Fixture.Id(2), Fixture.Id(8), true, new(Transform2D.Identity,double.NaN,BlendMode.Normal),new(double.PositiveInfinity,false)),
            new RegisterMedia(null!), new AddClapper(Fixture.Id(2), null!), new AddRecipe(Fixture.Id(2), null!),
            new UpdateClapper(Fixture.Id(2), null!), new UpdateRecipe(Fixture.Id(2), null!),
            new SetGeneratedProvenance(Fixture.Id(3), null!)
        ];
        for (int step = 0; step < 2400; step++)
        {
            EditResult a,b;
            long? expected = step % 11 == 0 ? -1 : native.GetProject().Revision;
            switch (step % 17)
            {
                case 0: a=native.Undo(expected);b=managed.Undo(expected);break;
                case 1: a=native.Redo(expected);b=managed.Redo(expected);break;
                case 2: a=native.ReplaceProject(step%51==2?initial:null,expected);b=managed.ReplaceProject(step%51==2?initial:null,expected);break;
                default:
                    var batch = new EditBatch([..Enumerable.Range(0,random.Next(1,4)).Select(_=>commands[random.Next(commands.Length)])],expected,step%13==0);
                    a=native.Execute(batch);b=managed.Execute(batch);break;
            }
            Assert.AreEqual(b.Success,a.Success,$"step {step}"); Assert.AreEqual(b.Revision,a.Revision,$"step {step}");
            CollectionAssert.AreEqual(b.Diagnostics.ToArray(),a.Diagnostics.ToArray(),$"step {step}");
            Compare(native.GetProject(),managed.GetProject(),step);
            // Reintroduce a complete domain often enough to exercise more than empty-session failures.
            if(step%37==0) {native.ReplaceProject(initial);managed.ReplaceProject(initial);}
        }
        // Explicit authoring/provenance success chain and undo/redo of compound transaction.
        var authoring = new EditBatch([new AddClapper(Fixture.Id(2),clapper),new AddRecipe(Fixture.Id(2),recipe),new SetGeneratedProvenance(Fixture.Id(3),new(recipe.Id,1,new('a',64),new('b',64))),new UpdateRecipe(Fixture.Id(2),recipe with {Revision=2}),new UpdateClapper(Fixture.Id(2),clapper with {Geometry=new(ClapperGeometryKind.Rectangle,1,2,3,4)})]);
        Assert.IsTrue(native.Execute(authoring).Success);Assert.IsTrue(managed.Execute(authoring).Success);Compare(native.GetProject(),managed.GetProject(),2401);
        native.Undo();managed.Undo();Compare(native.GetProject(),managed.GetProject(),2402);native.Redo();managed.Redo();Compare(native.GetProject(),managed.GetProject(),2403);
    }
    private static void Compare(ProjectSnapshot a,ProjectSnapshot b,int step)
    {
        Assert.AreEqual(b.Revision,a.Revision);Assert.AreEqual(b.CanUndo,a.CanUndo);Assert.AreEqual(b.CanRedo,a.CanRedo);
        Assert.AreEqual(b.Project is null,a.Project is null);
        if(a.Project is not null) Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(ManagedProjectJsonOracle.Serialize(b.Project!).Value!),JsonNode.Parse(ProjectJson.Serialize(a.Project).Value!)), $"project step {step}");
    }
    [TestMethod]
    public void RecipeRevisionOverflowKeepsTheReviewedRejectionAndState()
    {
        var p = new Fixture().Project;
        var clapper = new Clapper(Fixture.Id(31), "cue", 0, Fixture.T, null, null, null, "");
        var recipe = new Recipe(Fixture.Id(32), clapper.Id, "source", int.MaxValue, 0, "1", "1");
        p = p with { Sequences = [p.Sequences[0] with { Clappers = [clapper], Recipes = [recipe] }] };
        using var native = new EditorSession(); var managed = new ManagedSessionOracle();
        native.ReplaceProject(p); managed.ReplaceProject(p);
        var batch = new EditBatch([new UpdateRecipe(Fixture.Id(2), recipe with { Revision = int.MinValue })]);
        CollectionAssert.AreEqual(managed.Execute(batch).Diagnostics.ToArray(), native.Execute(batch).Diagnostics.ToArray());
        Compare(native.GetProject(), managed.GetProject(), 0);
    }
    [TestMethod]
    public void CancellationAndDocumentRevocationOccurBeforeCommit()
    {
        using var session = new EditorSession();var initial=new Fixture().Project;session.ReplaceProject(initial);
        var before=session.GetProject();var token=session.DocumentToken;int invalidations=0;
        session.DocumentReplacing += () => {invalidations++;Assert.AreSame(before,session.GetProject());Assert.AreEqual(token,session.DocumentToken);};
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>session.Execute(new([new DeleteClip(Fixture.Id(2),Fixture.Id(8))]),cancelled.Token));
        Assert.AreSame(before,session.GetProject());Assert.AreEqual(0,invalidations);
        Assert.IsFalse(session.ReplaceProject(initial with {Name=""}).Success);Assert.AreEqual(0,invalidations);
        Assert.IsTrue(session.ReplaceProject(initial).Success);Assert.AreEqual(1,invalidations);Assert.AreNotEqual(token,session.DocumentToken);
    }
    [TestMethod]
    public void NativeRawPreparedChangesAreInvisibleAbortableAndGuarded()
    {
        using var session=new NativeEditorSession(2);
        var initial="{\"action\":\"execute\",\"commands\":[{\"type\":\"CreateProject\",\"value\":{\"projectId\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"one\"}}]}";
        var prepared=JsonNode.Parse(session.Request(initial))!;
        Assert.AreEqual(0L,JsonNode.Parse(session.Request("{\"action\":\"get\"}"))!["revision"]!.GetValue<long>());
        session.Request("{\"action\":\"abort\"}");
        Assert.IsFalse(JsonNode.Parse(session.Request("{\"action\":\"commit\",\"transactionId\":"+prepared["transactionId"]+"}"))!["success"]!.GetValue<bool>());
        prepared=JsonNode.Parse(session.Request(initial))!;
        Assert.IsTrue(JsonNode.Parse(session.Request("{\"action\":\"commit\",\"transactionId\":"+prepared["transactionId"]+"}"))!["success"]!.GetValue<bool>());
        Assert.AreEqual(1L,JsonNode.Parse(session.Request("{\"action\":\"get\"}"))!["revision"]!.GetValue<long>());
        Assert.ThrowsExactly<NativeRuntimeException>(()=>session.Request("{\"action\":\"get\",\"action\":\"undo\"}"));
    }
    [TestMethod]
    public void FrozenProjectCodecCrossReadsV1V2AndMalformedContracts()
    {
        var p=new Fixture().Project;
        var old=ManagedProjectJsonOracle.Serialize(p).Value!;var current=ProjectJson.Serialize(p).Value!;
        Assert.IsTrue(ProjectJson.Deserialize(old).Success);Assert.IsTrue(ManagedProjectJsonOracle.Deserialize(current).Success);
        var v1=JsonNode.Parse(old)!;v1["schemaVersion"]=1;v1.AsObject().Remove("authoring");v1.AsObject().Remove("generatedAssets");
        Assert.IsTrue(ProjectJson.Deserialize(v1.ToJsonString()).Success);
        string[] inputs=[old,current,v1.ToJsonString(),old.Replace("\"Mov\"","\" mov \""),old.Replace("\"Mov\"","\"Mov, Wav\""),old.Replace(Fixture.Id(1).ToString(),Fixture.Id(1).ToString("X")),old.Replace(Fixture.Id(1).ToString(),Fixture.Id(1).ToString("N")),"{}","null","[]","{","{\"schemaVersion\":99}","{\"schemaVersion\":2147483648}",old.Replace("\"schemaVersion\": 2","\"schemaVersion\": 2,\"schemaVersion\": 2"),old.Replace("\"35280000\"","35280000"),old.Replace("\"Mov\"","0")];
        foreach(var input in inputs)
        {
            var a=ProjectJson.Deserialize(input);var b=ManagedProjectJsonOracle.Deserialize(input);
            Assert.AreEqual(b.Success,a.Success,input);CollectionAssert.AreEqual(b.Diagnostics.ToArray(),a.Diagnostics.ToArray(),input);
        }
        Project[] invalid=[p with {Name="\u3000"},p with {Name=""},p with {Assets=default},p with {Sequences=[p.Sequences[0] with {Clappers=default}]},p with {Assets=[p.Assets[0] with {SourcePath="a.mov\0bad"},p.Assets[1]]}];
        foreach(var value in invalid) CollectionAssert.AreEqual(ManagedValidatorOracle.Validate(value).ToArray(),ProjectValidator.Validate(value).ToArray());
    }
}
