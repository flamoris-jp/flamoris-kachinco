using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class VolumeAutomationTests
{
    [TestMethod]
    public void V1V2StayCompatibleAndV3RetainsScopedIdentityAndExactSignedTicks()
    {
        var f = new Fixture(); string old = NativeProjectCodec.Serialize(f.Project).Value!;
        Assert.AreEqual(2, JsonNode.Parse(old)!["schemaVersion"]!.GetValue<int>());
        Assert.IsTrue(NativeProjectCodec.Deserialize(old).Success);
        Assert.IsTrue(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(100), -Fixture.T, .25)),
            new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(101), 9_007_199_254_740_993, 1))).Success);
        string current = NativeProjectCodec.Serialize(f.Project).Value!;
        Assert.AreEqual(3, JsonNode.Parse(current)!["schemaVersion"]!.GetValue<int>());
        Assert.IsTrue(current.Contains("9007199254740993"));
        var reopened = NativeProjectCodec.Deserialize(current); Assert.IsTrue(reopened.Success);
        Assert.AreEqual(current, NativeProjectCodec.Serialize(reopened.Value!).Value);
        string[] bad = [current.Replace("\"tick\": \"-35280000\"", "\"tick\": -35280000"),
            current.Replace("\"volumePoints\": [", "\"unknown\": [], \"volumePoints\": ["),
            current.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 2")];
        foreach (var value in bad) { Assert.AreNotEqual(current, value); Assert.IsFalse(NativeProjectCodec.Deserialize(value).Success); }
        Assert.IsTrue(f.Edit(new DeleteClipVolumePoint(f.SequenceId, f.AudioClipId, Fixture.Id(100)), new DeleteClipVolumePoint(f.SequenceId, f.AudioClipId, Fixture.Id(101))).Success);
        Assert.AreEqual(old, NativeProjectCodec.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void PointEditsAreAtomicOrderedRevisionScopedAndPreserveConstantGainChanges()
    {
        var f = new Fixture(); var baseline = f.Session.GetProject();
        var a = new VolumePoint(Fixture.Id(100), 0, 0); var b = new VolumePoint(Fixture.Id(101), Fixture.T, 1);
        Assert.IsTrue(f.Session.Execute(new([new AddClipVolumePoint(f.SequenceId, f.AudioClipId, b), new AddClipVolumePoint(f.SequenceId, f.AudioClipId, a)], baseline.Revision, true)).Success);
        Assert.AreEqual(baseline.Revision, f.Session.GetProject().Revision);
        Assert.IsTrue(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, b), new AddClipVolumePoint(f.SequenceId, f.AudioClipId, a)).Success);
        CollectionAssert.AreEqual(new[] { a, b }, Audio(f).Audio.VolumePoints.ToArray());
        var before = NativeProjectCodec.Serialize(f.Project).Value;
        Assert.IsFalse(f.Edit(new UpdateClipVolumePoint(f.SequenceId, f.AudioClipId, a with { Tick = b.Tick })).Success);
        Assert.IsFalse(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, a with { Tick = -1 })).Success);
        Assert.IsFalse(f.Edit(new UpdateClipVolumePoint(f.SequenceId, f.AudioClipId, a with { Multiplier = double.NaN })).Success);
        Assert.IsFalse(f.Edit(new AddClipVolumePoint(f.SequenceId, f.ClipId, a)).Success);
        Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsFalse(f.Session.Execute(new([new DeleteClipVolumePoint(f.SequenceId, f.AudioClipId, a.Id)], baseline.Revision)).Success);
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, f.AudioClipId, true, Audio(f).Appearance, new(.5, false))).Success);
        CollectionAssert.AreEqual(new[] { a, b }, Audio(f).Audio.VolumePoints.ToArray());
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(.5, Audio(f).Audio.Gain);
    }
    [TestMethod]
    public void SplitAndTrimPreserveCurveOnTheSameSourceSamples()
    {
        var f = new Fixture(); AddRamp(f, 6 * Fixture.T); var original = f.Project;
        using var before = TimelineEvaluator.Create(original, f.SequenceId).Value!;
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.AudioClipId, 3 * Fixture.T, Fixture.Id(102))).Success);
        using var split = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        foreach (long tick in new[] { Fixture.T, 2 * Fixture.T, 3 * Fixture.T, 4 * Fixture.T, 7 * Fixture.T - 1 })
            Assert.AreEqual(before.Evaluate(tick).Value!.Audio.Single().Gain, split.Evaluate(tick).Value!.Audio.Single().Gain, 1e-12);
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.AudioClipId, 2 * Fixture.T, 3 * Fixture.T, 5 * Fixture.T)).Success);
        using var trimmed = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(-Fixture.T, Audio(f).Audio.VolumePoints[0].Tick);
        foreach (long tick in new[] { 2 * Fixture.T, 4 * Fixture.T, 7 * Fixture.T - 1 })
            Assert.AreEqual(before.Evaluate(tick).Value!.Audio.Single().Gain, trimmed.Evaluate(tick).Value!.Audio.Single().Gain, 1e-12);
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(NativeProjectCodec.Serialize(original).Value, NativeProjectCodec.Serialize(f.Project).Value);
    }
    [TestMethod]
    public void ExtremeSignedPointsInterpolateSafelyAndOverflowingSplitRollsBack()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(100), long.MinValue, 0)),
            new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(101), long.MaxValue, 1))).Success);
        using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(.5, evaluator.Evaluate(Fixture.T).Value!.Audio[0].Gain, 1e-12);
        var before = NativeProjectCodec.Serialize(f.Project).Value;
        Assert.IsFalse(f.Edit(new SplitClip(f.SequenceId, f.AudioClipId, 2 * Fixture.T, Fixture.Id(102))).Success);
        Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
    }
    [TestMethod]
    public async Task PCMEnvelopeIsSampleExactAndIndependentOfBlockPartition()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"); File.WriteAllBytes(path, []);
        try
        {
            var f = new Fixture(); AddRamp(f, Fixture.T);
            Assert.IsTrue(f.Edit(new RelinkMedia(f.WavId, path, 10 * Fixture.T, 48000, 2)).Success);
            using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
            var renderer = new SharedAudioRenderer(new ConstantDecoder());
            const long first = 48000 + 12000;
            var whole = await renderer.RenderAsync(evaluator, first, 4800, 48000, 2, default); Assert.IsTrue(whole.Success);
            var parts = new List<float>(); int done = 0;
            foreach (int size in new[] { 7, 103, 899, 3791 })
            { var part = await renderer.RenderAsync(evaluator, first + done, size, 48000, 2, default); Assert.IsTrue(part.Success); parts.AddRange(part.Value!.Samples); done += size; }
            CollectionAssert.AreEqual(whole.Value!.Samples.ToArray(), parts.ToArray());
            Assert.AreEqual(.25f * .25f, whole.Value.Samples[0], 1e-7);
            Assert.AreEqual(.25f * (float)((12000 + 4799d) / 48000), whole.Value.Samples[^1], 1e-7);
            Assert.AreNotEqual(whole.Value.Samples[0], whole.Value.Samples[^1]);
        }
        finally { File.Delete(path); }
    }
    [TestMethod]
    public void AudioCacheKeyDistinguishesEqualStartingGainWithDifferentCurveSlope()
    {
        var f = new Fixture(); AddRamp(f, Fixture.T);
        var first = PreviewContext.Create(f.Session.GetProject(), f.SequenceId).Value!;
        Assert.IsTrue(f.Edit(new UpdateClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(101), Fixture.T, .2))).Success);
        var second = PreviewContext.Create(f.Session.GetProject(), f.SequenceId).Value!;
        Assert.AreEqual(first.Evaluator.Evaluate(Fixture.T).Value!.Audio[0].Gain, second.Evaluator.Evaluate(Fixture.T).Value!.Audio[0].Gain);
        Assert.AreNotEqual(first.AudioKey(48000, 4800), second.AudioKey(48000, 4800));
    }
    private static Clip Audio(Fixture f) => f.Project.Sequences[0].Tracks[1].Clips.Single();
    [TestMethod]
    public void PointLimitAndClipScopedIdentitiesAreValidatedBeforeCommit()
    {
        var f = new Fixture(); var clip = Audio(f);
        var points = Enumerable.Range(100, 4096).Select(i => new VolumePoint(Fixture.Id(i), i, 1)).ToImmutableArray();
        var copy = clip with { Id = Fixture.Id(99), Audio = clip.Audio with { VolumePoints = points } };
        Assert.IsTrue(f.Edit(new InsertClip(f.SequenceId, f.AudioTrackId, copy)).Success);
        Assert.IsTrue(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, points[0])).Success, "Same point ID in another clip is scoped.");
        var before = NativeProjectCodec.Serialize(f.Project).Value;
        Assert.IsFalse(f.Edit(new AddClipVolumePoint(f.SequenceId, copy.Id, new(Fixture.Id(5000), 5000, 1))).Success);
        Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsFalse(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Guid.Empty, 0, 1))).Success);
    }
    [TestMethod]
    public async Task InteractivePreviewAndExportAgreeAcrossCacheReloadAndDifferentBlockSizes()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"); File.WriteAllBytes(path, []);
        try
        {
            var f = new Fixture(); AddRamp(f, Fixture.T); Assert.IsTrue(f.Edit(new RelinkMedia(f.WavId, path, 10 * Fixture.T, 48000, 2)).Success);
            using var source = new InteractivePreviewSource(forwardAudio: new ConstantDecoder());
            var context = PreviewContext.Create(f.Session.GetProject(), f.SequenceId).Value!;
            var backend = new AudioCaptureBackend();
            var export = new SnapshotExportService(new UnusedVideo(), new SharedAudioRenderer(new ConstantDecoder()), backend, new(null));
            Assert.AreEqual(ExportStage.Completed, (await export.ExportAsync(f.Session.GetProject(),
                new(Guid.NewGuid(), f.SequenceId, Path.GetFullPath("volume-envelope.mp4"), ExportPreset.YoutubeH264AacMp4), null, default)).Stage);
            var output = backend.Samples.ToArray();
            for (long first = 48000; first < 96000; first += 4800)
            {
                var preview = await source.AudioAsync(context, first, 4800, default); Assert.IsTrue(preview.Success);
                CollectionAssert.AreEqual(output.AsSpan((int)first * 2, 9600).ToArray(), preview.Value!.Samples.ToArray());
            }
            var cached = await source.AudioAsync(context, 48000, 4800, default); Assert.IsTrue(cached.Success); Assert.IsTrue(source.Audio.Statistics.Hits > 0);
            Assert.IsTrue(f.Edit(new UpdateClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(101), Fixture.T, .2))).Success);
            var reopened = NativeProjectCodec.Deserialize(NativeProjectCodec.Serialize(f.Project).Value!).Value!;
            using var reload = new EditorSession(); Assert.IsTrue(reload.ReplaceProject(reopened).Success);
            var fresh = PreviewContext.Create(reload.GetProject(), f.SequenceId).Value!;
            var changed = await source.AudioAsync(fresh, 48000, 4800, default); Assert.IsTrue(changed.Success);
            Assert.AreEqual(cached.Value!.Samples[0], changed.Value!.Samples[0]); Assert.AreNotEqual(cached.Value.Samples[^1], changed.Value.Samples[^1]);
        }
        finally { File.Delete(path); }
    }
    private sealed class UnusedVideo : IFrameRenderer
    {
        public ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long index, CancellationToken token) => throw new AssertFailedException();
    }
    private sealed class AudioCaptureBackend : IVideoEncodingBackend
    {
        public List<float> Samples { get; } = [];
        public async Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media, FfmpegSettings settings, IProgress<ExportProgress>? progress, CancellationToken token)
        { await foreach (var block in media.ReadAudioAsync(token)) Samples.AddRange(block.Samples); return new(request.JobId, ExportStage.Completed, request.OutputPath, []); }
    }
    [TestMethod]
    public void FadesPreserveUnrelatedKeysAreOneUndoAndPointPreviewIsTransient()
    {
        var f = new Fixture(); var middle = new VolumePoint(Fixture.Id(100), 3 * Fixture.T, .4);
        Assert.IsTrue(f.Edit(new AddClipVolumePoint(f.SequenceId, f.AudioClipId, middle),
            new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(101), Fixture.T / 2, .7))).Success);
        var baseline = f.Session.GetProject(); string before = NativeProjectCodec.Serialize(f.Project).Value!;
        Assert.IsTrue(f.Session.Execute(VolumeCurveEdits.Fade(baseline, f.SequenceId, f.AudioClipId, true)).Success);
        Assert.AreEqual(3, Audio(f).Audio.VolumePoints.Length); Assert.AreEqual(middle, Audio(f).Audio.VolumePoints.Single(p => p.Id == middle.Id));
        Assert.AreEqual(0d, Audio(f).Audio.VolumePoints[0].Multiplier); Assert.AreEqual(Fixture.T, Audio(f).Audio.VolumePoints[1].Tick);
        Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
        Assert.IsTrue(f.Session.Redo().Success);
        Assert.IsTrue(f.Session.Execute(VolumeCurveEdits.Fade(f.Session.GetProject(), f.SequenceId, f.AudioClipId, false)).Success);
        using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(.5, evaluator.Evaluate(6 * Fixture.T + Fixture.T / 2).Value!.Audio[0].Gain, 1e-12);
        baseline = f.Session.GetProject(); before = NativeProjectCodec.Serialize(f.Project).Value!;
        var edit = new VolumePointEdit(baseline, f.SequenceId, f.AudioClipId, middle);
        Assert.IsTrue(edit.Preview(.8).Success); Assert.AreEqual(baseline, f.Session.GetProject());
        Assert.IsTrue(f.Session.Execute(edit.Batch(.8)).Success); Assert.IsTrue(f.Session.Undo().Success);
        Assert.AreEqual(before, NativeProjectCodec.Serialize(f.Project).Value);
    }
    private static void AddRamp(Fixture f, long end) => Assert.IsTrue(f.Edit(
        new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(100), 0, 0)),
        new AddClipVolumePoint(f.SequenceId, f.AudioClipId, new(Fixture.Id(101), end, 1))).Success);
    private sealed class ConstantDecoder : IMediaDecoder
    {
        public Task<ImmutableArray<byte>> VideoAsync(string path, long source, int w, int h, CancellationToken token) => throw new AssertFailedException();
        public Task<ImmutableArray<float>> AudioAsync(string path, long source, int count, int rate, int channels, CancellationToken token) => Task.FromResult(Enumerable.Repeat(.25f, count * channels).ToImmutableArray());
    }
}
