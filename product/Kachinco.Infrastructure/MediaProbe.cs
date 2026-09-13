using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed record MediaProbeInfo(
    string SourcePath,
    MediaKind Kind,
    long DurationTicks,
    int? SampleRate,
    int? Channels,
    int? Width,
    int? Height,
    FrameRate? FrameRate,
    ImmutableArray<string> Codecs)
{
    public MediaAsset ToMediaAsset(Guid id, string? displayName = null) =>
        new(id, displayName ?? Path.GetFileName(SourcePath), SourcePath, Kind,
            DurationTicks, SampleRate, Channels);
}

public interface IMediaProbe
{
    Task<Result<MediaProbeInfo>> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class FfprobeMediaProbe(string? configuredExecutable = null) : IMediaProbe
{
    private const int MaxProbeOutputChars = 1024 * 1024;

    public async Task<Result<MediaProbeInfo>> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return Failure("MEDIA_PATH_REQUIRED", "Choose a MOV or WAV file.");
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("INVALID_MEDIA_PATH", "The media path is invalid.");
        }

        if (!File.Exists(fullPath)) return Failure("MEDIA_NOT_FOUND", "The selected media file does not exist.", path: fullPath);
        if (!TryKind(fullPath, out _)) return Failure("UNSUPPORTED_MEDIA_SOURCE", "Only MOV and WAV files are supported.", path: fullPath);

        using var process = new Process
        {
            StartInfo = BuildStartInfo(configuredExecutable, fullPath),
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start()) return Failure("FFPROBE_START_FAILED", "ffprobe could not be started.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failure("FFPROBE_NOT_FOUND", "ffprobe is not available. Configure FFmpeg or add ffprobe to PATH.");
        }

        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        });

        try
        {
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxProbeOutputChars, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxProbeOutputChars, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                return Failure("MEDIA_PROBE_FAILED", CleanError(stderr, "ffprobe could not inspect this media file."), path: fullPath);
            return MediaProbeParser.Parse(fullPath, stdout);
        }
        catch (OperationCanceledException)
        {
            return Failure("CANCELLED", "Media probing was cancelled.", path: fullPath);
        }
        catch (InvalidDataException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return Failure("MEDIA_PROBE_OUTPUT_TOO_LARGE", "ffprobe returned an unexpectedly large response.", path: fullPath);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return Failure("MEDIA_PROBE_FAILED", "ffprobe could not inspect this media file.", path: fullPath);
        }
    }

    private static ProcessStartInfo BuildStartInfo(string? configuredExecutable, string fullPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(configuredExecutable) ? "ffprobe" : configuredExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-show_entries",
            "format=duration:stream=codec_type,codec_name,width,height,r_frame_rate,sample_rate,channels,duration",
            "-of", "json", fullPath
        }) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (builder.Length + read > limit) throw new InvalidDataException("Probe output exceeds limit.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static string CleanError(string stderr, string fallback)
    {
        var value = stderr.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrEmpty(value) ? fallback : value[..Math.Min(value.Length, 512)];
    }

    internal static bool TryKind(string path, out MediaKind kind)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)) { kind = MediaKind.Mov; return true; }
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)) { kind = MediaKind.Wav; return true; }
        kind = default;
        return false;
    }

    private static Result<MediaProbeInfo> Failure(string code, string message, string? path = null) =>
        Result<MediaProbeInfo>.Fail(Diagnostic.Error(code, message, path: path));
}

public static class MediaProbeParser
{
    public static Result<MediaProbeInfo> Parse(string sourcePath, string ffprobeJson)
    {
        if (!FfprobeMediaProbe.TryKind(sourcePath, out var expectedKind))
            return Fail("UNSUPPORTED_MEDIA_SOURCE", "Only MOV and WAV files are supported.", sourcePath);
        try
        {
            using var document = JsonDocument.Parse(ffprobeJson, new() { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                return Fail("MEDIA_PROBE_INVALID", "ffprobe did not return a stream list.", sourcePath);

            bool hasVideo = false, hasAudio = false;
            int? sampleRate = null, channels = null, width = null, height = null;
            FrameRate? frameRate = null;
            decimal? durationSeconds = ReadDuration(root.TryGetProperty("format", out var format) ? format : default);
            var codecs = ImmutableArray.CreateBuilder<string>();
            foreach (var stream in streams.EnumerateArray())
            {
                var type = Text(stream, "codec_type");
                var codec = Text(stream, "codec_name");
                if (!string.IsNullOrWhiteSpace(codec)) codecs.Add(codec);
                var streamDuration = ReadDuration(stream);
                if (streamDuration is > 0 && (durationSeconds is null || streamDuration > durationSeconds)) durationSeconds = streamDuration;
                if (type == "video")
                {
                    hasVideo = true;
                    width ??= Integer(stream, "width");
                    height ??= Integer(stream, "height");
                    frameRate ??= Rational(stream, "r_frame_rate");
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                    sampleRate ??= Integer(stream, "sample_rate");
                    channels ??= Integer(stream, "channels");
                }
            }

            if ((expectedKind == MediaKind.Mov && !hasVideo) || (expectedKind == MediaKind.Wav && !hasAudio))
                return Fail("MEDIA_KIND_MISMATCH", "The file contents do not match the MOV/WAV extension.", sourcePath);
            if (durationSeconds is null or <= 0)
                return Fail("MEDIA_DURATION_MISSING", "The media duration could not be determined.", sourcePath);
            long durationTicks = TimelineTime.SecondsToTicks(durationSeconds.Value);
            if (durationTicks <= 0) return Fail("MEDIA_DURATION_MISSING", "The media duration is too short for the project timebase.", sourcePath);
            return Result<MediaProbeInfo>.Ok(new(Path.GetFullPath(sourcePath), expectedKind, durationTicks,
                sampleRate, channels, width, height, frameRate, codecs.Distinct(StringComparer.Ordinal).ToImmutableArray()));
        }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException or ArgumentException)
        {
            return Fail("MEDIA_PROBE_INVALID", "ffprobe returned invalid or unsupported metadata.", sourcePath);
        }
    }

    private static decimal? ReadDuration(JsonElement element)
    {
        var text = Text(element, "duration");
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static int? Integer(JsonElement element, string property) =>
        int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static FrameRate? Rational(JsonElement element, string property)
    {
        var pieces = Text(element, property)?.Split('/', 2);
        if (pieces is not { Length: 2 } || !int.TryParse(pieces[0], out var numerator) || !int.TryParse(pieces[1], out var denominator)) return null;
        try { return FrameRate.Create(numerator, denominator); } catch (ArgumentOutOfRangeException) { return null; }
    }

    private static Result<MediaProbeInfo> Fail(string code, string message, string path) =>
        Result<MediaProbeInfo>.Fail(Diagnostic.Error(code, message, path: path));
}
