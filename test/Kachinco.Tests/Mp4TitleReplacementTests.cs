using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Immutable;
using Kachinco.Core;
using System.Text.Json;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class Mp4TitleReplacementTests
{
    [TestMethod]
    [DataRow("30", 3, 181, 60, true)]
    [DataRow("30000/1001", 0, 181, 60, true)]
    [DataRow("30000/1001", 2, 181, 60, false)]
    [DataRow("30", 1, 7, 60, true)]
    [DataRow("30", 3, 5101, 250, true)]
    public async Task ReplacesFrameZeroAndPreservesTimingAudioAndDecodedCopiedPictures(string fps, int bframes, int count, int gop, bool audio)
    {
        string dir = NewDirectory();
        try
        {
            var (source, image) = await Fixture(dir, fps, bframes, count, gop, audio);
            var originalBytes = await File.ReadAllBytesAsync(source);
            var service = new Mp4TitleReplacement();
            var plan = await service.PrepareAsync(source, image);
            Assert.AreEqual(count, plan.FrameCount);
            Assert.AreEqual(Math.Min(count, gop), plan.ReencodedFrames);
            Assert.AreEqual(audio, plan.HasAudio);
            Assert.IsTrue(plan.PreviewPng.Length > 0);
            string output = Path.Combine(dir, "finished.mp4");
            var result = await service.ReplaceAsync(plan, output);
            Assert.AreEqual(count - plan.ReencodedFrames, result.CopiedFrames);
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(source));
            // Strict full decode protects against joins which look valid in container metadata only.
            await Run("ffmpeg", ["-v", "error", "-xerror", "-i", output, "-f", "null", "-"]);
            var before = await FrameHashes(source); var after = await FrameHashes(output);
            Assert.AreEqual(count, after.Length);
            if (result.CopiedFrames > 0) CollectionAssert.AreEqual(before[plan.ReencodedFrames..], after[plan.ReencodedFrames..]);
            // Inspect the first and next displayed frames independently. Only frame zero receives the title.
            byte[] pixels = await Run("ffmpeg", ["-v", "error", "-i", output, "-frames:v", "2", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]);
            Assert.IsTrue(pixels[0] > 220 && pixels[1] < 25 && pixels[2] < 25, "Frame zero must be the red title.");
            Assert.IsTrue(pixels[3] < 220 || pixels[4] > 25 || pixels[5] > 25, "Frame one must be source video, not the title.");
            CollectionAssert.AreEqual(await PacketTimes(source), await PacketTimes(output));
            Assert.AreEqual(0, Directory.GetDirectories(dir, ".kachinco-title-*").Length);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task CancellationAfterEncodingCleansTemporaryFilesAndRetainsInputs()
    {
        string dir = NewDirectory();
        try
        {
            var (source, image) = await Fixture(dir, "30", 3, 181, 60, true);
            var service = new Mp4TitleReplacement(); var plan = await service.PrepareAsync(source, image);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReplaceAsync(plan, Path.Combine(dir, "cancelled.mp4"),
                new CallbackProgress(p => { if (p.StartsWith("copy:", StringComparison.Ordinal)) cancellation.Cancel(); }), cancellation.Token));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "cancelled.mp4")));
            Assert.AreEqual(0, Directory.GetDirectories(dir, ".kachinco-title-*").Length);
            Assert.IsTrue(File.Exists(source)); Assert.IsTrue(File.Exists(image));
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task RejectsOverwriteChangedSourceAndUnsupportedCodec()
    {
        string dir = NewDirectory();
        try
        {
            var (source, image) = await Fixture(dir, "30", 0, 7, 60, false);
            var service = new Mp4TitleReplacement(); var plan = await service.PrepareAsync(source, image);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReplaceAsync(plan, source));
            string existing = Path.Combine(dir, "existing.mp4"); await File.WriteAllTextAsync(existing, "keep");
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReplaceAsync(plan, existing));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(existing));
            File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(1));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ReplaceAsync(plan, Path.Combine(dir, "changed.mp4")));
            string unsupported = Path.Combine(dir, "unsupported.mp4");
            await Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=s=160x90:r=30", "-frames:v", "3", "-c:v", "mpeg4", unsupported]);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(unsupported, image));
            Assert.AreEqual(0, Directory.GetDirectories(dir, ".kachinco-title-*").Length);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task OpenGopKeysAreNotMistakenForClosedIdrBoundaries()
    {
        string dir = NewDirectory();
        try
        {
            var (source, image) = await Fixture(dir, "30", 3, 181, 60, false, true);
            var plan = await new Mp4TitleReplacement().PrepareAsync(source, image);
            Assert.AreEqual(181, plan.ReencodedFrames);
            Assert.AreEqual(0, plan.CopiedFrames);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task RejectsVariableFrameRateAndRotatedVideo()
    {
        string dir = NewDirectory();
        try
        {
            var (source, image) = await Fixture(dir, "30", 0, 30, 60, false);
            string rotated = Path.Combine(dir, "rotated.mp4"), variable = Path.Combine(dir, "variable.mp4");
            await Run("ffmpeg", ["-v", "error", "-display_rotation", "90", "-i", source, "-c", "copy", rotated]);
            await Run("ffmpeg", ["-v", "error", "-i", source, "-vf", "select='not(eq(n,5))'", "-fps_mode", "vfr", "-c:v", "libx264", variable]);
            var service = new Mp4TitleReplacement();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(rotated, image));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(variable, image));
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task PreviewFitsImageAndFlattensTransparencyOnBlack()
    {
        string dir = NewDirectory();
        try
        {
            var (source, _) = await Fixture(dir, "30", 0, 7, 60, false);
            string image = Path.Combine(dir, "alpha.png");
            await Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=red@0.5:s=60x60,format=rgba", "-frames:v", "1", "-threads", "1", image]);
            var plan = await new Mp4TitleReplacement().PrepareAsync(source, image);
            string preview = Path.Combine(dir, "preview.png"); await File.WriteAllBytesAsync(preview, plan.PreviewPng.ToArray());
            byte[] rgb = await Run("ffmpeg", ["-v", "error", "-i", preview, "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]);
            Assert.AreEqual(160 * 90 * 3, rgb.Length);
            Assert.IsTrue(rgb[0] < 5 && rgb[1] < 5 && rgb[2] < 5, "Padding must be black.");
            int center = (45 * 160 + 80) * 3;
            Assert.IsTrue(rgb[center] > 110 && rgb[center] < 145 && rgb[center + 1] < 5 && rgb[center + 2] < 5,
                "Transparent title must composite on black, not the previous video image.");
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task RoundedEditDurationAndNvencStyleDecodeLeadRetainTheCfrGrid()
    {
        string dir = NewDirectory();
        try
        {
            var (source, image) = await Fixture(dir, "30", 3, 181, 60, true);
            string delayed = Path.Combine(dir, "delayed.mp4"), output = Path.Combine(dir, "fixed.mp4");
            // Reproduce the real NVENC file's three-frame DTS lead with unchanged PTS.
            await Run("ffmpeg", ["-v", "error", "-i", source, "-map", "0", "-c", "copy",
                "-bsf:v", "setts=pts=PTS:dts=DTS-1/30/TB", "-avoid_negative_ts", "disabled",
                "-video_track_timescale", "15360", "-movie_timescale", "1000", delayed]);
            // Pin both timescales: FFmpeg builds can choose a different movie clock.
            // Movie edit duration is 6033 ms; packet end is 6033 1/3 ms (-5 video ticks).
            SetVideoEditDuration(delayed, 6033);
            using (var metadata = JsonDocument.Parse(await Run("ffprobe", ["-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=duration_ts", "-of", "json", delayed])))
                Assert.AreEqual(181 * 512 - 5, metadata.RootElement.GetProperty("streams")[0].GetProperty("duration_ts").GetInt64());
            var service = new Mp4TitleReplacement(); var plan = await service.PrepareAsync(delayed, image);
            Assert.AreEqual(181, plan.FrameCount); Assert.AreEqual(60, plan.ReencodedFrames);
            await service.ReplaceAsync(plan, output);
            await Run("ffmpeg", ["-v", "error", "-xerror", "-i", output, "-f", "null", "-"]);
            CollectionAssert.AreEqual(await PacketTimes(delayed), await PacketTimes(output));
            var before = await FrameHashes(delayed); var after = await FrameHashes(output);
            CollectionAssert.AreEqual(before[60..], after[60..]);
            // A genuinely truncated duration must still fail, even with complete packets.
            SetVideoEditDuration(delayed, 6023);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(delayed, image));
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task RealKachincoCpuExportWithUnspecifiedPixelAspectCanBeRetitled()
    {
        string dir = NewDirectory();
        try
        {
            string source = Path.Combine(dir, "cpu.mp4"), image = Path.Combine(dir, "title.png");
            var request = new EncodingRequest(Guid.NewGuid(), source, ExportPreset.YoutubeH264AacMp4,
                SequenceSettings.Landscape, 4, 48000, 2, 6400);
            var encoded = await new FfmpegEncodingBackend().EncodeAsync(request, new FourBlueFrames(), new(null, VideoEncoderPreference.Cpu), null, default);
            Assert.AreEqual(ExportStage.Completed, encoded.Stage, string.Join(";", encoded.Diagnostics.Select(d => d.Message)));
            await Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=red:s=160x90", "-frames:v", "1", "-threads", "1", image]);
            var service = new Mp4TitleReplacement(); var plan = await service.PrepareAsync(source, image);
            Assert.AreEqual(4, plan.FrameCount);
            string output = Path.Combine(dir, "title.mp4"); await service.ReplaceAsync(plan, output);
            CollectionAssert.AreEqual(await PacketTimes(source), await PacketTimes(output));
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class FourBlueFrames : IRenderedMediaSource
    {
        public async IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            byte[] bytes = new byte[1920 * 1080 * 4];
            for (int i = 0; i < bytes.Length; i += 4) { bytes[i + 2] = 255; bytes[i + 3] = 255; }
            var rgba = ImmutableArray.CreateRange(bytes);
            for (int i = 0; i < 4; i++) { token.ThrowIfCancellationRequested(); yield return new(i, TimelineTime.FrameToTicks(i, new(30, 1)), 1920, 1080, rgba); }
            await Task.CompletedTask;
        }
        public async IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            yield return new(0, 48000, 2, ImmutableArray.CreateRange(new float[12800]));
            await Task.CompletedTask;
        }
    }
    // Patch only a small generated fixture's first video edit-list duration. Packet
    // bytes and sample timing stay untouched, reproducing the reported NVENC header.
    private static void SetVideoEditDuration(string path, uint milliseconds)
    {
        byte[] data = File.ReadAllBytes(path);
        (int Offset, int Size) Child(int start, int end, string kind)
        {
            for (int at = start; at + 8 <= end;)
            {
                int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4)));
                Assert.IsTrue(size >= 8 && size <= end - at);
                if (System.Text.Encoding.ASCII.GetString(data, at + 4, 4) == kind) return (at, size);
                at += size;
            }
            throw new AssertFailedException("Missing fixture box: " + kind);
        }
        var box = Child(0, data.Length, "moov");
        foreach (string kind in new[] { "trak", "edts", "elst" }) box = Child(box.Offset + 8, box.Offset + box.Size, kind);
        Assert.AreEqual((byte)0, data[box.Offset + 8]);
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.Offset + 12, 4)));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(box.Offset + 16, 4), milliseconds);
        File.WriteAllBytes(path, data);
    }
    private sealed class CallbackProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kachinco title ' test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static async Task<(string Source, string Image)> Fixture(string dir, string fps, int bf, int count, int gop, bool audio, bool openGop = false)
    {
        string source = Path.Combine(dir, "source.mp4"), image = Path.Combine(dir, "title.png");
        string[] args = ["-v", "error", "-f", "lavfi", "-i", $"testsrc2=s=160x90:r={fps}"];
        if (audio) args = [.. args, "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"];
        await Run("ffmpeg", [.. args, "-frames:v", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264",
            "-preset", "fast", "-crf", "18", "-bf", bf.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-g", gop.ToString(System.Globalization.CultureInfo.InvariantCulture), "-sc_threshold", "0", "-x264-params", "open-gop=" + (openGop ? "1" : "0"),
            .. (audio ? new[] { "-c:a", "aac", "-shortest" } : Array.Empty<string>()), source]);
        await Run("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "color=red:s=160x90", "-frames:v", "1", "-threads", "1", image]);
        return (source, image);
    }
    private static async Task<string[]> FrameHashes(string path)
    {
        string text = System.Text.Encoding.UTF8.GetString(await Run("ffmpeg", ["-v", "error", "-i", path, "-map", "0:v:0", "-f", "framemd5", "-"]));
        return text.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
    }
    private static async Task<string[]> PacketTimes(string path)
    {
        using var doc = JsonDocument.Parse(await Run("ffprobe", ["-v", "error", "-show_packets", "-show_entries", "packet=stream_index,pts,dts,duration", "-of", "json", path]));
        return doc.RootElement.GetProperty("packets").EnumerateArray().Select(p =>
            // Re-encoded opening DTS ordering can change, but all presentation timestamps must match.
            $"{p.GetProperty("stream_index")}:{p.GetProperty("pts")}:{p.GetProperty("duration")}").Order().ToArray();
    }
    private static async Task<byte[]> Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info }; process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        using var bytes = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes, timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); await output;
        Assert.AreEqual(0, process.ExitCode, await error);
        return bytes.ToArray();
    }
}
