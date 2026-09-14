using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public interface IMediaDecoder
{
    Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token);
    Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token);
}

// Each request is independently seekable. Sequential caching is derived future optimization.
public sealed class FfmpegMediaDecoder(string executable = "ffmpeg") : IMediaDecoder
{
    public async Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token)
    {
        int size = checked(width * height * 4);
        var bytes = await MediaProcess.ReadAsync(executable,
            ["-v", "error", "-nostdin", "-ss", Seconds(sourceTicks), "-i", Path.GetFullPath(path), "-map", "0:v:0",
             "-an", "-frames:v", "1", "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,format=rgba,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black@0",
             "-threads", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"], size, token);
        if (bytes.Length != size) throw new InvalidDataException("Decoder returned an incomplete video frame.");
        return ImmutableArray.CreateRange(bytes);
    }

    public async Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token)
    {
        int size = checked(count * channels * 4);
        var bytes = await MediaProcess.ReadAsync(executable,
            ["-v", "error", "-nostdin", "-ss", Seconds(sourceTicks), "-i", Path.GetFullPath(path), "-map", "0:a:0",
             "-vn", "-af", $"aresample={rate},atrim=end_sample={count}", "-ac", channels.ToString(CultureInfo.InvariantCulture),
             "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1"], size, token);
        if (bytes.Length % (4 * channels) != 0) throw new InvalidDataException("Decoder returned partial PCM samples.");
        var samples = new float[count * channels];
        for (int i = 0; i < bytes.Length / 4; i++)
        {
            var value = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
            if (!float.IsFinite(value)) throw new InvalidDataException("Decoder returned non-finite PCM.");
            samples[i] = value;
        }
        return ImmutableArray.CreateRange(samples);
    }
    internal static string Seconds(long ticks) => ((decimal)ticks / TimelineTime.TicksPerSecond).ToString("0.################", CultureInfo.InvariantCulture);
}

internal static class MediaProcess
{
    internal static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    // Process redirection uses blocking anonymous-pipe reads on Windows. Long-lived stderr
    // drains must not occupy the same ThreadPool that schedules frame/PCM requests.
    // Each bounded child process owns one dedicated reader; killing it closes the pipe.
    internal static Task<string> DrainErrorAsync(StreamReader reader, CancellationToken token) =>
        Task.Factory.StartNew(() =>
        {
            var result = new StringBuilder(); var buffer = new char[4096]; int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (result.Length < 4096) result.Append(buffer, 0, Math.Min(read, 4096 - result.Length));
            }
            return result.ToString();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    internal static async Task<byte[]> ReadAsync(string executable, IEnumerable<string> args, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = StartInfo(executable, args) };
        process.StartInfo.RedirectStandardOutput = true;
        process.Start();
        using var cancellation = timeout.Token.Register(() => Kill(process));
        var error = DrainErrorAsync(process.StandardError, timeout.Token);
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[65536]; int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("Decoder output exceeds its request budget.");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(timeout.Token);
            var detail = await error;
            if (process.ExitCode != 0) throw new InvalidDataException("FFmpeg decode failed: " + detail);
            return output.ToArray();
        }
        finally
        {
            Kill(process);
            try { await error; } catch (OperationCanceledException) { }
        }
    }
}
