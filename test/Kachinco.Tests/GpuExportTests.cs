using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class GpuExportTests
{
    [TestMethod]
    [DataRow("initialize")]
    [DataRow("midstream")]
    [DataRow("success")]
    public async Task HardwareProcessSelectionRecoveryAndActualCpuEncodedOutput(string mode)
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("POSIX injected executable; real Windows codec/WARP tests run separately."); return; }
        using var fixture = new EncoderFixture(mode);
        var source = new Frames();
        var result = await new FfmpegEncodingBackend().EncodeAsync(fixture.Request, source, new(fixture.Executable, VideoEncoderPreference.Nvenc, 0), null, default);
        Assert.AreEqual(ExportStage.Completed, result.Stage, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(result.Metrics);
        Assert.AreEqual(mode == "success" ? "h264_nvenc" : "libx264", result.Metrics.VideoEncoder);
        Assert.AreEqual(mode == "midstream" ? 2 : 1, source.Enumerations);
        Assert.IsTrue(mode == "midstream" ? source.FramesProduced is >= 4 and <= 6 : source.FramesProduced == 3);
        Assert.IsTrue(result.Metrics.TotalMilliseconds >= result.Metrics.VideoPipelineMilliseconds);
        Assert.IsTrue(result.Metrics.VideoPipelineMilliseconds >= result.Metrics.VideoRenderMilliseconds);
        Assert.AreEqual(mode != "success", result.Diagnostics.Any(d => d.Code == "ENCODER_FALLBACK"));
        var calls = File.ReadAllLines(fixture.Calls).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
        Assert.AreEqual(1, calls.Count(args => args.Contains("lavfi")), "One actual-initialization probe only.");
        Assert.AreEqual(mode == "initialize" ? 0 : 1, calls.Count(args => args.Contains("rawvideo") && args.Contains("h264_nvenc")));
        Assert.AreEqual(mode == "success" ? 0 : 1, calls.Count(args => args.Contains("rawvideo") && args.Contains("libx264")));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Directory).Length);
        string probe = await Run("ffprobe", ["-v", "error", "-count_frames", "-show_entries", "stream=codec_name,nb_read_frames,width,height", "-of", "json", fixture.Request.OutputPath]);
        using var doc = JsonDocument.Parse(probe);
        var stream = doc.RootElement.GetProperty("streams")[0];
        Assert.AreEqual("h264", stream.GetProperty("codec_name").GetString());
        Assert.AreEqual("3", stream.GetProperty("nb_read_frames").GetString());
        Assert.AreEqual(480, stream.GetProperty("width").GetInt32());
    }

    [TestMethod]
    public async Task CancelAtCpuRetryProtectsDestinationAndDoesNotEnumerateAgain()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("POSIX injected executable."); return; }
        using var fixture = new EncoderFixture("midstream"); using var cancellation = new CancellationTokenSource();
        File.WriteAllText(fixture.Request.OutputPath, "keep");
        var source = new Frames();
        var result = await new FfmpegEncodingBackend().EncodeAsync(fixture.Request, source, new(fixture.Executable, VideoEncoderPreference.Nvenc),
            new OnProgress(value => { if (value.FramesCompleted == 0) cancellation.Cancel(); }), cancellation.Token);
        Assert.AreEqual(ExportStage.Cancelled, result.Stage);
        Assert.AreEqual("keep", File.ReadAllText(fixture.Request.OutputPath));
        Assert.AreEqual(1, source.Enumerations); Assert.AreEqual(0, Directory.GetDirectories(fixture.Directory).Length);
    }

    [TestMethod]
    public async Task RendererValidationFailureDoesNotTriggerHardwareReplay()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("POSIX injected executable."); return; }
        using var fixture = new EncoderFixture("success");
        File.WriteAllText(fixture.Request.OutputPath, "keep"); var source = new Frames { Invalid = true };
        var result = await new FfmpegEncodingBackend().EncodeAsync(fixture.Request, source, new(fixture.Executable, VideoEncoderPreference.Nvenc), null, default);
        Assert.AreEqual(ExportStage.Failed, result.Stage); Assert.AreEqual(1, source.Enumerations);
        Assert.AreEqual("keep", File.ReadAllText(fixture.Request.OutputPath));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Directory).Length);
        Assert.IsFalse(File.ReadAllText(fixture.Calls).Contains("libx264"));
    }

    [TestMethod]
    public async Task ExplicitCpuBypassesHardwareProbeAndInvalidPreferenceStartsNothing()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("POSIX injected executable."); return; }
        using var fixture = new EncoderFixture("initialize");
        var result = await new FfmpegEncodingBackend().EncodeAsync(fixture.Request, new Frames(), new(fixture.Executable, VideoEncoderPreference.Cpu), null, default);
        Assert.AreEqual(ExportStage.Completed, result.Stage);
        Assert.AreEqual(1, File.ReadAllLines(fixture.Calls).Length);
        Assert.IsFalse(File.ReadAllText(fixture.Calls).Contains("h264_nvenc"));
        result = await new FfmpegEncodingBackend().EncodeAsync(fixture.Request, new Frames(), new(fixture.Executable, (VideoEncoderPreference)99), null, default);
        Assert.AreEqual("INVALID_ENCODING_REQUEST", result.Diagnostics[0].Code);
        Assert.AreEqual(1, File.ReadAllLines(fixture.Calls).Length);
    }

    [TestMethod]
    public async Task HardwareAndCpuFailureHaveOnlyTwoAttemptsAndKeepExistingOutput()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("POSIX injected executable."); return; }
        using var fixture = new EncoderFixture("bothfail");
        File.WriteAllText(fixture.Request.OutputPath, "keep");
        var result = await new FfmpegEncodingBackend().EncodeAsync(fixture.Request, new Frames(), new(fixture.Executable, VideoEncoderPreference.Nvenc), null, default);
        Assert.AreEqual(ExportStage.Failed, result.Stage);
        Assert.AreEqual(2, File.ReadAllLines(fixture.Calls).Count(line => line.Contains("rawvideo")));
        Assert.AreEqual("keep", File.ReadAllText(fixture.Request.OutputPath));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Directory).Length);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "ENCODER_FALLBACK"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializationProbeHasDeadlineAndCancellationNeverStartsVideo(bool cancel)
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("POSIX injected executable."); return; }
        using var fixture = new EncoderFixture("hangprobe"); using var cancellation = new CancellationTokenSource();
        var source = new Frames(); var watch = Stopwatch.StartNew();
        var pending = new FfmpegEncodingBackend().EncodeAsync(fixture.Request, source, new(fixture.Executable, VideoEncoderPreference.Nvenc), null, cancellation.Token);
        if (cancel)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(fixture.Calls)) await Task.Delay(10, timeout.Token);
            }
            finally { cancellation.Cancel(); }
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(cancel ? ExportStage.Cancelled : ExportStage.Completed, result.Stage);
        Assert.AreEqual(cancel ? 0 : 1, source.Enumerations);
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Directory).Length);
        if (!cancel) Assert.IsTrue(result.Metrics!.EncoderFallbackReason!.Contains("timed out"));
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task ExportServiceUsesSharedGpuLayersCaptionsSpeedAndStickySameFrameCpuRecovery(bool failRead, bool warp)
    {
        if (warp && !OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows WARP export parity."); return; }
        string directory = Path.Combine(Path.GetTempPath(), "export-composition-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var f = new Fixture(); using var session = f.Session; string input = Path.Combine(directory, "input.mov"); File.WriteAllBytes(input, []);
            Assert.IsTrue(f.Edit(new RelinkMedia(f.MovId, input, 20 * Fixture.T),
                new SetTrackEnabled(f.SequenceId, f.AudioTrackId, false),
                new SetClipProperties(f.SequenceId, f.ClipId, true, ClipAppearance.Default with { Opacity = .6, Blend = BlendMode.Screen, Transform = new(.5, -.5, 1.1, .9, 13) }, AudioProperties.Default),
                new TrimClip(f.SequenceId, f.ClipId, 0, 0, Fixture.T / 5),
                new SetClipSpeed(f.SequenceId, f.ClipId, 2)).Success);
            var project = f.Project with { Sequences = [f.Project.Sequences[0] with { DurationTicks = Fixture.T / 10, Tracks = [f.Project.Sequences[0].Tracks[0], f.Project.Sequences[0].Tracks[2] with { Captions = [new(f.CaptionId, 0, Fixture.T / 10, "caption")] }] }] };
            Assert.IsTrue(f.Session.ReplaceProject(project).Success);
            var gpu = new CpuOracleCompositor(failRead); var decoder = new Decoder(); var captions = new Captions();
            bool Factory(out IPreviewFrameCompositor? compositor, out string reason)
            {
                if (warp) { bool ready = NativeGpuCompositor.TryCreate(out var native, out reason, forceWarp: true); compositor = ready ? new Warp(native!) : null; Assert.IsTrue(ready, reason); return ready; }
                compositor = gpu; reason = "test"; return true;
            }
            using var renderer = new ExportFrameRenderer(decoder, captions: captions, preference: PreviewBackendPreference.D3D11, compositorFactory: Factory);
            using var evaluator = TimelineEvaluator.Create(project, f.SequenceId).Value!;
            var expectedRenderer = new SharedFrameRenderer(new Decoder(), captions: captions);
            var capture = new CaptureBackend(evaluator, expectedRenderer);
            var result = await new SnapshotExportService(renderer, new SharedAudioRenderer(decoder), capture, new(null))
                .ExportAsync(f.Session.GetProject(), new(Guid.NewGuid(), f.SequenceId, Path.Combine(directory, "out.mp4"), ExportPreset.YoutubeH264AacMp4), null, default);
            Assert.AreEqual(ExportStage.Completed, result.Stage);
            Assert.AreEqual(3, capture.Frames); Assert.AreEqual(4800L, capture.Samples);
            Assert.AreEqual(failRead ? 0L : 3L, result.Metrics!.Composition!.GpuFrames);
            Assert.AreEqual(failRead ? 3L : 0L, result.Metrics.Composition.CpuFrames);
            if (!warp) Assert.AreEqual(failRead ? 1 : 3, gpu.Begins, "Device failure is sticky for this whole export.");
            Assert.AreEqual(3, decoder.Calls, "Same-frame fallback never redecodes.");
            Assert.AreEqual(TimelineTime.FrameToTicks(2, new(30, 1)) * 2, decoder.Ticks.Last());
            renderer.Dispose(); if (!warp) Assert.IsTrue(gpu.Disposed);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Warp(NativeGpuCompositor native) : IPreviewFrameCompositor
    {
        public ulong AllocatedBytes => native.AllocatedBytes;
        public void Begin(int width, int height) => native.Begin(width, height);
        public void Composite(ReadOnlySpan<byte> pixels, NativeAppearance appearance) => native.Composite(pixels, appearance);
        public byte[] Read() => native.Read();
        public void Reset() => native.Reset();
        public void Dispose() => native.Dispose();
    }

    private sealed class Frames : IRenderedMediaSource
    {
        public int Enumerations, FramesProduced; public bool Invalid;
        public async IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync([EnumeratorCancellation] CancellationToken token)
        {
            Enumerations++;
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested(); FramesProduced++;
                var rgba = new byte[480 * 270 * 4]; for (int n = 0; n < rgba.Length; n += 4) { rgba[n] = 200; rgba[n + 3] = 255; }
                yield return new(i, TimelineTime.FrameToTicks(i, new(30, 1)), 480, 270, Invalid ? [1] : [.. rgba]);
                await Task.Yield();
            }
        }
        public IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync(CancellationToken token) => throw new AssertFailedException("Silent clip output must not render audio.");
    }
    private sealed class OnProgress(Action<ExportProgress> action) : IProgress<ExportProgress> { public void Report(ExportProgress value) => action(value); }
    private sealed class EncoderFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "gpu-encoder-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Directory, "ffmpeg-test");
        public string Calls => Path.Combine(Directory, "calls.jsonl");
        public EncodingRequest Request => new(Guid.NewGuid(), Path.Combine(Directory, "out.mp4"), ExportPreset.ClipPreviewH264Mp4, new(480, 270, new(30, 1)), 3, 48000, 2, 4800);
        public EncoderFixture(string mode)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Executable, "#!/usr/bin/python3\nimport sys,json,os\na=sys.argv[1:]\nwith open(" + JsonSerializer.Serialize(Calls) + ", 'a') as f: f.write(json.dumps(a)+'\\n')\n" +
                "mode=" + JsonSerializer.Serialize(mode) + "\n" +
                "if mode=='hangprobe' and 'lavfi' in a:\n import time;time.sleep(30)\n" +
                "if mode=='bothfail' and 'libx264' in a: sys.exit(21)\n" +
                "if 'h264_nvenc' in a:\n" +
                " if 'lavfi' in a:\n  sys.exit(1 if mode=='initialize' else 0)\n" +
                " if mode in ['midstream','bothfail']:\n  sys.stdin.buffer.read(480*270*4)\n  open(a[-1],'wb').write(b'partial')\n  sys.exit(12)\n" +
                " drop=['-gpu','-tune','-rc','-cq','-b:v']\n b=[];i=0\n while i<len(a):\n" +
                "  if a[i] in drop: i+=2;continue\n  b.append('libx264' if a[i]=='h264_nvenc' else 'fast' if a[i]=='p5' else a[i]);i+=1\n a=b\n" +
                "os.execvp('ffmpeg',['ffmpeg']+a)\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private sealed class CpuOracleCompositor(bool fail) : IPreviewFrameCompositor
    {
        private byte[] pixels = []; private int width, height;
        public int Begins; public bool Disposed; public ulong AllocatedBytes => (ulong)pixels.Length;
        public void Begin(int w, int h) { Begins++; width = w; height = h; pixels = new byte[w * h * 4]; for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; }
        public void Composite(ReadOnlySpan<byte> source, NativeAppearance a) => NativeComposition.Composite(pixels, source, width, height, a, default);
        public byte[] Read() => fail ? throw new InvalidOperationException("test device lost") : pixels.ToArray();
        public void Reset() => pixels = [];
        public void Dispose() { Disposed = true; pixels = []; }
    }
    private sealed class Decoder : IMediaDecoder
    {
        public int Calls; public List<long> Ticks = [];
        public Task<ImmutableArray<byte>> VideoAsync(string path, long tick, int width, int height, CancellationToken token)
        { Calls++; Ticks.Add(tick); var pixels = new byte[width * height * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 170; pixels[i + 3] = 160; } return Task.FromResult(ImmutableArray.CreateRange(pixels)); }
        public Task<ImmutableArray<float>> AudioAsync(string path, long tick, int count, int rate, int channels, CancellationToken token) => throw new AssertFailedException("Disabled audio.");
    }
    private sealed class Captions : ICaptionRasterizer
    {
        public ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token)
        { var pixels = new byte[width * height * 4]; pixels[1] = 220; pixels[3] = 150; return ValueTask.FromResult(ImmutableArray.CreateRange(pixels)); }
    }
    private sealed class CaptureBackend(TimelineEvaluator evaluator, IFrameRenderer oracle) : IVideoEncodingBackend
    {
        public int Frames; public long Samples;
        public async Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media, FfmpegSettings settings, IProgress<ExportProgress>? progress, CancellationToken token)
        {
            await foreach (var frame in media.ReadVideoAsync(token))
            {
                var expected = await oracle.RenderAsync(evaluator.Project, evaluator.Evaluate(frame.Tick).Value!, frame.FrameIndex, token);
                CollectionAssert.AreEqual(expected.Value!.Rgba8.ToArray(), frame.Rgba8.ToArray()); Frames++;
            }
            await foreach (var audio in media.ReadAudioAsync(token)) Samples += audio.Samples.Length / 2;
            return new(request.JobId, ExportStage.Completed, request.OutputPath, [], new("libx264", null, null, 0, 0, 0, 0, 0));
        }
    }
    private static async Task<string> Run(string executable, string[] args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Assert.AreEqual(0, process.ExitCode, await error); return await output;
    }
}
