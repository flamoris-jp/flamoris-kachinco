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
        return RenderAsync(project, scaled, TimelineTime.RoundHalfUp((System.Numerics.BigInteger)frame.Tick * frame.Settings.FrameRate.Numerator,
            (System.Numerics.BigInteger)TimelineTime.TicksPerSecond * frame.Settings.FrameRate.Denominator), token);
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
        if (output.Length != checked(width * height * 4) || source.Length != output.Length) throw new InvalidDataException("Invalid RGBA buffer dimensions.");
        var t = appearance.Transform;
        if (t == Transform2D.Identity && appearance.Opacity == 1 && appearance.Blend == BlendMode.Normal)
        {
            bool opaque = true;
            for (int i = 3; i < source.Length; i += 4)
            {
                if ((i & 65535) == 3) token.ThrowIfCancellationRequested();
                if (source[i] != 255) { opaque = false; break; }
            }
            if (opaque) { source.AsSpan().CopyTo(output); return; }
            for (int i = 0; i < source.Length; i += 4)
            {
                if (i % (width * 4) == 0) token.ThrowIfCancellationRequested();
                if (source[i + 3] == 0) continue;
                if (source[i + 3] == 255) { source.AsSpan(i, 4).CopyTo(output.AsSpan(i, 4)); continue; }
                var mixed = BlendReference.Composite(new(output[i]/255d,output[i+1]/255d,output[i+2]/255d,output[i+3]/255d),
                    new(source[i]/255d,source[i+1]/255d,source[i+2]/255d,source[i+3]/255d), appearance.Blend, 1);
                output[i]=Byte(mixed.R); output[i+1]=Byte(mixed.G); output[i+2]=Byte(mixed.B); output[i+3]=Byte(mixed.A);
            }
            return;
        }
        double radians = t.RotationDegrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 - t.X, py = y + 0.5 - t.Y;
                double sx = (cos * px + sin * py) / t.ScaleX, sy = (-sin * px + cos * py) / t.ScaleY;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                int src = ((int)sy * width + (int)sx) * 4, dst = (y * width + x) * 4;
                if (source[src + 3] == 0) continue;
                var mixed = BlendReference.Composite(new(output[dst] / 255d, output[dst + 1] / 255d, output[dst + 2] / 255d, output[dst + 3] / 255d),
                    new(source[src] / 255d, source[src + 1] / 255d, source[src + 2] / 255d, source[src + 3] / 255d), appearance.Blend, appearance.Opacity);
                output[dst] = Byte(mixed.R); output[dst + 1] = Byte(mixed.G); output[dst + 2] = Byte(mixed.B); output[dst + 3] = Byte(mixed.A);
            }
        }
    }
    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
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
                for (int i = 0; i < samples.Length; i++) mix[offset + i] += samples[i] * layer.Gain;
            }
            return Result<RenderedAudioBlock>.Ok(new(firstSample, sampleRate, channels,
                ImmutableArray.CreateRange(mix.Select(x => (float)Math.Clamp(x, -1d, 1d)))));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Result<RenderedAudioBlock>.Fail(Diagnostic.Error("AUDIO_RENDER_FAILED", e.Message)); }
    }
    private static long FirstSampleAtOrAfter(long tick, int rate) => checked((long)(((System.Numerics.BigInteger)tick * rate + TimelineTime.TicksPerSecond - 1) / TimelineTime.TicksPerSecond));
}
