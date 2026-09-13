using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class EvaluationTests
{
    [TestMethod]
    public void HalfOpenActivitySourceMappingAndCaptionsAreShared()
    {
        var f = new Fixture(); var e = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(Fixture.T, e.Evaluate(0).Value!.VideoLayers[0].SourceTicks);
        Assert.AreEqual(0, e.Evaluate(0).Value!.Audio.Length);
        var first = e.Evaluate(Fixture.T).Value!;
        Assert.AreEqual(2*Fixture.T, first.Audio[0].SourceTicks); Assert.AreEqual(1, first.Captions.Length);
        Assert.AreEqual(0, e.Evaluate(3*Fixture.T).Value!.Captions.Length);
        Assert.AreEqual(0, e.Evaluate(7*Fixture.T).Value!.Audio.Length);
        Assert.IsFalse(e.Evaluate(8*Fixture.T).Success); Assert.IsFalse(e.Evaluate(-1).Success);
    }
    [TestMethod]
    public void DisabledTracksClipsAndMutedAudioDoNotContribute()
    {
        var f = new Fixture();
        f.Edit(new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false),
            new SetClipProperties(f.SequenceId, f.AudioClipId, true, ClipAppearance.Default, new(1, true)));
        var frame = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(Fixture.T).Value!;
        Assert.AreEqual(0, frame.VideoLayers.Length); Assert.AreEqual(0, frame.Audio.Length);
    }
    [TestMethod]
    public void AudioRangeIntersectionsUseSourceMappingAtSampleIndependentTimes()
    {
        var f = new Fixture(); var e = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        var parts = e.EvaluateAudioRange(Fixture.T / 2, 2*Fixture.T).Value;
        Assert.AreEqual(1, parts.Length);
        Assert.AreEqual(Fixture.T, parts[0].TimelineStartTicks);
        Assert.AreEqual(2*Fixture.T, parts[0].SourceStartTicks);
        Assert.AreEqual(Fixture.T + Fixture.T/2, parts[0].DurationTicks);
        Assert.AreEqual(0, e.EvaluateAudioRange(7*Fixture.T, Fixture.T).Value.Length);
        Assert.IsFalse(e.EvaluateAudioRange(long.MaxValue, 1).Success);
    }
    [TestMethod]
    public async Task PreviewConsumesExactlyTheExportEvaluatedFrame()
    {
        var f = new Fixture(); var plan = ExportPlanner.Create(f.Project, f.SequenceId).Value!;
        var renderer = new RecordingRenderer(); var preview = new PreviewService(renderer);
        foreach (long index in new[] { 0L, 30, plan.FrameCount - 1 })
        {
            var expected = plan.EvaluateFrame(index).Value!;
            Assert.IsTrue((await preview.RenderAsync(plan, index)).Success);
            Assert.AreEqual(expected.Tick, renderer.Seen!.Tick);
            CollectionAssert.AreEqual(expected.VideoLayers.ToArray(), renderer.Seen.VideoLayers.ToArray());
            CollectionAssert.AreEqual(expected.Audio.ToArray(), renderer.Seen.Audio.ToArray());
            CollectionAssert.AreEqual(expected.Captions.ToArray(), renderer.Seen.Captions.ToArray());
        }
        Assert.AreEqual(240L, plan.FrameCount); Assert.AreEqual(384000L, plan.AudioSampleCount);
        Assert.IsFalse(plan.EvaluateFrame(plan.FrameCount).Success);
    }
    [TestMethod]
    public void ExportSnapshotIsUnaffectedByLaterEditingAndSupportsPortrait()
    {
        var f = new Fixture(); var plan = ExportPlanner.Create(f.Project, f.SequenceId).Value!;
        f.Edit(new DeleteClip(f.SequenceId, f.ClipId));
        Assert.AreEqual(1, plan.EvaluateFrame(0).Value!.VideoLayers.Length);
        var portrait = Fixture.Id(11);
        Assert.IsTrue(f.Edit(new CreateSequence(portrait, "Shorts", SequenceSettings.Portrait with { FrameRate = new(30000, 1001) }, 8*Fixture.T)).Success);
        var portraitPlan = ExportPlanner.Create(f.Project, portrait).Value!;
        Assert.AreEqual(1080, portraitPlan.EvaluateFrame(0).Value!.Settings.Width);
        Assert.AreEqual(1920, portraitPlan.EvaluateFrame(0).Value!.Settings.Height);
    }
    [TestMethod]
    public void LayerOrderIsTrackThenStartThenStableId()
    {
        var f = new Fixture(); Guid topTrack = Fixture.Id(11);
        f.Edit(new AddTrack(f.SequenceId, topTrack, "V2", TrackKind.Video),
            new InsertClip(f.SequenceId, topTrack, Fixture.Clip(Fixture.Id(20), f.MovId, 0, 0, Fixture.T)),
            new InsertClip(f.SequenceId, topTrack, Fixture.Clip(Fixture.Id(12), f.MovId, 0, 0, Fixture.T)));
        var layers = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(0).Value!.VideoLayers;
        CollectionAssert.AreEqual(new[] { f.ClipId, Fixture.Id(12), Fixture.Id(20) }, layers.Select(x => x.ClipId).ToArray());
        f.Edit(new ReorderTrack(f.SequenceId, topTrack, 0));
        Assert.AreEqual(Fixture.Id(12), TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(0).Value!.VideoLayers[0].ClipId);
    }
    [TestMethod]
    public void ScreenBlackPreservesBackdropAndWhiteLightensIt()
    {
        var background = new Rgba(0.2, 0.4, 0.6, 1);
        var black = BlendReference.Composite(background, new(0, 0, 0, 1), BlendMode.Screen);
        Assert.AreEqual(background.R, black.R, 1e-12); Assert.AreEqual(background.G, black.G, 1e-12);
        Assert.AreEqual(new Rgba(1, 1, 1, 1), BlendReference.Composite(background, new(1, 1, 1, 1), BlendMode.Screen));
        Assert.AreEqual(background, BlendReference.Composite(background, new(1, 0, 0, 1), BlendMode.Normal, 0));
    }
    [TestMethod]
    public void AlphaAndOpacityApplyOnceWithTransparentInputs()
    {
        var result = BlendReference.Composite(new(0, 0, 0, 0), new(1, 0.5, 0, 0.5), BlendMode.Screen, 0.5);
        Assert.AreEqual(new Rgba(1, 0.5, 0, 0.25), result);
        Assert.AreEqual(new Rgba(0, 0, 0, 0), BlendReference.Composite(new(1, 1, 1, 0), new(1, 0, 0, 0), BlendMode.Normal));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BlendReference.Composite(new(0, 0, 0, 1), new(double.NaN, 0, 0, 1), BlendMode.Normal));
    }
    [TestMethod]
    public async Task FfmpegBoundaryDoesNotPretendToExportOrConsumeMedia()
    {
        var request = new EncodingRequest(Guid.NewGuid(), "must-not-exist.mp4", ExportPreset.YoutubeH264AacMp4,
            SequenceSettings.Landscape, 240, 48000, 2, 384000);
        var backend = new FfmpegEncodingBackend();
        var result = await backend.EncodeAsync(request, new NeverReadMedia(), new(null), null, default);
        Assert.AreEqual(ExportStage.Failed, result.Stage); Assert.IsNull(result.OutputPath);
        Assert.AreEqual("EXPORT_NOT_IMPLEMENTED", result.Diagnostics[0].Code);
        using var token = new CancellationTokenSource(); token.Cancel();
        Assert.AreEqual(ExportStage.Cancelled, (await backend.EncodeAsync(request, new NeverReadMedia(), new(null), null, token.Token)).Stage);
    }
    private sealed class RecordingRenderer : IFrameRenderer
    {
        public EvaluatedFrame? Seen { get; private set; }
        public ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long frameIndex, CancellationToken cancellationToken)
        {
            Seen = frame;
            // This double records the boundary only; it deliberately does not claim image pixels.
            return ValueTask.FromResult(Result<RenderedVideoFrame>.Ok(new(frameIndex, frame.Tick, frame.Settings.Width, frame.Settings.Height, [])));
        }
    }
    private sealed class NeverReadMedia : IRenderedMediaSource
    {
        public IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync(CancellationToken cancellationToken) => throw new AssertFailedException("Foundation must not render/encode.");
        public IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync(CancellationToken cancellationToken) => throw new AssertFailedException("Foundation must not render/encode.");
    }
}
