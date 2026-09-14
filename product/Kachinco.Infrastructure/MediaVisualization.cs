using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed record MediaVisualization(ImmutableArray<byte> Rgba, int Width, int Height,
    ImmutableArray<float> Peaks, long DurationTicks);

// Fixed-size source overview. Display cropping uses SourceInTicks, never clip start.
public static class WaveformProjection
{
    public static ImmutableArray<float> Crop(ImmutableArray<float> peaks, long assetDuration, long sourceIn, long duration, int columns)
    {
        if (peaks.IsDefaultOrEmpty || assetDuration <= 0 || sourceIn < 0 || duration <= 0 ||
            sourceIn > assetDuration || duration > assetDuration - sourceIn || columns is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(duration));
        var output = ImmutableArray.CreateBuilder<float>(columns);
        for (int x = 0; x < columns; x++)
        {
            decimal start = sourceIn + (decimal)duration * x / columns;
            decimal end = sourceIn + (decimal)duration * (x + 1) / columns;
            int a = Math.Clamp((int)decimal.Floor(start * peaks.Length / assetDuration), 0, peaks.Length - 1);
            int b = Math.Clamp((int)decimal.Ceiling(end * peaks.Length / assetDuration), a + 1, peaks.Length);
            float peak = 0;
            for (int i = a; i < b; i++) peak = Math.Max(peak, peaks[i]);
            output.Add(peak);
        }
        return output.MoveToImmutable();
    }
}

public sealed class WaveformAccumulator
{
    private readonly float[] peaks;
    private readonly long expectedSamples;
    public long SamplesRead { get; private set; }
    public WaveformAccumulator(long samples, int bins)
    {
        if (samples <= 0 || bins is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(samples));
        expectedSamples = samples; peaks = new float[bins];
    }
    public void Add(float sample)
    {
        if (!float.IsFinite(sample)) throw new InvalidDataException("Non-finite waveform sample.");
        if (SamplesRead >= expectedSamples) throw new InvalidDataException("Waveform exceeds source duration budget.");
        int bin = (int)((decimal)SamplesRead * peaks.Length / expectedSamples);
        peaks[bin] = Math.Max(peaks[bin], Math.Min(1, Math.Abs(sample))); SamplesRead++;
    }
    public ImmutableArray<float> Complete()
    {
        if (SamplesRead == 0) throw new InvalidDataException("No audio samples decoded.");
        return ImmutableArray.CreateRange(peaks);
    }
}

public sealed class MediaVisualizationService(string executable = "ffmpeg")
{
    public const int ThumbnailWidth = 160;
    public const int ThumbnailHeight = 90;
    public async Task<MediaVisualization> GenerateAsync(MediaAsset asset, string path, CancellationToken token)
    {
        if (asset.Kind == MediaKind.Mov)
        {
            var rgba = await new FfmpegMediaDecoder(executable).VideoAsync(path, 0, ThumbnailWidth, ThumbnailHeight, token);
            return new(rgba, ThumbnailWidth, ThumbnailHeight, [], asset.DurationTicks);
        }
        const int rate = 8000;
        long samples = checked((long)decimal.Ceiling((decimal)asset.DurationTicks * rate / TimelineTime.TicksPerSecond));
        var accumulator = new WaveformAccumulator(samples, 2048);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = new Process { StartInfo = MediaProcess.StartInfo(executable,
            ["-v", "error", "-nostdin", "-i", Path.GetFullPath(path), "-map", "0:a:0", "-vn", "-t", FfmpegMediaDecoder.Seconds(asset.DurationTicks),
             "-ac", "1", "-ar", "8000", "-f", "f32le", "pipe:1"]) };
        process.StartInfo.RedirectStandardOutput = true;
        token.ThrowIfCancellationRequested(); process.Start();
        using var cancellation = timeout.Token.Register(() => MediaProcess.Kill(process));
        var error = MediaProcess.DrainErrorAsync(process.StandardError, timeout.Token);
        try
        {
            byte[] buffer = new byte[32768]; int carry = 0;
            while (true)
            {
                int read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), timeout.Token);
                if (read == 0) break;
                int count = read + carry; int full = count - count % 4;
                for (int i = 0; i < full; i += 4)
                    accumulator.Add(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i, 4))));
                carry = count - full;
                buffer.AsSpan(full, carry).CopyTo(buffer);
            }
            await process.WaitForExitAsync(timeout.Token);
            if (carry != 0 || process.ExitCode != 0) throw new InvalidDataException("Waveform decode failed: " + await error);
            // A materially truncated source must not masquerade as silence.
            if (accumulator.SamplesRead < samples - rate / 10) throw new InvalidDataException("Waveform source is shorter than its registered duration.");
            return new([], 0, 0, accumulator.Complete(), asset.DurationTicks);
        }
        finally
        {
            MediaProcess.Kill(process);
            try { await error; } catch (OperationCanceledException) { }
        }
    }
}
