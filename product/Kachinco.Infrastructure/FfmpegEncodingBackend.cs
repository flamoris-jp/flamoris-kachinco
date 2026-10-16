using System.Diagnostics;
using System.Collections.Immutable;
using Kachinco.Native;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class FfmpegEncodingBackend : IVideoEncodingBackend
{
    public async Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media, FfmpegSettings settings,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        string? work = null;
        var total = Stopwatch.StartNew();
        double videoRenderMs = 0, videoPipelineMs = 0, audioMs = 0, muxMs = 0;
        bool hardware = false;
        string? fallback = null;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ExportMetrics Metrics() => new(hardware ? "h264_nvenc" : "libx264", hardware ? settings.NvencDevice : null,
            fallback, total.Elapsed.TotalMilliseconds, videoRenderMs, videoPipelineMs, audioMs, muxMs);
        ExportResult Complete(string output) => new(request.JobId, ExportStage.Completed, output, diagnostics.ToImmutable(), Metrics());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FrameCount <= 0 || request.AudioSampleCount <= 0 || request.AudioSampleRate != 48000 || request.AudioChannels != 2 ||
                !request.VideoSettings.FrameRate.IsValid || !Enum.IsDefined(request.Preset) ||
                !Enum.IsDefined(settings.Encoder) || settings.NvencDevice < 0 ||
                !ValidSize(request) ||
                !Path.GetExtension(request.OutputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                return Fail(request, "INVALID_ENCODING_REQUEST", "Expected landscape/portrait H.264 MP4 with 48 kHz stereo audio.");
            string output = Path.GetFullPath(request.OutputPath);
            work = Path.Combine(Path.GetDirectoryName(output)!, ".kachinco-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var videoPath = Path.Combine(work, "video.mp4"); var audioPath = Path.Combine(work, "audio.f32"); var finalPath = Path.Combine(work, "complete.mp4");
            string executable = string.IsNullOrWhiteSpace(settings.ExecutablePath) ? "ffmpeg" : settings.ExecutablePath;
            if (settings.Encoder == VideoEncoderPreference.Nvenc || settings.Encoder == VideoEncoderPreference.Auto && OperatingSystem.IsWindows())
            {
                fallback = await ProbeNvencAsync(executable, request, settings.NvencDevice, cancellationToken);
                hardware = fallback is null;
            }
            else if (settings.Encoder == VideoEncoderPreference.Auto) fallback = "Automatic NVENC selection is Windows-only.";
            if (fallback is not null) diagnostics.Add(new("ENCODER_FALLBACK", DiagnosticSeverity.Info, fallback));
            var videoClock = Stopwatch.StartNew();
            try { videoRenderMs += await EncodeVideoAsync(executable, request, media, videoPath, hardware, settings.NvencDevice, progress, cancellationToken); }
            catch (VideoEncoderException e) when (hardware && !cancellationToken.IsCancellationRequested)
            {
                videoRenderMs += e.RenderMilliseconds;
                hardware = false; fallback = e.Message;
                diagnostics.Add(new("ENCODER_FALLBACK", DiagnosticSeverity.Warning, "NVENC failed; restarting video once through libx264. " + fallback));
                File.Delete(videoPath);
                progress?.Report(new(request.JobId, ExportStage.Rendering, 0, request.FrameCount, 0, "NVENC failed; CPU retry from frame zero."));
                videoRenderMs += await EncodeVideoAsync(executable, request, media, videoPath, false, settings.NvencDevice, progress, cancellationToken);
            }
            videoPipelineMs = videoClock.Elapsed.TotalMilliseconds;
            diagnostics.Add(new("ENCODER_SELECTED", DiagnosticSeverity.Info,
                hardware ? $"h264_nvenc: GPU {settings.NvencDevice}, p5 / hq / VBR-CQ 19 (not equivalent to x264 CRF 18)." : "libx264: fast / CRF 18."));
            if (request.Preset == ExportPreset.ClipPreviewH264Mp4)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(videoPath).Length > ClipDownloadStore.MaximumFileBytes) throw new InvalidDataException("download_size_limit");
                File.Move(videoPath, output, true);
                return Complete(output);
            }
            var audioClock = Stopwatch.StartNew();
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
            audioMs = audioClock.Elapsed.TotalMilliseconds;
            if (samples != request.AudioSampleCount) throw new InvalidDataException("Renderer returned too few audio samples.");
            progress?.Report(new(request.JobId, ExportStage.Encoding, request.FrameCount, request.FrameCount, samples));
            var muxClock = Stopwatch.StartNew();
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
                finally { await StopAsync(process, error); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            muxMs = muxClock.Elapsed.TotalMilliseconds;
            File.Move(finalPath, output, true);
            progress?.Report(new(request.JobId, ExportStage.Completed, request.FrameCount, request.FrameCount, samples));
            return Complete(output);
        }
        catch (OperationCanceledException) { return new(request.JobId, ExportStage.Cancelled, null, diagnostics.ToImmutable(), Metrics()); }
        catch (System.ComponentModel.Win32Exception e) { return Fail(request, "FFMPEG_NOT_FOUND", e.Message); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException or TimeoutException)
        { return cancellationToken.IsCancellationRequested ? new(request.JobId, ExportStage.Cancelled, null, []) : new(request.JobId, ExportStage.Failed, null, [Diagnostic.Error("ENCODING_FAILED", e.Message), .. diagnostics], Metrics()); }
        finally { if (work is not null) try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static string[] EncoderArguments(bool hardware, int device) => hardware
        ? ["-c:v", "h264_nvenc", "-gpu", device.ToString(System.Globalization.CultureInfo.InvariantCulture), "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0", "-pix_fmt", "yuv420p"]
        : ["-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p"];

    private static async Task<string?> ProbeNvencAsync(string executable, EncodingRequest request, int device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var fps = request.VideoSettings.FrameRate;
        using var process = NativeMediaProcess.Start(executable,
            ["-v", "error", "-nostdin", "-f", "lavfi", "-i", $"color=c=black:s={request.VideoSettings.Width}x{request.VideoSettings.Height}:r={fps.Numerator}/{fps.Denominator}",
             "-frames:v", "3", "-an", .. EncoderArguments(true, device), "-f", "null", "-"]);
        using var cancel = deadline.Token.Register(() => MediaProcess.Kill(process));
        var error = MediaProcess.DrainErrorAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string detail = await error;
            token.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? null : "NVENC initialization encode failed: " + detail;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return "NVENC initialization encode timed out."; }
        finally { MediaProcess.Kill(process); try { await error; } catch (OperationCanceledException) { } }
    }

    private static async Task<double> EncodeVideoAsync(string executable, EncodingRequest request, IRenderedMediaSource media,
        string path, bool hardware, int device, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fps = request.VideoSettings.FrameRate;
        var info = MediaProcess.StartInfo(executable,
            ["-v", "error", "-nostdin", "-y", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{request.VideoSettings.Width}x{request.VideoSettings.Height}",
             "-r", $"{fps.Numerator}/{fps.Denominator}", "-i", "pipe:0", "-an", .. EncoderArguments(hardware, device), path]);
        info.RedirectStandardInput = true;
        using var process = new Process { StartInfo = info };
        process.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var cancel = deadline.Token.Register(() => MediaProcess.Kill(process));
        var error = MediaProcess.DrainErrorAsync(process.StandardError, token);
        double renderMs = 0;
        try
        {
            long count = 0;
            await using var frames = media.ReadVideoAsync(token).GetAsyncEnumerator(token);
            for (;;)
            {
                long started = Stopwatch.GetTimestamp();
                bool next = await frames.MoveNextAsync();
                renderMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!next) break;
                var frame = frames.Current;
                if (count >= request.FrameCount || frame.FrameIndex != count || frame.Width != request.VideoSettings.Width || frame.Height != request.VideoSettings.Height ||
                    frame.Tick != TimelineTime.FrameToTicks(count, fps) || frame.Rgba8.Length != checked(frame.Width * frame.Height * 4))
                    throw new InvalidDataException("Renderer video sequence does not match encoding plan.");
                try
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    await process.StandardInput.BaseStream.WriteAsync(frame.Rgba8.ToArray(), deadline.Token);
                    deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (Exception e) when (e is IOException || e is OperationCanceledException && !token.IsCancellationRequested)
                { throw new VideoEncoderException("Video encoder pipe failed or stalled.", e, renderMs); }
                count++;
                if (request.Preset == ExportPreset.ClipPreviewH264Mp4 && File.Exists(path) && new FileInfo(path).Length > ClipDownloadStore.MaximumFileBytes)
                    throw new InvalidDataException("download_size_limit");
                progress?.Report(new(request.JobId, ExportStage.Rendering, count, request.FrameCount, 0, hardware ? "h264_nvenc" : "libx264"));
            }
            if (count != request.FrameCount) throw new InvalidDataException("Renderer returned too few frames.");
            process.StandardInput.Close();
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException e) when (!token.IsCancellationRequested)
            { throw new VideoEncoderException("Video encoder drain timed out.", e, renderMs); }
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new VideoEncoderException("Video encoding failed: " + await error, null, renderMs);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new VideoEncoderException("Video encoder produced no output.", null, renderMs);
            return renderMs;
        }
        catch (VideoEncoderException e)
        {
            await StopAsync(process, error);
            string detail;
            try { detail = await error; } catch (OperationCanceledException) { detail = "Cancelled."; }
            throw new VideoEncoderException(e.Message + " " + detail, e, e.RenderMilliseconds);
        }
        finally { await StopAsync(process, error); }
    }
    private static async Task StopAsync(Process process, Task<string> error)
    {
        MediaProcess.Kill(process);
        // Join the encoder before deleting/reusing its temporary output (Windows locks).
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try { await error; } catch (OperationCanceledException) { }
    }
    private sealed class VideoEncoderException(string message, Exception? inner, double renderMs) : IOException(message, inner)
    { public double RenderMilliseconds { get; } = renderMs; }

    private static bool ValidSize(EncodingRequest request) =>
        request.Preset == ExportPreset.ClipPreviewH264Mp4
            ? new[] { (1920,1080), (1080,1920), (960,540), (540,960), (480,270), (270,480) }.Contains((request.VideoSettings.Width, request.VideoSettings.Height))
            : (request.VideoSettings.Width, request.VideoSettings.Height) is (1920,1080) or (1080,1920);
    private static ExportResult Fail(EncodingRequest request, string code, string message) => new(request.JobId, ExportStage.Failed, null, [Diagnostic.Error(code, message)]);
}
