using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PreviewBackendTests
{
    [TestMethod]
    [DataRow("begin")]
    [DataRow("compose")]
    [DataRow("read")]
    [DataRow("bad-read")]
    public async Task GpuFailureReplaysWholeFrameExactlyOnceWithoutRedecoding(string failure)
    {
        using var input = new Inputs();
        var decoder = new Decoder(); var gpu = new Compositor { Failure = failure };
        using var backend = new PreviewRenderBackend(PreviewBackendPreference.D3D11, Factory(gpu));
        var frame = input.LayeredFrame();
        var expected = await new SharedFrameRenderer(new Decoder()).RenderAsync(input.Project, frame, 3, default);
        var result = await new SharedFrameRenderer(decoder, previewBackend: backend).RenderAsync(input.Project, frame, 3, default);
        Assert.IsTrue(result.Success, string.Join(",", result.Diagnostics));
        CollectionAssert.AreEqual(expected.Value!.Rgba8.ToArray(), result.Value!.Rgba8.ToArray());
        Assert.AreEqual(2, decoder.VideoCalls);
        Assert.AreEqual("CPU", backend.Diagnostics.Active);
        Assert.IsNotNull(backend.Diagnostics.FallbackReason);
        Assert.IsTrue(gpu.Disposed);
        Assert.AreEqual(0UL, backend.Diagnostics.AllocatedBytes);
        await backend.ResetAsync();
        var second = await new SharedFrameRenderer(decoder, previewBackend: backend).RenderAsync(input.Project, frame, 4, default);
        Assert.IsTrue(second.Success); Assert.AreEqual(1, gpu.BeginCalls, "Seek/reset must preserve the sticky GPU downgrade.");
    }

    [TestMethod]
    public async Task SuccessfulCompositorUsesSamePrimitivesCaptionsAndImmutableCpuFrame()
    {
        using var input = new Inputs(); var decoder = new Decoder(); var gpu = new Compositor();
        using var backend = new PreviewRenderBackend(PreviewBackendPreference.D3D11, Factory(gpu));
        var frame = input.LayeredFrame() with { Captions = [new(Guid.NewGuid(), Guid.NewGuid(), "caption")] };
        var captions = new Captions();
        var expected = await new SharedFrameRenderer(new Decoder(), captions: captions).RenderAsync(input.Project, frame, 9, default);
        var rendered = await new SharedFrameRenderer(decoder, captions: captions, previewBackend: backend).RenderAsync(input.Project, frame, 9, default);
        Assert.IsTrue(rendered.Success); Assert.AreEqual("D3D11", backend.Diagnostics.Active);
        Assert.AreEqual(3, gpu.CompositeCalls); Assert.AreEqual(1, gpu.ReadCalls);
        CollectionAssert.AreEqual(expected.Value!.Rgba8.ToArray(), rendered.Value!.Rgba8.ToArray());
        var retained = rendered.Value.Rgba8.ToArray();
        await backend.ResetAsync();
        CollectionAssert.AreEqual(retained, rendered.Value.Rgba8.ToArray(), "Cached/presented frames do not retain GPU textures.");
        Assert.AreEqual(0UL, backend.Diagnostics.AllocatedBytes);
    }

    [TestMethod]
    public async Task AutoKeepsNativeCpuIdentityFastPathWhileExplicitD3D11CanCompareIt()
    {
        using var input = new Inputs(); var gpu = new Compositor();
        using var backend = new PreviewRenderBackend(PreviewBackendPreference.Auto, Factory(gpu));
        var renderer = new SharedFrameRenderer(new Decoder(), previewBackend: backend);
        Assert.IsTrue((await renderer.RenderAsync(input.Project, input.Frame, 0, default)).Success);
        Assert.AreEqual(0, gpu.BeginCalls); Assert.AreEqual("CPU", backend.Diagnostics.Active);
        await backend.SelectAsync(PreviewBackendPreference.D3D11);
        Assert.IsTrue((await renderer.RenderAsync(input.Project, input.Frame, 0, default)).Success);
        Assert.AreEqual(1, gpu.BeginCalls); Assert.AreEqual("D3D11", backend.Diagnostics.Active);
        await backend.SelectAsync(PreviewBackendPreference.Cpu);
        Assert.IsTrue(gpu.Disposed);
        Assert.IsTrue((await renderer.RenderAsync(input.Project, input.Frame, 0, default)).Success);
        Assert.AreEqual(1, gpu.BeginCalls);
    }

    [TestMethod]
    public async Task ResetAndDisposeFailuresKeepCpuRecoveryAvailable()
    {
        using var input = new Inputs(); var gpu = new Compositor { Failure = "reset-dispose" };
        using var backend = new PreviewRenderBackend(PreviewBackendPreference.D3D11, Factory(gpu));
        var renderer = new SharedFrameRenderer(new Decoder(), previewBackend: backend);
        Assert.IsTrue((await renderer.RenderAsync(input.Project, input.Frame, 0, default)).Success);
        await backend.ResetAsync();
        Assert.IsTrue(gpu.Disposed); Assert.AreEqual("CPU", backend.Diagnostics.Active);
        await backend.SelectAsync(PreviewBackendPreference.Cpu);
        Assert.IsTrue((await renderer.RenderAsync(input.Project, input.Frame, 0, default)).Success);
        backend.Dispose(); backend.Dispose();
    }

    [TestMethod]
    public async Task BackendSelectionJoinsCanceledDecodeBeforeResetAndCanceledFramesNeverCache()
    {
        using var input = new Inputs(); var gpu = new Compositor(); var decoder = new Decoder { Hold = true };
        using var source = new InteractivePreviewSource(randomDecoder: decoder, forwardVideo: decoder, forwardAudio: decoder,
            backendPreference: PreviewBackendPreference.D3D11, compositorFactory: Factory(gpu));
        var context = PreviewContext.Create(input.Fixture.Session.GetProject(), input.Fixture.SequenceId).Value!;
        using var cancellation = new CancellationTokenSource();
        var rendering = source.FrameAsync(context, 0, PreviewQuality.Quarter, false, cancellation.Token).AsTask();
        await decoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selection = source.SelectBackendAsync(PreviewBackendPreference.Cpu).AsTask();
        Assert.IsFalse(selection.IsCompleted, "Selection must wait for the frame's async decode to join.");
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await rendering);
        await selection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(gpu.ResetCalls > 0); Assert.IsTrue(gpu.Disposed);
        Assert.AreEqual(0, source.Frames.Statistics.Entries);
        decoder.Hold = false;
        Assert.IsTrue((await source.FrameAsync(context, 0, PreviewQuality.Quarter, false, default)).Success);
        Assert.AreEqual("CPU", source.BackendDiagnostics.Active);
    }

    [TestMethod]
    public async Task RetainedInputLimitSelectsStreamingCpuBeforeGpuFactoryRuns()
    {
        var fixture = new Fixture(); var frame = TimelineEvaluator.Create(fixture.Project, fixture.SequenceId).Value!.Evaluate(0).Value!;
        var gpu = new Compositor(); using var backend = new PreviewRenderBackend(PreviewBackendPreference.D3D11, Factory(gpu));
        frame = frame with { Settings = frame.Settings with { Width = 4096, Height = 4096 }, VideoLayers = [frame.VideoLayers[0], frame.VideoLayers[0], frame.VideoLayers[0], frame.VideoLayers[0], frame.VideoLayers[0]] };
        var result = await new SharedFrameRenderer(new Decoder(), previewBackend: backend).RenderAsync(fixture.Project, frame, 0, default);
        Assert.IsFalse(result.Success); Assert.AreEqual("MEDIA_MISSING", result.Diagnostics[0].Code);
        Assert.AreEqual(0, gpu.BeginCalls); StringAssert.Contains(backend.Diagnostics.FallbackReason!, "bounded");
        fixture.Session.Dispose();
    }

    private static PreviewCompositorFactory Factory(Compositor gpu) => (out IPreviewFrameCompositor? compositor, out string reason) =>
    { compositor = gpu; reason = ""; return true; };
    private sealed class Inputs : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kachinco-gpu-host-" + Guid.NewGuid().ToString("N"));
        public Fixture Fixture { get; } = new();
        public Project Project => Fixture.Project;
        public EvaluatedFrame Frame { get; }
        public Inputs()
        {
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "source.mov"); File.WriteAllBytes(path, []);
            Assert.IsTrue(Fixture.Edit(new RelinkMedia(Fixture.MovId, path, 10 * Fixture.T)).Success);
            Frame = TimelineEvaluator.Create(Project, Fixture.SequenceId).Value!.Evaluate(0).Value!;
            Frame = Frame with { Settings = Frame.Settings with { Width = 2, Height = 2 } };
        }
        public EvaluatedFrame LayeredFrame() => Frame with { VideoLayers = [Frame.VideoLayers[0], Frame.VideoLayers[0] with
        { Appearance = ClipAppearance.Default with { Opacity = .5, Blend = BlendMode.Screen, Transform = new(1, 0, 1, 1, 0) } }] };
        public void Dispose() { Fixture.Session.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class Decoder : IMediaDecoder
    {
        public int VideoCalls; public bool Hold;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImmutableArray<byte>> VideoAsync(string path, long tick, int width, int height, CancellationToken token)
        {
            VideoCalls++; Entered.TrySetResult();
            if (Hold) await Task.Delay(Timeout.Infinite, token);
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 160; pixels[i + 1] = 40; pixels[i + 2] = 80; pixels[i + 3] = 192; }
            return [.. pixels];
        }
        public Task<ImmutableArray<float>> AudioAsync(string path, long tick, int count, int rate, int channels, CancellationToken token) => Task.FromResult(ImmutableArray.CreateRange(new float[count * channels]));
    }
    private sealed class Captions : ICaptionRasterizer
    {
        public ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token) =>
            ValueTask.FromResult(ImmutableArray.Create<byte>(0, 200, 50, 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }
    private sealed class Compositor : IPreviewFrameCompositor
    {
        private byte[] output = []; private int width, height;
        public string? Failure; public int BeginCalls, CompositeCalls, ReadCalls, ResetCalls; public bool Disposed;
        public ulong AllocatedBytes => (ulong)output.Length;
        public void Begin(int width, int height)
        {
            BeginCalls++; if (Failure == "begin") throw new InvalidOperationException("GPU begin failure");
            this.width = width; this.height = height; output = new byte[width * height * 4];
            for (int i = 3; i < output.Length; i += 4) output[i] = 255;
        }
        public void Composite(ReadOnlySpan<byte> pixels, NativeAppearance appearance)
        {
            CompositeCalls++;
            if (Failure == "compose" && CompositeCalls == 2) throw new InvalidOperationException("GPU composite failure");
            NativeComposition.Composite(output, pixels, width, height, appearance, default);
        }
        public byte[] Read() { ReadCalls++; if (Failure == "read") throw new InvalidOperationException("GPU read failure"); return Failure == "bad-read" ? [] : output.ToArray(); }
        public void Reset() { ResetCalls++; output = []; if (Failure == "reset-dispose") throw new InvalidOperationException("GPU reset failure"); }
        public void Dispose() { Disposed = true; output = []; if (Failure == "reset-dispose") throw new InvalidOperationException("GPU dispose failure"); }
    }
}
