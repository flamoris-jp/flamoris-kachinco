using System.Collections.Immutable;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class ClipSpeedTests
{
    [TestMethod]
    public void SpeedPreservesExactSourceSpanAndOtherTracksWithHistoryPersistence()
    {
        var f = new Fixture(); var before = ProjectJson.Serialize(f.Project).Value;
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, Fixture.T, 8 * Fixture.T - 3)).Success);
        long span = f.VideoClip.DurationTicks;
        var audio = f.Project.Sequences[0].Tracks[1];
        Assert.IsTrue(f.Edit(new SetClipSpeed(f.SequenceId, f.ClipId, 8)).Success);
        Assert.AreEqual((span + 7) / 8, f.VideoClip.DurationTicks);
        Assert.AreEqual(span, f.VideoClip.SourceDurationTicks);
        Assert.AreEqual(JsonSerializer.Serialize(audio), JsonSerializer.Serialize(f.Project.Sequences[0].Tracks[1]));
        string text = ProjectJson.Serialize(f.Project).Value!;
        Assert.AreEqual(5, JsonDocument.Parse(text).RootElement.GetProperty("schemaVersion").GetInt32());
        var reopened = ProjectJson.Deserialize(text); Assert.IsTrue(reopened.Success);
        Assert.AreEqual(8, reopened.Value!.Sequences[0].Tracks[0].Clips[0].PlaybackRate);
        using var eval = TimelineEvaluator.Create(reopened.Value, f.SequenceId).Value!;
        foreach (long tick in new[] { 0L, Fixture.T / 2, f.VideoClip.DurationTicks - 1 })
            Assert.AreEqual(Fixture.T + Math.Min(tick * 8, span - 1), eval.Evaluate(tick).Value!.VideoLayers[0].SourceTicks);
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(1, f.VideoClip.PlaybackRate);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(8, f.VideoClip.PlaybackRate);
        Assert.IsTrue(f.Edit(new SetClipSpeed(f.SequenceId, f.ClipId, 1)).Success);
        Assert.AreEqual(span, f.VideoClip.DurationTicks);
        Assert.AreEqual(2, JsonDocument.Parse(ProjectJson.Serialize(f.Project).Value!).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(ProjectJson.Deserialize(before!).Success);
    }

    [TestMethod]
    public void SplitTrimDuplicateAndAutomationFollowSourceTime()
    {
        var f = new Fixture(); var key = Guid.NewGuid();
        Assert.IsTrue(f.Edit(new SetClipPropertyCurve(f.SequenceId, f.ClipId,
            new(VisualProperty.X, [new(key, 4 * Fixture.T, 100)])), new SetClipSpeed(f.SequenceId, f.ClipId, 4)).Success);
        Assert.AreEqual(Fixture.T, f.VideoClip.Appearance.Automation[0].Points[0].Tick);
        var rightId = Guid.NewGuid();
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.ClipId, Fixture.T, rightId)).Success);
        var right = f.Project.Sequences[0].Tracks[0].Clips.Single(c => c.Id == rightId);
        Assert.AreEqual(5 * Fixture.T, right.SourceInTicks); Assert.AreEqual(4 * Fixture.T, right.SourceDurationTicks);
        Assert.AreEqual(0L, right.Appearance.Automation[0].Points[0].Tick);
        var trim = TimelineEditPlanner.Trim(f.Project, f.SequenceId, rightId, TrimEdge.Start, Fixture.T + Fixture.T / 4);
        Assert.IsTrue(trim.Success); Assert.AreEqual(6 * Fixture.T, trim.Value!.SourceInTicks);
        Assert.IsTrue(f.Edit(trim.Value).Success);
        var duplicate = TimelineEditPlanner.Duplicate(f.Project, f.SequenceId, rightId, Guid.NewGuid());
        Assert.IsTrue(duplicate.Success); Assert.IsTrue(f.Session.Execute(duplicate.Value!).Success);
        Assert.IsTrue(f.Project.Sequences[0].Tracks[0].Clips.All(c => c.PlaybackRate == 4));
    }

    [TestMethod]
    public void InvalidRatesOverlapOverflowAndCollidingKeysFailAtomically()
    {
        var f = new Fixture();
        foreach (int rate in new[] { 0, -1, 3, int.MaxValue }) Assert.IsFalse(f.Edit(new SetClipSpeed(f.SequenceId, f.ClipId, rate)).Success);
        Assert.IsTrue(f.Edit(new SetClipSpeed(f.SequenceId, f.ClipId, 8)).Success);
        Assert.IsTrue(f.Edit(new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(Guid.NewGuid(), f.MovId, Fixture.T, 0, Fixture.T))).Success);
        var before = ProjectJson.Serialize(f.Project).Value;
        Assert.IsFalse(f.Edit(new SetClipSpeed(f.SequenceId, f.ClipId, 1)).Success);
        Assert.AreEqual(before, ProjectJson.Serialize(f.Project).Value);
        var g = new Fixture();
        Assert.IsTrue(g.Edit(new SetClipPropertyCurve(g.SequenceId, g.ClipId,
            new(VisualProperty.X, [new(Guid.NewGuid(), 1, 1), new(Guid.NewGuid(), 2, 2)]))).Success);
        Assert.IsFalse(g.Edit(new SetClipSpeed(g.SequenceId, g.ClipId, 8)).Success);
        Assert.IsTrue(g.Edit(new SetClipPropertyCurve(g.SequenceId, g.ClipId,
            new(VisualProperty.X, [new(Guid.NewGuid(), long.MaxValue, 1)])), new SetClipSpeed(g.SequenceId, g.ClipId, 8)).Success);
        // Returning from 8x multiplies the already-retimed key safely.
        Assert.IsTrue(g.Edit(new SetClipSpeed(g.SequenceId, g.ClipId, 1)).Success);
        Assert.IsTrue(g.Edit(new SetClipSpeed(g.SequenceId, g.ClipId, 8), new SetClipPropertyCurve(g.SequenceId, g.ClipId,
            new(VisualProperty.X, [new(Guid.NewGuid(), long.MaxValue, 1)]))).Success);
        Assert.IsFalse(g.Edit(new SetClipSpeed(g.SequenceId, g.ClipId, 1)).Success);
    }

    [TestMethod]
    public void V5RejectsMissingUnknownInconsistentAndOutOfRangeSpeedFields()
    {
        var f = new Fixture(); Assert.IsTrue(f.Edit(new SetClipSpeed(f.SequenceId, f.ClipId, 8)).Success);
        string valid = ProjectJson.Serialize(f.Project).Value!;
        foreach (string mode in new[] { "missing", "unknown", "span", "rate", "null" })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(valid)!;
            var clip = json["project"]!["sequences"]![0]!["tracks"]![0]!["clips"]![0]!.AsObject();
            if (mode == "missing") clip.Remove("sourceDurationTicks");
            if (mode == "unknown") clip["speedCurve"] = 1;
            if (mode == "span") clip["sourceDurationTicks"] = "1";
            if (mode == "rate") clip["playbackRate"] = 0;
            if (mode == "null") clip["sourceDurationTicks"] = null;
            Assert.IsFalse(ProjectJson.Deserialize(json.ToJsonString()).Success, mode);
        }
    }

    [TestMethod]
    public async Task AudioAdvancesAtSpeedAndEnvelopeUsesOutputSampleTime()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.AudioClipId, 0, 0, 4 * Fixture.T),
            new SetClipProperties(f.SequenceId, f.AudioClipId, true, ClipAppearance.Default, new(0.6, false)),
            new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Guid.NewGuid(), 0, 0)),
            new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Guid.NewGuid(), 4 * Fixture.T, 1)),
            new SetClipSpeed(f.SequenceId, f.AudioClipId, 4)).Success);
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "input.wav"), [1]);
            using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
            var decoder = new RampDecoder();
            var block = await new SharedAudioRenderer(decoder, Path.Combine(dir, "project.fkproj")).RenderAsync(evaluator, 12000, 600, 48000, 2, default);
            Assert.IsTrue(block.Success, string.Join(";", block.Diagnostics.Select(d => d.Message)));
            Assert.AreEqual(Fixture.T, decoder.LastTick);
            Assert.AreEqual(2400, decoder.LastCount);
            Assert.AreEqual(0.15f, block.Value!.Samples[0], 1e-5);
            Assert.AreEqual((float)((1 + 4 / 48000d) * .6 * (12001 / 48000d)), block.Value.Samples[2], 1e-5);
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class RampDecoder : IMediaDecoder
    {
        public long LastTick; public int LastCount;
        public Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token) => throw new NotSupportedException();
        public Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token)
        {
            LastTick = sourceTicks; LastCount = count;
            return Task.FromResult(ImmutableArray.CreateRange(Enumerable.Range(0, count * channels).Select(i => (float)((double)sourceTicks / Fixture.T + i / channels / (double)rate))));
        }
    }
}
