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
    public async Task MalformedDecoderOutputBecomesStructuredRenderDiagnostics()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kachinco-render-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string mov = Path.Combine(folder, "input.mov"), wav = Path.Combine(folder, "input.wav");
            File.WriteAllBytes(mov, []); File.WriteAllBytes(wav, []);
            var f = new Fixture();
            Assert.IsTrue(f.Edit(new RelinkMedia(f.MovId, mov, 10 * Fixture.T),
                new RelinkMedia(f.WavId, wav, 10 * Fixture.T, 48000, 2)).Success);
            var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
            var frame = evaluator.Evaluate(0).Value!;
            var video = await new SharedFrameRenderer(new MalformedDecoder()).RenderAsync(f.Project, frame, 0, default);
            var audio = await new SharedAudioRenderer(new MalformedDecoder()).RenderAsync(evaluator, 48000, 1, 48000, 2, default);
            Assert.AreEqual("FRAME_RENDER_FAILED", video.Diagnostics[0].Code);
            Assert.AreEqual("AUDIO_RENDER_FAILED", audio.Diagnostics[0].Code);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task RendererFailureDuringExportBecomesStructuredExportDiagnostic()
    {
        var f = new Fixture();
        var service = new SnapshotExportService(new FailingFrameRenderer(), new SharedAudioRenderer(new UnusedDecoder()),
            new EnumeratingBackend(), new(null));
        var result = await service.ExportAsync(f.Session.GetProject(),
            new(Guid.NewGuid(), f.SequenceId, Path.GetFullPath("render-failure.mp4"), ExportPreset.YoutubeH264AacMp4), null, default);
        Assert.AreEqual(ExportStage.Failed, result.Stage);
        Assert.AreEqual("EXPORT_FAILED", result.Diagnostics[0].Code);
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

    private sealed class MalformedDecoder : IMediaDecoder
    {
        public Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token) =>
            Task.FromResult(ImmutableArray.Create<byte>(0));
        public Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token) =>
            throw new InvalidDataException("Decoder returned partial PCM samples.");
    }

    private sealed class FailingFrameRenderer : IFrameRenderer
    {
        public ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long frameIndex, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<RenderedVideoFrame>.Fail(Diagnostic.Error("FRAME_RENDER_FAILED", "Malformed decoder output.")));
    }

    private sealed class EnumeratingBackend : IVideoEncodingBackend
    {
        public async Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media, FfmpegSettings settings,
            IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
        {
            await foreach (var _ in media.ReadVideoAsync(cancellationToken)) { }
            return new(request.JobId, ExportStage.Completed, request.OutputPath, []);
        }
    }
}
