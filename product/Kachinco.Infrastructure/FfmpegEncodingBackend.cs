using System.Diagnostics;
using System.Globalization;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class FfmpegEncodingBackend : IVideoEncodingBackend
{
    public async Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media, FfmpegSettings settings,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        string? work = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FrameCount <= 0 || request.AudioSampleCount <= 0 || request.AudioSampleRate != 48000 || request.AudioChannels != 2 ||
                !request.VideoSettings.FrameRate.IsValid || !Enum.IsDefined(request.Preset) ||
                (request.VideoSettings.Width, request.VideoSettings.Height) is not ((1920, 1080) or (1080, 1920)) ||
                !Path.GetExtension(request.OutputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                return Fail(request, "INVALID_ENCODING_REQUEST", "Expected landscape/portrait H.264 MP4 with 48 kHz stereo audio.");
            string output = Path.GetFullPath(request.OutputPath);
            work = Path.Combine(Path.GetDirectoryName(output)!, ".kachinco-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var videoPath = Path.Combine(work, "video.mp4"); var audioPath = Path.Combine(work, "audio.f32"); var finalPath = Path.Combine(work, "complete.mp4");
            string executable = string.IsNullOrWhiteSpace(settings.ExecutablePath) ? "ffmpeg" : settings.ExecutablePath;
            var fps = request.VideoSettings.FrameRate;
            var info = MediaProcess.StartInfo(executable,
                ["-v", "error", "-nostdin", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{request.VideoSettings.Width}x{request.VideoSettings.Height}",
                 "-r", $"{fps.Numerator}/{fps.Denominator}", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p", videoPath]);
            info.RedirectStandardInput = true;
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                using var cancel = cancellationToken.Register(() => MediaProcess.Kill(process));
                var error = MediaProcess.DrainErrorAsync(process.StandardError, cancellationToken);
                try
                {
                    long count = 0;
                    await foreach (var frame in media.ReadVideoAsync(cancellationToken))
                    {
                        if (count >= request.FrameCount || frame.FrameIndex != count || frame.Width != request.VideoSettings.Width || frame.Height != request.VideoSettings.Height ||
                            frame.Tick != TimelineTime.FrameToTicks(count, fps) || frame.Rgba8.Length != checked(frame.Width * frame.Height * 4))
                            throw new InvalidDataException("Renderer video sequence does not match encoding plan.");
                        await process.StandardInput.BaseStream.WriteAsync(frame.Rgba8.ToArray(), cancellationToken);
                        count++;
                        progress?.Report(new(request.JobId, ExportStage.Rendering, count, request.FrameCount, 0));
                    }
                    if (count != request.FrameCount) throw new InvalidDataException("Renderer returned too few frames.");
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode != 0) throw new InvalidDataException("Video encoding failed: " + await error);
                }
                finally
                {
                    MediaProcess.Kill(process);
                    try { await error; } catch (OperationCanceledException) { }
                }
            }
            long samples = 0;
            await using (var stream = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await foreach (var block in media.ReadAudioAsync(cancellationToken))
                {
                    if (block.FirstSample != samples || block.SampleRate != 48000 || block.Channels != 2 || block.Samples.IsDefaultOrEmpty || block.Samples.Length % 2 != 0 ||
                        block.Samples.Length / 2 > request.AudioSampleCount - samples)
                        throw new InvalidDataException("Renderer PCM sequence does not match encoding plan.");
                    var bytes = new byte[checked(block.Samples.Length * 4)];
                    for (int i = 0; i < block.Samples.Length; i++)
                    {
                        float value = block.Samples[i];
                        if (!float.IsFinite(value) || value < -1 || value > 1) throw new InvalidDataException("Invalid mixed PCM.");
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), BitConverter.SingleToInt32Bits(value));
                    }
                    await stream.WriteAsync(bytes, cancellationToken);
                    samples += block.Samples.Length / 2;
                    progress?.Report(new(request.JobId, ExportStage.Rendering, request.FrameCount, request.FrameCount, samples));
                }
            }
            if (samples != request.AudioSampleCount) throw new InvalidDataException("Renderer returned too few audio samples.");
            progress?.Report(new(request.JobId, ExportStage.Encoding, request.FrameCount, request.FrameCount, samples));
            using (var process = new Process { StartInfo = MediaProcess.StartInfo(executable,
                ["-v", "error", "-nostdin", "-y", "-i", videoPath, "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audioPath,
                 "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", finalPath]) })
            {
                process.Start();
                using var cancel = cancellationToken.Register(() => MediaProcess.Kill(process));
                var error = MediaProcess.DrainErrorAsync(process.StandardError, cancellationToken);
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode != 0) throw new InvalidDataException("Mux failed: " + await error);
                }
                finally { MediaProcess.Kill(process); try { await error; } catch (OperationCanceledException) { } }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(finalPath, output, true);
            progress?.Report(new(request.JobId, ExportStage.Completed, request.FrameCount, request.FrameCount, samples));
            return new(request.JobId, ExportStage.Completed, output, []);
        }
        catch (OperationCanceledException) { return new(request.JobId, ExportStage.Cancelled, null, []); }
        catch (System.ComponentModel.Win32Exception e) { return Fail(request, "FFMPEG_NOT_FOUND", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        { return cancellationToken.IsCancellationRequested ? new(request.JobId, ExportStage.Cancelled, null, []) : Fail(request, "ENCODING_FAILED", e.Message); }
        finally { if (work is not null) try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static ExportResult Fail(EncodingRequest request, string code, string message) => new(request.JobId, ExportStage.Failed, null, [Diagnostic.Error(code, message)]);
}
