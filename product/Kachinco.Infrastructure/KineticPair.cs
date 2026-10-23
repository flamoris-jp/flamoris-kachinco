using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Native;

namespace Kachinco.Infrastructure;

public static class KineticPair
{
    public static async Task<string> ExportAsync(JsonObject input, string destination, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var doc = KineticComposition.Validate(input);
        int width = doc["width"]!.GetValue<int>(), height = doc["height"]!.GetValue<int>();
        var fps = new FrameRate(doc["fpsNumerator"]!.GetValue<int>(), doc["fpsDenominator"]!.GetValue<int>());
        long duration = KineticComposition.Duration(doc), frames = TimelineTime.FrameCount(duration, fps);
        string final = Path.GetFullPath(destination);
        if (Directory.Exists(final) || File.Exists(final)) throw new IOException("Choose a new output folder.");
        string temporary = final + "." + Guid.NewGuid().ToString("N") + ".partial";
        Directory.CreateDirectory(temporary);
        try
        {
            string rgbPath = Path.Combine(temporary, "text_rgb.mp4"), maskPath = Path.Combine(temporary, "text_mask.mp4");
            using var renderer = new NativeKineticRenderer();
            using var rgb = Start(rgbPath, false, width, height, fps);
            try {
            using var mask = Start(maskPath, true, width, height, fps);
            using var cancel = token.Register(() => { MediaProcess.Kill(rgb); MediaProcess.Kill(mask); });
            var rgbError = MediaProcess.DrainErrorAsync(rgb.StandardError, CancellationToken.None);
            var maskError = MediaProcess.DrainErrorAsync(mask.StandardError, CancellationToken.None);
            try
            {
                string source = doc.ToJsonString(); var gray = new byte[checked(width * height)];
                for (long frame = 0; frame < frames; ++frame)
                {
                    token.ThrowIfCancellationRequested();
                    var pixels = renderer.Render(source, TimelineTime.FrameToTicks(frame, fps), width, height);
                    for (int i = 0; i < gray.Length; ++i) gray[i] = pixels.Mask[i * 3];
                    await rgb.StandardInput.BaseStream.WriteAsync(pixels.Rgb, token);
                    await mask.StandardInput.BaseStream.WriteAsync(gray, token);
                    progress?.Report((double)(frame + 1) / frames);
                }
                rgb.StandardInput.Close(); mask.StandardInput.Close();
                await Task.WhenAll(rgb.WaitForExitAsync(token), mask.WaitForExitAsync(token));
                var errors = await Task.WhenAll(rgbError, maskError);
                token.ThrowIfCancellationRequested();
                if (rgb.ExitCode != 0 || mask.ExitCode != 0) throw new IOException("RGB/mask encoding failed: " + string.Join("; ", errors));
                var manifest = new JsonObject {
                    ["format"] = "flamoris-kinetic-pair", ["version"] = 1, ["pairId"] = Guid.NewGuid().ToString(),
                    ["width"] = width, ["height"] = height, ["fpsNumerator"] = fps.Numerator, ["fpsDenominator"] = fps.Denominator,
                    ["originTicks"] = "0", ["durationTicks"] = duration.ToString(CultureInfo.InvariantCulture), ["frameCount"] = frames.ToString(CultureInfo.InvariantCulture),
                    ["rgbAssociation"] = "premultiplied-black", ["maskRange"] = "full", ["color"] = "SDR-BT709-full",
                    ["rgb"] = await EntryAsync(rgbPath, token), ["mask"] = await EntryAsync(maskPath, token),
                    ["composition"] = doc.DeepClone(), ["renderer"] = renderer.Info
                };
                // Verify decoded stream timing/counts before publishing even our own output.
                await VerifyStreamAsync(rgbPath, width, height, fps, frames, token);
                await VerifyStreamAsync(maskPath, width, height, fps, frames, token);
                KineticComposition.WriteAtomic(Path.Combine(temporary, "text.ktpair.json"), manifest.ToJsonString(new() { WriteIndented = true }));
                token.ThrowIfCancellationRequested(); Directory.Move(temporary, final);
                return Path.Combine(final, "text.ktpair.json");
            }
            finally
            {
                MediaProcess.Kill(rgb); MediaProcess.Kill(mask);
                await Task.WhenAll(rgb.WaitForExitAsync(), mask.WaitForExitAsync());
                await Task.WhenAll(rgbError, maskError);
            }
            } finally { MediaProcess.Kill(rgb); await rgb.WaitForExitAsync(); }
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private static Process Start(string path, bool mask, int width, int height, FrameRate fps)
    {
        var info = MediaProcess.StartInfo("ffmpeg", ["-v", "error", "-nostdin", "-n", "-f", "rawvideo", "-pixel_format", mask ? "gray" : "rgb24",
            "-video_size", $"{width}x{height}", "-framerate", $"{fps.Numerator}/{fps.Denominator}", "-i", "pipe:0", "-an", "-c:v", "libx264",
            "-preset", "fast", "-crf", mask ? "0" : "18", "-pix_fmt", mask ? "gray" : "yuv444p", "-color_range", "pc",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-threads", "2", "-movflags", "+faststart", path]);
        info.RedirectStandardInput = true;
        return Process.Start(info) ?? throw new IOException("Cannot start FFmpeg.");
    }
    private static async Task<JsonObject> EntryAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        return new() { ["file"] = Path.GetFileName(path), ["sha256"] = hash, ["bytes"] = file.Length.ToString(CultureInfo.InvariantCulture) };
    }
    public static async Task<MediaAsset> ImportAsync(string manifestPath, CancellationToken token = default)
    {
        string full = Path.GetFullPath(manifestPath);
        if (new FileInfo(full).Length > KineticComposition.MaximumBytes) throw new InvalidDataException("Pair manifest too large.");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(full, token))!.AsObject();
        if (root["format"]?.GetValue<string>() != "flamoris-kinetic-pair" || root["version"]?.GetValue<int>() != 1 ||
            root["rgbAssociation"]?.GetValue<string>() != "premultiplied-black" || root["maskRange"]?.GetValue<string>() != "full" ||
            root["color"]?.GetValue<string>() != "SDR-BT709-full" || root["originTicks"]?.GetValue<string>() != "0" || !Guid.TryParse(root["pairId"]?.GetValue<string>(), out _))
            throw new InvalidDataException("Unsupported mask pair contract.");
        var composition = KineticComposition.Validate(root["composition"]!.AsObject());
        int width = root["width"]!.GetValue<int>(), height = root["height"]!.GetValue<int>();
        var fps = FrameRate.Create(root["fpsNumerator"]!.GetValue<int>(), root["fpsDenominator"]!.GetValue<int>());
        long duration = KineticComposition.Tick(root, "durationTicks"), frames = KineticComposition.Tick(root, "frameCount");
        if (duration != KineticComposition.Duration(composition) || frames != TimelineTime.FrameCount(duration, fps) || width != composition["width"]!.GetValue<int>() || height != composition["height"]!.GetValue<int>()) throw new InvalidDataException("Mask pair metadata mismatch.");
        string directory = Path.GetDirectoryName(full)!;
        async Task<string> ReadEntry(string key)
        {
            var entry = root[key]!.AsObject(); string name = entry["file"]!.GetValue<string>();
            if (name != Path.GetFileName(name) || name.Contains('\\') || name is "." or ".." || !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pair files must be adjacent MP4 names.");
            string path = Path.Combine(directory, name); var actual = await EntryAsync(path, token);
            if (actual["sha256"]!.GetValue<string>() != entry["sha256"]!.GetValue<string>() || actual["bytes"]!.GetValue<string>() != entry["bytes"]!.GetValue<string>()) throw new InvalidDataException("Mask pair checksum mismatch.");
            await VerifyStreamAsync(path, width, height, fps, frames, token); return path;
        }
        var rgb = await ReadEntry("rgb"); var mask = await ReadEntry("mask");
        if (rgb == mask) throw new InvalidDataException("RGB and mask must be different files.");
        return new(Guid.NewGuid(), Path.GetFileName(directory), rgb, MediaKind.MaskedVideo, duration) { MaskSourcePath = mask };
    }
    private static async Task VerifyStreamAsync(string path, int width, int height, FrameRate fps, long frames, CancellationToken token)
    {
        // Frame PTS are checked as a bounded streaming ffprobe output, not loaded for a song.
        using var process = new Process { StartInfo = MediaProcess.StartInfo("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate,nb_frames,color_range:frame=best_effort_timestamp_time", "-of", "compact=p=1:nk=0", path]) };
        process.StartInfo.RedirectStandardOutput = true; process.Start();
        using var cancel = token.Register(() => MediaProcess.Kill(process));
        var errors = MediaProcess.DrainErrorAsync(process.StandardError, CancellationToken.None); long count = 0; bool stream = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (line.Length > 4096) throw new InvalidDataException("Unbounded pair probe output.");
                var fields = line.Split('|').Skip(1).Where(x => x.Contains('=')).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
                if (line.StartsWith("frame|", StringComparison.Ordinal))
                {
                    if (!fields.TryGetValue("best_effort_timestamp_time", out string? raw) || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) throw new InvalidDataException("Pair PTS missing.");
                    decimal expected = (decimal)count * fps.Denominator / fps.Numerator;
                    if (Math.Abs(seconds - expected) > 0.000002m || ++count > frames) throw new InvalidDataException("Mask pair is not synchronized CFR.");
                }
                if (line.StartsWith("stream|", StringComparison.Ordinal))
                {
                    if (fields.GetValueOrDefault("width") != width.ToString(CultureInfo.InvariantCulture) || fields.GetValueOrDefault("height") != height.ToString(CultureInfo.InvariantCulture) || fields.GetValueOrDefault("r_frame_rate") != $"{fps.Numerator}/{fps.Denominator}" || fields.GetValueOrDefault("color_range") != "pc") throw new InvalidDataException("Mask pair dimensions/rate/range mismatch.");
                    stream = true;
                }
            }
            await process.WaitForExitAsync(token); await errors;
            if (process.ExitCode != 0 || count != frames || !stream) throw new InvalidDataException("Incomplete mask pair stream.");
        }
        finally { MediaProcess.Kill(process); await process.WaitForExitAsync(); await errors; }
    }
}
