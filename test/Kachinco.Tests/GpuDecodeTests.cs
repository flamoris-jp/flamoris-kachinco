using System.Collections.Immutable;
using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class GpuDecodeTests
{
    [TestMethod]
    public void HardwareArgumentsRequireD3D11FramesAndExplicitDownloadWithBoundedContinuousPipes()
    {
        string[] software = FfmpegVideoDecodeArguments.Build("source.mov", Fixture.T / 2, 64, 36, false);
        string[] hardware = FfmpegVideoDecodeArguments.Build("source.mov", Fixture.T / 2, 64, 36, true);
        Assert.IsFalse(software.Contains("-hwaccel"));
        Assert.AreEqual("d3d11va", Value(hardware, "-hwaccel"));
        Assert.AreEqual("d3d11", Value(hardware, "-hwaccel_output_format"));
        Assert.AreEqual("0", Value(hardware, "-extra_hw_frames"));
        string softwareFilter = Value(software, "-vf"), hardwareFilter = Value(hardware, "-vf");
        Assert.AreEqual("hwdownload,format=nv12," + softwareFilter, hardwareFilter);
        foreach (string option in new[] { "-ss", "-i", "-map", "-fps_mode", "-pix_fmt" })
            Assert.AreEqual(Value(software, option), Value(hardware, option), option);
        Assert.AreEqual("0.5", Value(hardware, "-ss"));
        Assert.IsFalse(hardware.Contains("-t"));
        Assert.IsFalse(hardware.Contains("-frames:v"));
        Assert.AreEqual("passthrough", Value(hardware, "-fps_mode"));
    }

    [TestMethod]
    public async Task UnsupportedHardwareCodecRetriesTheExactTickInSoftwareOnceAndKeepsForwardReuse()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-gpu-decode-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "source.mov");
            // QTRLE has no D3D11VA decoder. Even on a machine with D3D11, strict
            // hwdownload rejects any silent software codec choice.
            await CreateVideo(path);
            using var random = new FfmpegMediaDecoder();
            using var decoder = new FfmpegForwardDecoder(randomVideoFallback: new UnexpectedRandomFallback(),
                decodePreference: PreviewDecodePreference.D3D11);
            using var owner = new CancellationTokenSource();
            foreach (decimal seconds in new[] { .12m, .14m, .2m, .5m, .9m, 1.1m, 2.5m })
            {
                long tick = TimelineTime.SecondsToTicks(seconds);
                var expected = await random.VideoAsync(path, tick, 64, 36, default);
                var actual = await decoder.VideoAsync(path, tick, 64, 36, owner.Token);
                CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray(), $"source {seconds}");
                Assert.AreEqual(PreviewDecodePreference.D3D11, decoder.DecodeDiagnostics.RequestedBackend);
                Assert.AreEqual(PreviewDecodePreference.Software, decoder.DecodeDiagnostics.ActiveBackend);
                Assert.IsTrue(decoder.DecodeDiagnostics.HardwareRequested);
                Assert.IsFalse(decoder.DecodeDiagnostics.HardwareConfirmed);
                Assert.IsFalse(decoder.DecodeDiagnostics.RequiresCpuTransfer);
                Assert.IsFalse(string.IsNullOrWhiteSpace(decoder.DecodeDiagnostics.FallbackReason));
                Assert.AreEqual(0, decoder.ActiveHardwareVideoStreams);
            }
            Assert.AreEqual(2L, decoder.ProcessStarts, "One failing hardware launch, then one continuous software stream.");
            // A new playback owner and resolution must not repeatedly try a broken
            // hardware device/codec path for this decoder instance.
            using var renewed = new CancellationTokenSource();
            await decoder.VideoAsync(path, 0, 32, 18, renewed.Token);
            Assert.AreEqual(3L, decoder.ProcessStarts);
            decoder.ResetStreams();
            Assert.AreEqual(0, decoder.ActiveVideoStreams);
            await decoder.VideoAsync(path, 0, 64, 36, renewed.Token);
            Assert.AreEqual(4L, decoder.ProcessStarts, "Playback lifecycle reset preserves the sticky hardware failure.");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task CancelledRequestNeverStartsOrDisablesHardwareAndSoftwareDefaultRemainsExplicit()
    {
        using var hardware = new FfmpegForwardDecoder(decodePreference: PreviewDecodePreference.D3D11);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => hardware.VideoAsync("unopened.mov", 0, 64, 36, cancelled.Token));
        Assert.AreEqual(0L, hardware.ProcessStarts);
        Assert.IsNull(hardware.DecodeDiagnostics.FallbackReason);
        Assert.IsFalse(hardware.DecodeDiagnostics.HardwareConfirmed);
        using var software = new FfmpegForwardDecoder();
        Assert.AreEqual(PreviewDecodePreference.Software, software.DecodeDiagnostics.RequestedBackend);
        Assert.IsFalse(software.DecodeDiagnostics.HardwareRequested);
        Assert.IsFalse(software.DecodeDiagnostics.RequiresCpuTransfer);
    }

    [TestMethod]
    public void InvalidDecodePreferenceIsRejectedBeforeStartingAProcess() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FfmpegForwardDecoder(decodePreference: (PreviewDecodePreference)99));

    [TestMethod]
    public async Task PartialHardwareFrameIsDiscardedAndSoftwareRestartsTheSameRequest()
    {
        List<string[]> launches = [];
        using var decoder = FakeDecoder(launches, "partial-hardware");
        long tick = Fixture.T / 2;
        var frame = await decoder.VideoAsync("fixture.mov", tick, 16, 16, default);
        Assert.AreEqual(2, launches.Count);
        Assert.IsTrue(launches[0].Contains("-hwaccel"));
        Assert.IsFalse(launches[1].Contains("-hwaccel"));
        Assert.AreEqual(Value(launches[0], "-ss"), Value(launches[1], "-ss"));
        Assert.IsTrue(frame.Where((_, i) => i % 4 == 0).All(value => value == 23), "Only complete software pixels may escape.");
        await decoder.VideoAsync("fixture.mov", tick + TimelineTime.FrameToTicks(1, new(30, 1)), 16, 16, default);
        Assert.AreEqual(2, launches.Count, "Software forward stream remains reusable after a partial GPU failure.");
    }

    [TestMethod]
    public async Task HardwareStreamPoolIsBoundedAndExcessContributorsUseSoftwareForwardStreams()
    {
        List<string[]> launches = [];
        using var decoder = FakeDecoder(launches, "frames");
        for (int i = 0; i < FfmpegForwardDecoder.MaximumHardwareVideoStreams + 1; i++)
            await decoder.VideoAsync($"fixture-{i}.mov", 0, 16, 16, default);
        Assert.AreEqual(2, decoder.ActiveHardwareVideoStreams);
        Assert.AreEqual(3, decoder.ActiveVideoStreams);
        Assert.IsTrue(launches[0].Contains("-hwaccel"));
        Assert.IsTrue(launches[1].Contains("-hwaccel"));
        Assert.IsFalse(launches[2].Contains("-hwaccel"));
        Assert.AreEqual(PreviewDecodePreference.Software, decoder.DecodeDiagnostics.ActiveBackend);
        StringAssert.Contains(decoder.DecodeDiagnostics.FallbackReason!, "stream limit");
        await decoder.VideoAsync("fixture-0.mov", TimelineTime.FrameToTicks(1, new(30, 1)), 16, 16, default);
        Assert.IsTrue(decoder.DecodeDiagnostics.HardwareConfirmed);
        Assert.IsTrue(decoder.DecodeDiagnostics.RequiresCpuTransfer);
        Assert.AreEqual(3, launches.Count);
        decoder.ResetStreams();
        Assert.AreEqual(0, decoder.ActiveHardwareVideoStreams);
        Assert.AreEqual(0, decoder.ActiveVideoStreams);
    }

    [TestMethod]
    public async Task CancellationDuringHardwareReadNeverFallsBackAndDoesNotDisableTheNextRequest()
    {
        List<string[]> launches = [];
        int starts = 0;
        using var decoder = new FfmpegForwardDecoder("unused", null, "test", new UnexpectedRandomFallback(),
            PreviewDecodePreference.D3D11, args =>
            {
                string[] captured = args.ToArray(); launches.Add(captured);
                return FakeProcess(captured, starts++ == 0 ? "stall" : "frames");
            });
        using var owner = new CancellationTokenSource();
        var pending = decoder.VideoAsync("fixture.mov", 0, 16, 16, owner.Token);
        Assert.AreEqual(1, launches.Count);
        owner.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(1, launches.Count);
        Assert.IsNull(decoder.DecodeDiagnostics.FallbackReason);
        using var renewed = new CancellationTokenSource();
        await decoder.VideoAsync("fixture.mov", 0, 16, 16, renewed.Token);
        Assert.AreEqual(2, launches.Count);
        Assert.IsTrue(launches[1].Contains("-hwaccel"));
        Assert.IsTrue(decoder.DecodeDiagnostics.HardwareConfirmed);
    }

    [TestMethod]
    public async Task HardwareStreamContinuesPast64FramesAndTwoSecondsWithoutLosingItsSlot()
    {
        List<string[]> launches = [];
        using var decoder = FakeDecoder(launches, "long");
        for (int frame = 0; frame < 180; frame++)
        {
            long tick = TimelineTime.FrameToTicks(frame, new(30, 1));
            decoder.RetainVideoStreams([("fixture.mov", tick)], 16, 16, default);
            await decoder.VideoAsync("fixture.mov", tick, 16, 16, default);
            Assert.IsTrue(decoder.DecodeDiagnostics.HardwareConfirmed);
            Assert.AreEqual(1, decoder.ActiveHardwareVideoStreams);
        }
        Assert.AreEqual(1, launches.Count);
        long seek = 9 * Fixture.T;
        decoder.RetainVideoStreams([("fixture.mov", seek)], 16, 16, default);
        Assert.AreEqual(0, decoder.ActiveHardwareVideoStreams, "A large forward seek releases the old surface pool before opening.");
        await decoder.VideoAsync("fixture.mov", seek, 16, 16, default);
        Assert.AreEqual(2, launches.Count);
        Assert.IsTrue(launches[1].Contains("-hwaccel"));
    }

    [TestMethod]
    public async Task ContributorRetirementReleasesHardwareBeforeTheNextClipAndPreservesOffsets()
    {
        List<string[]> launches = [];
        using var decoder = FakeDecoder(launches, "long");
        // Opening decreasing source positions creates independent streams.
        await decoder.VideoAsync("fixture.mov", Fixture.T, 16, 16, default);
        await decoder.VideoAsync("fixture.mov", 0, 16, 16, default);
        decoder.RetainVideoStreams([("fixture.mov", Fixture.T), ("fixture.mov", 0)], 16, 16, default);
        Assert.AreEqual(2, decoder.ActiveHardwareVideoStreams);
        await decoder.VideoAsync("fixture.mov", Fixture.T, 16, 16, default);
        await decoder.VideoAsync("fixture.mov", 0, 16, 16, default);
        Assert.AreEqual(2, launches.Count, "Both simultaneous offsets must stay reusable.");
        decoder.RetainVideoStreams([("next.mov", 0)], 16, 16, default);
        Assert.AreEqual(0, decoder.ActiveVideoStreams);
        await decoder.VideoAsync("next.mov", 0, 16, 16, default);
        Assert.IsTrue(launches[2].Contains("-hwaccel"));
        decoder.RetainVideoStreams([], 16, 16, default);
        Assert.AreEqual(0, decoder.ActiveHardwareVideoStreams);
    }

    [TestMethod]
    public async Task NaturalEofReleasesHardwareWithoutRestartOrStickyDowngradeAndRetainsExactTail()
    {
        List<string[]> launches = [];
        using var decoder = FakeDecoder(launches, "tail");
        long frameTick = TimelineTime.FrameToTicks(1, new(30, 1));
        await decoder.VideoAsync("fixture.mov", 0, 16, 16, default);
        var last = await decoder.VideoAsync("fixture.mov", frameTick, 16, 16, default);
        var end = await Assert.ThrowsExactlyAsync<MediaEndOfStreamException>(() => decoder.VideoAsync("fixture.mov", 2 * frameTick, 16, 16, default));
        Assert.AreEqual(frameTick, end.RetainedRequestTick);
        CollectionAssert.AreEqual(last.ToArray(), end.RetainedFrame.ToArray());
        Assert.AreEqual(1, launches.Count, "EOF must not reopen at the same missing timestamp.");
        Assert.AreEqual(0, decoder.ActiveHardwareVideoStreams);
        await Assert.ThrowsExactlyAsync<MediaEndOfStreamException>(() => decoder.VideoAsync("fixture.mov", 3 * frameTick, 16, 16, default));
        Assert.AreEqual(1, launches.Count);
        await decoder.VideoAsync("next.mov", 0, 16, 16, default);
        Assert.IsTrue(launches[1].Contains("-hwaccel"));
        Assert.IsTrue(decoder.DecodeDiagnostics.HardwareConfirmed, "Natural EOF must not disable healthy hardware.");
    }

    [TestMethod]
    public async Task PreviewCacheHitStillRetiresOldContributors()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mov");
        File.WriteAllText(path, "decoder fixture");
        try
        {
            var fixture = new Fixture();
            Assert.IsTrue(fixture.Edit(new RelinkMedia(fixture.MovId, path, 10 * Fixture.T),
                new SetTrackEnabled(fixture.SequenceId, fixture.SubtitleTrackId, false)).Success);
            List<string[]> launches = [];
            using var decoder = FakeDecoder(launches, "long");
            using var source = new InteractivePreviewSource(forwardVideo: decoder);
            var context = PreviewContext.Create(fixture.Session.GetProject(), fixture.SequenceId).Value!;
            Assert.IsTrue((await source.FrameAsync(context, 0, PreviewQuality.Quarter, true, default)).Success);
            await decoder.VideoAsync("obsolete.mov", 0, 480, 270, default);
            Assert.AreEqual(2, decoder.ActiveHardwareVideoStreams);
            Assert.IsTrue((await source.FrameAsync(context, 0, PreviewQuality.Quarter, true, default)).Success);
            Assert.AreEqual(1L, source.Frames.Statistics.Hits);
            Assert.AreEqual(1, decoder.ActiveHardwareVideoStreams);
            Assert.AreEqual(2, launches.Count, "Cache hit must retire obsolete streams without decoding another frame.");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task RendererReusesOnlyTheExactExistingFirstTailRetryWithoutAnotherProcess()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mov");
        File.WriteAllText(path, "decoder fixture");
        try
        {
            var fixture = new Fixture();
            Assert.IsTrue(fixture.Edit(new RelinkMedia(fixture.MovId, path, 10 * Fixture.T)).Success);
            List<string[]> launches = [];
            using var decoder = FakeDecoder(launches, "tail");
            var renderer = new SharedFrameRenderer(decoder);
            using var evaluator = TimelineEvaluator.Create(fixture.Project, fixture.SequenceId).Value!;
            long step = TimelineTime.FrameToTicks(1, new(30, 1));
            Result<RenderedVideoFrame>? last = null;
            for (int i = 0; i < 3; i++)
            {
                var frame = evaluator.Evaluate(i * step).Value!;
                frame = frame with { Settings = frame.Settings with { Width = 16, Height = 16 } };
                var result = await renderer.RenderAsync(fixture.Project, frame, i, default);
                Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
                Assert.AreEqual(i * step, result.Value!.Tick);
                if (i == 2) CollectionAssert.AreEqual(last!.Value!.Rgba8.ToArray(), result.Value.Rgba8.ToArray());
                last = result;
            }
            Assert.AreEqual(1, launches.Count, "Natural tail uses the exact previously decoded retry tick.");
            Assert.AreEqual(0, decoder.ActiveHardwareVideoStreams);
        }
        finally { File.Delete(path); }
    }

    private static FfmpegForwardDecoder FakeDecoder(List<string[]> launches, string mode) =>
        new("unused", null, "test", new UnexpectedRandomFallback(), PreviewDecodePreference.D3D11, args =>
        {
            string[] captured = args.ToArray(); launches.Add(captured);
            return FakeProcess(captured, mode);
        });
    private static NativeMediaProcess FakeProcess(string[] args, string mode) =>
        NativeMediaProcess.Start(OperatingSystem.IsWindows() ? "python" : "python3", ["-c", FakeDecoderPython, mode, ..args]);

    // Inject a bounded codec child through the existing native process adapter. This
    // tests pool/fallback/cancellation orchestration without requiring a physical GPU.
    private const string FakeDecoderPython = """
import sys,re,time
mode=sys.argv[1]
args=sys.argv[2:]
hardware='-hwaccel' in args
vf=args[args.index('-vf')+1]
w,h=map(int,re.search(r'scale=(\d+):(\d+)',vf).groups())
sys.stderr.write('[Parsed_showinfo_0] config in time_base: 1/30\n')
sys.stderr.flush()
if mode=='stall':
    time.sleep(60)
    sys.exit(1)
if mode=='partial-hardware' and hardware:
    sys.stderr.write('[Parsed_showinfo_0] n: 0 pts: 0\n')
    sys.stderr.flush()
    sys.stdout.buffer.write(bytes([99,0,0,255])*(w*h//2))
    sys.stdout.buffer.flush()
    sys.exit(1)
frame=bytes([99 if hardware else 23,0,0,255])*(w*h)
for i in range(256 if mode=='long' else 2 if mode=='tail' else 64):
    sys.stderr.write('[Parsed_showinfo_0] n: %d pts: %d\n'%(i,i))
    sys.stderr.flush()
    sys.stdout.buffer.write(frame)
sys.stdout.buffer.flush()
""";

    private static string Value(string[] args, string option) => args[Array.IndexOf(args, option) + 1];
    private static async Task CreateVideo(string path)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-f", "lavfi", "-i",
            "testsrc2=s=64x36:r=30:d=3", "-c:v", "qtrle", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await errors);
    }
    private sealed class UnexpectedRandomFallback : IMediaDecoder
    {
        public Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token) =>
            throw new AssertFailedException("Hardware fallback must preserve software forward reuse.");
        public Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token) =>
            throw new AssertFailedException("Unexpected audio fallback.");
    }
}
