using System.Collections.Immutable;
using Flamoris.Logging;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public interface ICaptionRasterizer
{
    ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token);
}

public sealed class SharedFrameRenderer(IMediaDecoder decoder, string? projectPath = null, ICaptionRasterizer? captions = null,
    FlamorisLogger? logger = null, PreviewRenderBackend? previewBackend = null) : IFrameRenderer
{
    public ValueTask<Result<RenderedVideoFrame>> RenderPreviewAsync(Project project, EvaluatedFrame frame, PreviewQuality quality, CancellationToken token)
    {
        int divisor = (int)quality;
        if (divisor is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(quality));
        var scaled = frame with { Settings = frame.Settings with { Width = frame.Settings.Width / divisor, Height = frame.Settings.Height / divisor },
            VideoLayers = [.. frame.VideoLayers.Select(l => l with { Appearance = l.Appearance with {
                Transform = l.Appearance.Transform with { X = l.Appearance.Transform.X / divisor, Y = l.Appearance.Transform.Y / divisor } } })] };
        return RenderAsync(project, scaled, TimelineTime.TicksToFrame(frame.Tick, frame.Settings.FrameRate), token);
    }
    public async ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long frameIndex, CancellationToken cancellationToken)
    {
        using var backendLease = previewBackend is null ? null : await previewBackend.EnterAsync(cancellationToken);
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int width = frame.Settings.Width, height = frame.Settings.Height;
            int byteCount = checked(width * height * 4);
            bool gpu = previewBackend?.TryBegin(width, height, frame.VideoLayers.Length + (frame.Captions.IsEmpty ? 0 : 1),
                frame.Captions.IsEmpty && (frame.VideoLayers.IsEmpty || frame.VideoLayers.Length == 1 && frame.VideoLayers[0].Appearance == ClipAppearance.Default)) == true;
            byte[]? output = gpu ? null : Black();
            List<(ImmutableArray<byte> Pixels, ClipAppearance Appearance)>? retained = gpu ? [] : null;
            var paths = MediaReferenceResolver.Inspect(project, projectPath).ToDictionary(x => x.MediaAssetId);
            foreach (var layer in frame.VideoLayers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[layer.MediaAssetId];
                if (!path.IsAvailable || path.ResolvedPath is null) return Result<RenderedVideoFrame>.Fail(Diagnostic.Error("MEDIA_MISSING", "Video source is missing.", layer.MediaAssetId));
                var pixels = await DecodeVideoAsync(path.ResolvedPath, layer, frame, width, height, cancellationToken);
                CompositeLayer(pixels, layer.Appearance);
            }
            if (!frame.Captions.IsEmpty)
            {
                if (captions is null) return Result<RenderedVideoFrame>.Fail(Diagnostic.Error("CAPTION_RENDERER_REQUIRED", "A caption rasterizer is required."));
                var pixels = await captions.RasterizeAsync(frame.Captions, width, height, cancellationToken);
                CompositeLayer(pixels, ClipAppearance.Default);
            }
            if (gpu)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output = previewBackend!.Read();
                    if (output.Length != byteCount) throw new InvalidDataException("GPU returned an incomplete RGBA frame.");
                }
                catch (Exception e) when (PreviewRenderBackend.IsGpuFailure(e)) { RecoverCpu(e); }
            }
            cancellationToken.ThrowIfCancellationRequested(); completed = true;
            return Result<RenderedVideoFrame>.Ok(new(frameIndex, frame.Tick, width, height,
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(output!)));

            byte[] Black()
            {
                var pixels = new byte[byteCount];
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }
            void CompositeLayer(ImmutableArray<byte> pixels, ClipAppearance appearance)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pixels.Length != byteCount) throw new InvalidDataException("Decoder returned an incomplete RGBA frame.");
                if (gpu)
                {
                    retained!.Add((pixels, appearance));
                    try
                    {
                        var t = appearance.Transform;
                        previewBackend!.Composite(pixels.AsSpan(), new(t.X, t.Y, t.ScaleX, t.ScaleY, t.RotationDegrees, appearance.Opacity, (int)appearance.Blend));
                    }
                    catch (Exception e) when (PreviewRenderBackend.IsGpuFailure(e)) { RecoverCpu(e); }
                }
                else Composite(output!, pixels, width, height, appearance, cancellationToken);
            }
            void RecoverCpu(Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                previewBackend!.FallBack(e.Message); gpu = false; output = Black();
                // Replay the exact decoded/evaluated inputs, including every prior layer.
                foreach (var input in retained!) Composite(output, input.Pixels, width, height, input.Appearance, cancellationToken);
                retained = null;
                logger?.Log(LogLevel.Warn, "preview.backend", "GPU preview fell back to native CPU composition",
                    new Dictionary<string, object?> { ["sequenceId"] = frame.SequenceId, ["timelineTick"] = frame.Tick, ["reason"] = e.Message }, e);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger?.Error("preview.decoder", "Video frame acquisition failed", e, new Dictionary<string, object?>
            {
                ["sequenceId"] = frame.SequenceId, ["timelineTick"] = frame.Tick,
            });
            return Result<RenderedVideoFrame>.Fail(Diagnostic.Error("FRAME_RENDER_FAILED", e.Message));
        }
        finally { if (!completed) previewBackend?.AbandonFrame(); }
    }

    private async Task<ImmutableArray<byte>> DecodeVideoAsync(string path, EvaluatedVideoLayer layer, EvaluatedFrame frame,
        int width, int height, CancellationToken cancellationToken)
    {
        try { return await decoder.VideoAsync(path, layer.SourceTicks, width, height, cancellationToken); }
        catch (MediaEndOfStreamException original)
        {
            long frameTicks = Math.Max(1, TimelineTime.FrameToTicks(1, frame.Settings.FrameRate));
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                long fallbackTick = Math.Max(0, layer.SourceTicks - checked(frameTicks * attempt));
                if (fallbackTick == layer.SourceTicks) break;
                try
                {
                    var pixels = await decoder.VideoAsync(path, fallbackTick, width, height, cancellationToken);
                    logger?.Log(LogLevel.Warn, "preview.decoder", "Held the last decodable video frame across a short media tail",
                        new Dictionary<string, object?>
                        {
                            ["sequenceId"] = frame.SequenceId, ["timelineTick"] = frame.Tick, ["clipId"] = layer.ClipId,
                            ["mediaAssetId"] = layer.MediaAssetId, ["requestedSourceTick"] = layer.SourceTicks,
                            ["decodedSourceTick"] = fallbackTick,
                        }, original);
                    return pixels;
                }
                catch (MediaEndOfStreamException) when (fallbackTick > 0) { }
            }
            throw;
        }
    }

    public static void Composite(byte[] output, ImmutableArray<byte> source, int width, int height, ClipAppearance appearance, CancellationToken token = default)
    {
        var t = appearance.Transform;
        Kachinco.Native.NativeComposition.Composite(output, source.AsSpan(), width, height,
            new(t.X, t.Y, t.ScaleX, t.ScaleY, t.RotationDegrees, appearance.Opacity, (int)appearance.Blend), token);
    }
}

public sealed class SharedAudioRenderer(IMediaDecoder decoder, string? projectPath = null) : IAudioRenderer
{
    public async ValueTask<Result<RenderedAudioBlock>> RenderAsync(TimelineEvaluator evaluator, long firstSample, int sampleCount, int sampleRate, int channels, CancellationToken cancellationToken)
    {
        if (firstSample < 0 || sampleCount <= 0 || sampleCount > 48000 || sampleRate != 48000 || channels != 2)
            return Result<RenderedAudioBlock>.Fail(Diagnostic.Error("INVALID_AUDIO_BLOCK", "Expected bounded 48 kHz stereo output."));
        try
        {
            long total = TimelineTime.SampleCount(evaluator.Sequence.DurationTicks, sampleRate);
            if (firstSample >= total || sampleCount > total - firstSample)
                return Result<RenderedAudioBlock>.Fail(Diagnostic.Error("INVALID_AUDIO_BLOCK", "Audio block is outside the sequence."));
            long start = TimelineTime.SampleToTicks(firstSample, sampleRate);
            long end = Math.Min(evaluator.Sequence.DurationTicks, TimelineTime.SampleToTicks(firstSample + sampleCount, sampleRate));
            var plan = evaluator.EvaluateAudioRange(start, end - start);
            if (!plan.Success) return new(null, plan.Diagnostics);
            var mix = new double[sampleCount * channels];
            var paths = MediaReferenceResolver.Inspect(evaluator.Project, projectPath).ToDictionary(x => x.MediaAssetId);
            foreach (var layer in plan.Value)
            {
                // First output sample inside each half-open contribution, never round backward.
                long from = FirstSampleAtOrAfter(layer.TimelineStartTicks, sampleRate);
                long until = FirstSampleAtOrAfter(layer.TimelineStartTicks + layer.DurationTicks, sampleRate);
                from = Math.Max(firstSample, from); until = Math.Min(firstSample + sampleCount, until);
                if (until <= from) continue;
                long sourceTicks = layer.SourceStartTicks + TimelineTime.SampleToTicks(from, sampleRate) - layer.TimelineStartTicks;
                var path = paths[layer.MediaAssetId];
                if (!path.IsAvailable || path.ResolvedPath is null) return Result<RenderedAudioBlock>.Fail(Diagnostic.Error("MEDIA_MISSING", "Audio source is missing.", layer.MediaAssetId));
                var samples = await decoder.AudioAsync(path.ResolvedPath, sourceTicks, (int)(until - from), sampleRate, channels, cancellationToken);
                int offset = checked((int)(from - firstSample) * channels);
                if (samples.Length != checked((int)(until - from) * channels)) throw new InvalidDataException("Decoder returned an incomplete PCM block.");
                evaluator.MixAudio(layer.ClipId, mix, samples.AsSpan(), offset, from, sampleRate, channels);
            }
            return Result<RenderedAudioBlock>.Ok(new(firstSample, sampleRate, channels,
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(Kachinco.Native.NativeComposition.Finish(mix))));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Result<RenderedAudioBlock>.Fail(Diagnostic.Error("AUDIO_RENDER_FAILED", e.Message)); }
    }
    private static long FirstSampleAtOrAfter(long tick, int rate) => Kachinco.Native.NativePlayback.FirstSample(tick, rate);
}
