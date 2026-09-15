using System.Diagnostics;
using System.Collections.Immutable;
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
    public async Task ThreeVideoContributorsReuseProcessesAcrossFrames()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-forward-three-video-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "source.mov");
            await Run(["-f", "lavfi", "-i", "testsrc2=s=64x36:r=30:d=2", "-c:v", "qtrle", source]);
            string[] paths = [source, Path.Combine(dir, "second.mov"), Path.Combine(dir, "third.mov")];
            File.Copy(source, paths[1]); File.Copy(source, paths[2]);
            using var forward = new FfmpegForwardDecoder();
            for (int frame = 0; frame < 12; frame++)
            {
                long tick = TimelineTime.FrameToTicks(frame, new(30, 1));
                foreach (string path in paths)
                {
                    var pixels = await forward.VideoAsync(path, tick, 64, 36, default);
                    Assert.IsTrue(pixels.Where((_, i) => i % 4 != 3).Any(value => value > 100));
                }
            }
            Assert.AreEqual(3L, forward.ProcessStarts, "Three simultaneous source contributors must keep their forward streams across frames.");
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task ThreeAudioContributorsReuseProcessesAcrossBlocks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-forward-three-audio-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string source = Path.Combine(dir, "source.wav");
            await Run(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:a", "pcm_s16le", source]);
            string[] paths = [source, Path.Combine(dir, "second.wav"), Path.Combine(dir, "third.wav")];
            File.Copy(source, paths[1]); File.Copy(source, paths[2]);
            using var forward = new FfmpegForwardDecoder();
            const int count = 4800;
            for (int block = 0; block < 10; block++)
            {
                long tick = TimelineTime.SampleToTicks(block * count, 48000);
                foreach (string path in paths)
                {
                    var samples = await forward.AudioAsync(path, tick, count, 48000, 2, default);
                    Assert.IsTrue(samples.Any(value => value != 0));
                }
            }
            Assert.AreEqual(3L, forward.ProcessStarts, "Three simultaneous source contributors must keep their forward streams across PCM blocks.");
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task SameVideoSourceAtThreeOffsetsKeepsIndependentForwardWindows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-forward-offset-video-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "source.mov");
            await Run(["-f", "lavfi", "-i", "testsrc2=s=64x36:r=30:d=2", "-c:v", "qtrle", path]);
            using var forward = new FfmpegForwardDecoder();
            long[] offsets = [0, Fixture.T / 2, Fixture.T];
            for (int frame = 0; frame < 12; frame++)
                foreach (long offset in offsets)
                {
                    var pixels = await forward.VideoAsync(path, offset + TimelineTime.FrameToTicks(frame, new(30, 1)), 64, 36, default);
                    Assert.IsTrue(pixels.Where((_, i) => i % 4 != 3).Any(value => value > 100));
                }
            Assert.AreEqual(3L, forward.ProcessStarts, "One source at three clip offsets needs three reusable windows, not a process per frame.");
            Assert.AreEqual(3, forward.ActiveVideoStreams);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task SameAudioSourceAtThreeOffsetsKeepsIndependentForwardWindows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-forward-offset-audio-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "source.wav");
            await Run(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:a", "pcm_s16le", path]);
            using var forward = new FfmpegForwardDecoder();
            const int count = 4800; long[] offsets = [0, Fixture.T / 2, Fixture.T];
            for (int block = 0; block < 10; block++)
                foreach (long offset in offsets)
                {
                    var samples = await forward.AudioAsync(path, offset + TimelineTime.SampleToTicks(block * count, 48000), count, 48000, 2, default);
                    Assert.IsTrue(samples.Any(value => value != 0));
                }
            Assert.AreEqual(3L, forward.ProcessStarts, "One source at three clip offsets needs three reusable windows, not a process per block.");
            Assert.AreEqual(3, forward.ActiveAudioStreams);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task ForwardStreamPoolsKeepHardBoundsAndEvictLeastRecentlyUsed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-forward-lru-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "video-0.mov"), audio = Path.Combine(dir, "audio-0.wav");
            await Run(["-f", "lavfi", "-i", "testsrc2=s=16x16:r=30:d=2", "-c:v", "qtrle", video]);
            await Run(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:a", "pcm_s16le", audio]);
            string[] videos = Enumerable.Range(0, FfmpegForwardDecoder.MaximumVideoStreams + 1).Select(i => Path.Combine(dir, $"video-{i}.mov")).ToArray();
            string[] audios = Enumerable.Range(0, FfmpegForwardDecoder.MaximumAudioStreams + 1).Select(i => Path.Combine(dir, $"audio-{i}.wav")).ToArray();
            for (int i = 1; i < videos.Length; i++) File.Copy(video, videos[i]);
            for (int i = 1; i < audios.Length; i++) File.Copy(audio, audios[i]);
            using (var forward = new FfmpegForwardDecoder())
            {
                foreach (string path in videos) await forward.VideoAsync(path, 0, 16, 16, default);
                Assert.AreEqual(FfmpegForwardDecoder.MaximumVideoStreams, forward.ActiveVideoStreams);
                long starts = forward.ProcessStarts;
                await forward.VideoAsync(videos[^1], TimelineTime.FrameToTicks(1, new(30, 1)), 16, 16, default);
                Assert.AreEqual(starts, forward.ProcessStarts, "The newest video stream remains resident.");
                await forward.VideoAsync(videos[0], TimelineTime.FrameToTicks(1, new(30, 1)), 16, 16, default);
                Assert.AreEqual(starts + 1, forward.ProcessStarts, "The least recently used video stream was evicted.");
                Assert.AreEqual(FfmpegForwardDecoder.MaximumVideoStreams, forward.ActiveVideoStreams);
            }
            using (var forward = new FfmpegForwardDecoder())
            {
                foreach (string path in audios) await forward.AudioAsync(path, 0, 480, 48000, 2, default);
                Assert.AreEqual(FfmpegForwardDecoder.MaximumAudioStreams, forward.ActiveAudioStreams);
                long starts = forward.ProcessStarts;
                await forward.AudioAsync(audios[^1], TimelineTime.SampleToTicks(480, 48000), 480, 48000, 2, default);
                Assert.AreEqual(starts, forward.ProcessStarts, "The newest audio stream remains resident.");
                await forward.AudioAsync(audios[0], TimelineTime.SampleToTicks(480, 48000), 480, 48000, 2, default);
                Assert.AreEqual(starts + 1, forward.ProcessStarts, "The least recently used audio stream was evicted.");
                Assert.AreEqual(FfmpegForwardDecoder.MaximumAudioStreams, forward.ActiveAudioStreams);
            }
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
    [TestMethod]
    public async Task ScaledPreviewPreservesTransformsBlendOpacitySourceTimeAndCaptionInputs()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mov"); File.WriteAllText(path, "decoder fixture");
        try
        {
            var f = new Fixture();
            Assert.IsTrue(f.Edit(new RelinkMedia(f.MovId, path, 10 * Fixture.T),
                new SetClipProperties(f.SequenceId, f.ClipId, true, new(new(64, 32, .5, .5, 0), .75, BlendMode.Screen), AudioProperties.Default)).Success);
            var decoder = new SolidDecoder(); var captions = new CaptionInputs(); var renderer = new SharedFrameRenderer(decoder, captions: captions);
            var frame = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(2 * Fixture.T).Value!;
            var full = await renderer.RenderPreviewAsync(f.Project, frame, PreviewQuality.Full, default);
            var export = await renderer.RenderAsync(f.Project, frame, 60, default);
            CollectionAssert.AreEqual(full.Value!.Rgba8.ToArray(), export.Value!.Rgba8.ToArray());
            foreach (var quality in new[] { PreviewQuality.Full, PreviewQuality.Half, PreviewQuality.Quarter })
            {
                var result = await renderer.RenderPreviewAsync(f.Project, frame, quality, default); Assert.IsTrue(result.Success);
                var actual = result.Value!; int divisor = (int)quality;
                Assert.AreEqual(3 * Fixture.T, decoder.SourceTick); Assert.AreEqual(frame.Captions, captions.Input);
                Assert.AreEqual((actual.Width, actual.Height), captions.Size);
                int inside = ((200 / divisor) * actual.Width + 200 / divisor) * 4;
                Assert.AreEqual(full.Value.Rgba8[(200 * 1920 + 200) * 4], actual.Rgba8[inside]);
                Assert.IsTrue(actual.Rgba8[inside] > 0); Assert.AreEqual((byte)0, actual.Rgba8[0], "Translated source leaves the origin black.");
                Assert.AreEqual(frame.Tick, actual.Tick);
            }
        }
        finally { File.Delete(path); }
    }
    private sealed class SolidDecoder : IMediaDecoder
    {
        public long SourceTick;
        public Task<ImmutableArray<byte>> VideoAsync(string path, long tick, int w, int h, CancellationToken ct)
        {
            SourceTick = tick; byte[] pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 50; pixels[i + 3] = 128; }
            return Task.FromResult(pixels.ToImmutableArray());
        }
        public Task<ImmutableArray<float>> AudioAsync(string p, long t, int c, int r, int ch, CancellationToken ct) => throw new AssertFailedException("Unexpected audio decode.");
    }
    private sealed class CaptionInputs : ICaptionRasterizer
    {
        public ImmutableArray<EvaluatedCaption> Input; public (int, int) Size;
        public ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> input, int w, int h, CancellationToken ct)
        { Input = input; Size = (w, h); return ValueTask.FromResult(new byte[w * h * 4].ToImmutableArray()); }
    }
    private static async Task Run(string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (string a in new[] {"-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1"}.Concat(args)) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!; var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
    }
}
