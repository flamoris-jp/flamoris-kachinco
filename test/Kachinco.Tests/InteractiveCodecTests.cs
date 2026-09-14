using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;
[TestClass]
public sealed class InteractiveCodecTests
{
    [TestMethod]
    public async Task ForwardTimestampedFramesMatchArbitrarySeekAndAmortizeStartup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-forward-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "source.mov");
            await Run(["-f", "lavfi", "-i", "testsrc2=s=64x36:r=30:d=3", "-c:v", "qtrle", path]);
            var random = new FfmpegMediaDecoder(); using var forward = new FfmpegForwardDecoder(); using var lifetime = new CancellationTokenSource();
            foreach (decimal seconds in new[] {.02m, .04m, .10m, .12m, .2m, .5m, 1.1m, 1.8m, 2.1m})
            {
                long t = TimelineTime.SecondsToTicks(seconds);
                var expected = await random.VideoAsync(path, t, 64, 36, default);
                var actual = await forward.VideoAsync(path, t, 64, 36, lifetime.Token);
                CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray(), $"source {seconds}");
                Assert.IsTrue(actual.Where((_, i) => i % 4 != 3).Any(b => b > 100), "Actual MOV pixels are visible.");
            }
            Assert.IsTrue(forward.ProcessStarts <= 2);
            lifetime.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => forward.VideoAsync(path, 0, 64, 36, lifetime.Token));
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task ForwardPcmMatchesRandomAccessAndHandlesFinalPartialSource()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-pcm-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "source.wav");
            await Run(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-c:a", "pcm_s16le", path]);
            var random = new FfmpegMediaDecoder(); using var forward = new FfmpegForwardDecoder();
            for (int i = 2; i < 8; i++)
            {
                long tick = TimelineTime.SampleToTicks(i * 4800, 48000);
                var expected = await random.AudioAsync(path, tick, 4800, 48000, 2, default);
                var actual = await forward.AudioAsync(path, tick, 4800, 48000, 2, default);
                CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
            }
            Assert.AreEqual(1L, forward.ProcessStarts);
            var tail = await forward.AudioAsync(path, TimelineTime.SampleToTicks(47999, 48000), 2, 48000, 2, default);
            Assert.AreEqual(4, tail.Length); Assert.AreEqual(0f, tail[2]);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task PreviewFullIsExportPixelsAndQualityPreservesCanonicalTime()
    {
        var f = new Fixture(); Assert.IsTrue(f.Edit(new SetTrackEnabled(f.SequenceId, f.VideoTrackId, false), new SetTrackEnabled(f.SequenceId, f.SubtitleTrackId, false)).Success);
        var frame = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(Fixture.T + 123).Value!;
        var renderer = new SharedFrameRenderer(new FfmpegMediaDecoder());
        var full = await renderer.RenderPreviewAsync(f.Project, frame, PreviewQuality.Full, default);
        var export = await renderer.RenderAsync(f.Project, frame, full.Value!.FrameIndex, default);
        CollectionAssert.AreEqual(export.Value!.Rgba8.ToArray(), full.Value.Rgba8.ToArray());
        foreach (var quality in new[] { PreviewQuality.Full, PreviewQuality.Half, PreviewQuality.Quarter })
        {
            var result = await renderer.RenderPreviewAsync(f.Project, frame, quality, default);
            Assert.AreEqual(frame.Tick, result.Value!.Tick); Assert.AreEqual(1920 / (int)quality, result.Value.Width); Assert.AreEqual(1080 / (int)quality, result.Value.Height);
        }
    }
    private static async Task Run(string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (string a in new[] {"-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1"}.Concat(args)) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!; var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
    }
}
