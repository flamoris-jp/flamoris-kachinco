using System.Diagnostics;
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
    [DataRow("30", 0, 5101, 60, true)]
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
