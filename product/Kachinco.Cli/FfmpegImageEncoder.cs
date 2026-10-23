using System.Diagnostics;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

public sealed class FfmpegImageEncoder : IPreviewImageEncoder
{
    public async Task<byte[]> EncodeAsync(RenderedVideoFrame frame, int width, int height, string format, int quality, CancellationToken token)
    {
        if (format is not ("png" or "jpeg") || width is < 1 or > 4096 || height is < 1 or > 4096 || quality is < 1 or > 100)
            throw new ArgumentException("Invalid image output.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = new("ffmpeg") {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        string[] args = ["-v", "error", "-nostdin", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", $"{frame.Width}x{frame.Height}",
            "-i", "pipe:0", "-vf", $"scale={width}:{height}", "-frames:v", "1", "-threads", "1",
            "-c:v", format == "png" ? "png" : "mjpeg", "-q:v", Math.Clamp(31 - quality * 29 / 100, 2, 31).ToString(), "-f", "image2pipe", "pipe:1"];
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var cancel = deadline.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = DrainError(process.StandardError, deadline.Token);
        var writer = WriteInput();
        using var output = new MemoryStream();
        try
        {
            byte[] buffer = new byte[65536]; int n;
            while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (output.Length + n > 1024 * 1024) throw new PreviewImageTooLargeException();
                output.Write(buffer, 0, n);
            }
            await writer; await process.WaitForExitAsync(deadline.Token); await stderr;
            deadline.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || output.Length == 0) throw new IOException("FFmpeg image encoding failed.");
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await writer; } catch { }
            try { await stderr; } catch { }
        }
        async Task WriteInput()
        {
            try { await process.StandardInput.BaseStream.WriteAsync(frame.Rgba8.ToArray(), deadline.Token); }
            finally { process.StandardInput.Close(); }
        }
    }
    private static async Task DrainError(StreamReader reader, CancellationToken token)
    { char[] chars = new char[4096]; while (await reader.ReadAsync(chars, token) != 0) { } }
}
