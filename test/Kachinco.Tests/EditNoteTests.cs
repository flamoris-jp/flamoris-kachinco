using System.Text.Json;
using System.Text.Json.Nodes;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class EditNoteTests
{
    [TestMethod]
    public void NotesReuseCaptionCommandsHistoryTimingAndConditionalV9()
    {
        var f = new Fixture(); var baseline = ProjectJson.Serialize(f.Project).Value!;
        var note = new Caption(Guid.NewGuid(), 0, 2 * Fixture.T, "ここで急に引く\nクラリネットで反物が招く");
        var plan = CaptionImport.Plan(f.Project.Sequences[0], [note], Fixture.T,
            f.Session.GetProject().Revision, TrackKind.EditNote);
        Assert.IsTrue(f.Session.Execute(plan).Success);
        var project = f.Project; var track = project.Sequences[0].Tracks.Single(t => t.Kind == TrackKind.EditNote);
        Assert.AreEqual(Fixture.T, track.Captions[0].StartTicks);
        var encoded = ProjectJson.Serialize(project).Value!;
        Assert.AreEqual(9, JsonDocument.Parse(encoded).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(encoded, ProjectJson.Serialize(ProjectJson.Deserialize(encoded).Value!).Value);
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(baseline, ProjectJson.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(encoded, ProjectJson.Serialize(f.Project).Value);
        var sequence = f.Project.Sequences[0];
        var trim = CaptionTimingEdits.Plan(sequence, track.Captions[0], Fixture.T, TrimEdge.End,
            new TimelineViewport(100), false, 0);
        Assert.IsTrue(f.Edit(trim).Success);
        Assert.AreEqual(3 * Fixture.T, f.Project.Sequences[0].Tracks.Single(t => t.Kind == TrackKind.EditNote).Captions[0].DurationTicks);
        var invalid = f.Edit(new UpdateCaption(f.SequenceId, note.Id, -1, Fixture.T, "bad", true));
        Assert.IsFalse(invalid.Success);
        Assert.IsTrue(f.Edit(new DeleteCaption(f.SequenceId, note.Id)).Success);
        // An empty M track persists, rather than disappearing on reload.
        Assert.IsTrue(ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!).Value!
            .Sequences[0].Tracks.Single(t => t.Kind == TrackKind.EditNote).Captions.IsEmpty);
        var old = JsonNode.Parse(encoded)!; old["schemaVersion"] = 8;
        Assert.IsFalse(ProjectJson.Deserialize(old.ToJsonString()).Success);
        old = JsonNode.Parse(encoded)!; old["project"]!["sequences"]![0]!["tracks"]![3]!["kind"] = "Bogus";
        Assert.IsFalse(ProjectJson.Deserialize(old.ToJsonString()).Success);
    }

    [TestMethod]
    public async Task NativeActivitySeparatesNotesFromAllRenderedCaptionsAndSrt()
    {
        var f = new Fixture(); var id = Guid.NewGuid(); var trackId = Guid.NewGuid();
        Assert.IsTrue(f.Edit(new AddTrack(f.SequenceId, trackId, "M1", TrackKind.EditNote),
            new AddCaption(f.SequenceId, trackId, new(id, Fixture.T, Fixture.T, "PRIVATE EDITING NOTE"))).Success);
        using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.IsTrue(evaluator.Evaluate(Fixture.T - 1).Value!.EditNotes.IsEmpty);
        var frame = evaluator.Evaluate(Fixture.T).Value!;
        Assert.AreEqual(id, frame.EditNotes.Single().CaptionId);
        Assert.AreEqual(f.CaptionId, frame.Captions.Single().CaptionId);
        Assert.IsTrue(evaluator.Evaluate(2 * Fixture.T).Value!.EditNotes.IsEmpty);
        Assert.IsFalse(SrtCodec.Write(f.Project.Sequences[0]).Value!.Contains("PRIVATE"));
        Assert.IsFalse(f.Edit(new InsertClip(f.SequenceId, trackId,
            Fixture.Clip(Guid.NewGuid(), f.MovId, 0, 0, Fixture.T))).Success);
        // Both shared preview and export adapters receive only real captions.
        var recorder = new RecordingCaptions();
        var captionsOnly = frame with { VideoLayers = [], Audio = [], Settings = new(32, 18, new(30, 1)) };
        using var decoder = new FfmpegMediaDecoder();
        var shared = new SharedFrameRenderer(decoder, captions: recorder);
        Assert.IsTrue((await shared.RenderAsync(f.Project, captionsOnly, 30, CancellationToken.None)).Success);
        CollectionAssert.AreEqual(new[] { f.CaptionId }, recorder.Ids);
        Assert.IsTrue(f.Edit(new SetTrackEnabled(f.SequenceId, trackId, false)).Success);
        using var disabled = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.IsTrue(disabled.Evaluate(Fixture.T).Value!.EditNotes.IsEmpty);
    }

    [TestMethod]
    public async Task McpUsesExistingGuardedCaptionCommandsForNotes()
    {
        using var h = new McpCoreHarness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var track = Guid.NewGuid(); var note = Guid.NewGuid(); var guard = h.Guard();
        object[] commands = [
            new { type = "AddTrack", sequenceId = h.Fixture.SequenceId, trackId = track, name = "M1", kind = "EditNote" },
            new { type = "AddCaption", sequenceId = h.Fixture.SequenceId, trackId = track,
                caption = new { id = note, startTicks = "0", durationTicks = Fixture.T.ToString(), text = "ゆっくり引く", enabled = true } }
        ];
        Assert.IsFalse((await h.Call(grant, "edit_batch", new { commands }, guard)).IsError);
        Assert.IsTrue(h.Fixture.Project.Sequences[0].Tracks.Any(t => t.Kind == TrackKind.EditNote && t.Captions[0].Id == note));
        Assert.IsTrue((await h.Call(grant, "edit_batch", new { commands }, guard)).IsError);
        Assert.IsFalse((await h.Call(grant, "undo", guard: h.Guard())).IsError);
        Assert.IsFalse(h.Fixture.Project.Sequences[0].Tracks.Any(t => t.Kind == TrackKind.EditNote));
        Assert.IsFalse((await h.Call(grant, "redo", guard: h.Guard())).IsError);
    }

    private sealed class RecordingCaptions : ICaptionRasterizer
    {
        public Guid[] Ids { get; private set; } = [];
        public ValueTask<System.Collections.Immutable.ImmutableArray<byte>> RasterizeAsync(
            System.Collections.Immutable.ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token)
        { Ids = captions.Select(c => c.CaptionId).ToArray(); return ValueTask.FromResult(System.Collections.Immutable.ImmutableArray.CreateRange(new byte[width * height * 4])); }
    }
}
