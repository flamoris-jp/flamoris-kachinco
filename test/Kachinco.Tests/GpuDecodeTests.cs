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
    public void HardwareArgumentsRequireD3D11FramesAndExplicitDownloadWithoutChangingTheForwardWindow()
    {
        string[] software = FfmpegVideoDecodeArguments.Build("source.mov", Fixture.T / 2, 64, 36, false);
        string[] hardware = FfmpegVideoDecodeArguments.Build("source.mov", Fixture.T / 2, 64, 36, true);
        Assert.IsFalse(software.Contains("-hwaccel"));
        Assert.AreEqual("d3d11va", Value(hardware, "-hwaccel"));
        Assert.AreEqual("d3d11", Value(hardware, "-hwaccel_output_format"));
        Assert.AreEqual("0", Value(hardware, "-extra_hw_frames"));
        string softwareFilter = Value(software, "-vf"), hardwareFilter = Value(hardware, "-vf");
        Assert.AreEqual("hwdownload,format=nv12," + softwareFilter, hardwareFilter);
        foreach (string option in new[] { "-ss", "-i", "-map", "-t", "-frames:v", "-fps_mode", "-pix_fmt" })
            Assert.AreEqual(Value(software, option), Value(hardware, option), option);
        Assert.AreEqual("0.5", Value(hardware, "-ss"));
        Assert.AreEqual("2", Value(hardware, "-t"));
        Assert.AreEqual("64", Value(hardware, "-frames:v"));
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
            Assert.AreEqual(3L, decoder.ProcessStarts, "One failing hardware launch, then two bounded software windows.");
            // A new playback owner and resolution must not repeatedly try a broken
            // hardware device/codec path for this decoder instance.
            using var renewed = new CancellationTokenSource();
            await decoder.VideoAsync(path, 0, 32, 18, renewed.Token);
            Assert.AreEqual(4L, decoder.ProcessStarts);
            decoder.ResetStreams();
            Assert.AreEqual(0, decoder.ActiveVideoStreams);
            await decoder.VideoAsync(path, 0, 64, 36, renewed.Token);
            Assert.AreEqual(5L, decoder.ProcessStarts, "Playback lifecycle reset preserves the sticky hardware failure.");
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
for i in range(64):
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
