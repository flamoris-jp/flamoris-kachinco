using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class ProductionRenderTests
{
    [TestMethod]
    public void ScreenBlackIsTransparentAndTransformUsesPixelCenters()
    {
        byte[] output = [40, 80, 120, 255, 0, 0, 0, 255];
        SharedFrameRenderer.Composite(output, [0, 0, 0, 255, 255, 0, 0, 255], 2, 1,
            ClipAppearance.Default with { Blend = BlendMode.Screen });
        CollectionAssert.AreEqual(new byte[] {40,80,120,255,255,0,0,255}, output);
        output = [0,0,0,255,0,0,0,255];
        SharedFrameRenderer.Composite(output, [255, 0, 0, 255, 0, 255, 0, 255], 2, 1,
            ClipAppearance.Default with { Transform = new(1,0,1,1,0) });
        CollectionAssert.AreEqual(new byte[] {0,0,0,255,255,0,0,255}, output);
    }

    [TestMethod]
    public async Task MissingSourcesFailInsteadOfRenderingSuccess()
    {
        var f = new Fixture();
        var frame = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(0).Value!;
        var result = await new SharedFrameRenderer(new UnusedDecoder()).RenderAsync(f.Project, frame, 0, default);
        Assert.IsFalse(result.Success);
        Assert.AreEqual("MEDIA_MISSING", result.Diagnostics[0].Code);
    }

    [TestMethod]
    public async Task ExportRejectsSourceOverwriteBeforeRendererRuns()
    {
        var f = new Fixture();
        var service = new SnapshotExportService(new SharedFrameRenderer(new UnusedDecoder()),
            new SharedAudioRenderer(new UnusedDecoder()), new FfmpegEncodingBackend(), new(null), Path.GetFullPath("project.fkproj"));
        var result = await service.ExportAsync(f.Session.GetProject(),
            new(Guid.NewGuid(), f.SequenceId, Path.GetFullPath("input.mov"), ExportPreset.YoutubeH264AacMp4), null, default);
        Assert.AreEqual("OUTPUT_IS_INPUT", result.Diagnostics[0].Code);
    }

    [TestMethod]
    public async Task SilenceIsSampleExactAcrossFinalPartialBlock()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Edit(new SetTrackEnabled(f.SequenceId, f.AudioTrackId, false)).Success);
        var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        var result = await new SharedAudioRenderer(new UnusedDecoder()).RenderAsync(evaluator, 383999, 1, 48000, 2, default);
        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new float[] {0,0}, result.Value!.Samples.ToArray());
        Assert.AreEqual(383999L, result.Value.FirstSample);
    }

    private sealed class UnusedDecoder : IMediaDecoder
    {
        public Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token) => throw new AssertFailedException("Decoder must not run.");
        public Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token) => throw new AssertFailedException("Decoder must not run.");
    }
}
