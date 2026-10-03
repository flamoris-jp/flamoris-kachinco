using System.Collections.Immutable;
using Flamoris.Logging;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public interface ICaptionRasterizer
{
    ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token);
}

public sealed class SharedFrameRenderer(IMediaDecoder decoder, string? projectPath = null, ICaptionRasterizer? captions = null,
    FlamorisLogger? logger = null) : IFrameRenderer
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
        try
        {
            int width = frame.Settings.Width, height = frame.Settings.Height;
            var output = new byte[checked(width * height * 4)];
            for (int i = 3; i < output.Length; i += 4) output[i] = 255;
            var paths = MediaReferenceResolver.Inspect(project, projectPath).ToDictionary(x => x.MediaAssetId);
            foreach (var layer in frame.VideoLayers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[layer.MediaAssetId];
                if (!path.IsAvailable || path.ResolvedPath is null) return Result<RenderedVideoFrame>.Fail(Diagnostic.Error("MEDIA_MISSING", "Video source is missing.", layer.MediaAssetId));
                var pixels = await DecodeVideoAsync(path.ResolvedPath, layer, frame, width, height, cancellationToken);
                Composite(output, pixels, width, height, layer.Appearance, cancellationToken);
            }
            if (!frame.Captions.IsEmpty)
            {
                if (captions is null) return Result<RenderedVideoFrame>.Fail(Diagnostic.Error("CAPTION_RENDERER_REQUIRED", "A caption rasterizer is required."));
                var pixels = await captions.RasterizeAsync(frame.Captions, width, height, cancellationToken);
                Composite(output, pixels, width, height, ClipAppearance.Default, cancellationToken);
            }
            return Result<RenderedVideoFrame>.Ok(new(frameIndex, frame.Tick, width, height, System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(output)));
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
