using Kachinco.Core;
using Kachinco.Native;

namespace Kachinco.Infrastructure;

// Preparation owns only unpublished WAV bytes. Registration/history is ordinary editing.
public sealed class Mp4AudioImport(string directory, IMediaProbe probe, string executable = "ffmpeg")
{
    public async Task<PreparedMp4Audio?> PrepareAsync(MediaProbeInfo video, CancellationToken token = default)
    {
        if (video.Kind != MediaKind.Mov || !Path.GetExtension(video.SourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || video.SampleRate is null || video.Channels is null) return null;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using (var process = NativeMediaProcess.Start(executable,
                ["-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe", "-format_whitelist", "mov",
                 "-i", video.SourcePath, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1",
                 "-c:a", "pcm_s16le", "-rf64", "auto", "-f", "wav", path]))
            {
                using var cancellation = timeout.Token.Register(() => MediaProcess.Kill(process));
                var error = MediaProcess.DrainErrorAsync(process.StandardError, CancellationToken.None);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    var detail = await error;
                    if (process.ExitCode != 0) throw new InvalidDataException("MP4 audio extraction failed: " + detail);
                }
                finally
                {
                    MediaProcess.Kill(process);
                    await error;
                }
            }
            var inspected = await probe.ProbeAsync(path, timeout.Token);
            if (!inspected.Success) throw new InvalidDataException(string.Join(" / ", inspected.Diagnostics.Select(d => d.Message)));
            token.ThrowIfCancellationRequested();
            return new(inspected.Value!.ToMediaAsset(Guid.NewGuid(), Path.GetFileNameWithoutExtension(video.SourcePath) + " — audio.wav"));
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

public sealed class PreparedMp4Audio(MediaAsset asset) : IDisposable
{
    private bool committed;
    public MediaAsset Asset { get; } = asset;
    public void Keep() => committed = true;
    public void Dispose()
    {
        if (committed) return;
        try { File.Delete(Asset.SourcePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
