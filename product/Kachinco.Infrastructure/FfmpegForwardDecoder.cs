using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Codec-side forward streams only: no timeline, transforms, blending or audio mixing.
// A caller serializes video calls and audio calls independently. Each side holds <=2 sources.
public sealed class FfmpegForwardDecoder(string executable = "ffmpeg") : IMediaDecoder, IDisposable
{
    private readonly Dictionary<string, VideoStream> videos = [];
    private readonly Dictionary<string, AudioStream> audios = [];
    private long processStarts;
    public long ProcessStarts => Interlocked.Read(ref processStarts);
    private static string Key(string path) => Path.GetFullPath(path) + "|" + PreviewContext.FileStamp(path);
    public async Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token)
    {
        string key = Key(path) + $"|{width}|{height}";
        if (videos.TryGetValue(key, out var found) && !found.Accepts(sourceTicks, token)) { found.Dispose(); videos.Remove(key); found = null; }
        if (found is null) found = Open();
        try { return await found.FrameAsync(sourceTicks, token); }
        catch (EndOfStreamException)
        {
            found.Dispose(); videos.Remove(key); found = Open();
            return await found.FrameAsync(sourceTicks, token);
        }
        VideoStream Open()
        {
            if (videos.Count >= 2) { var old = videos.First(); old.Value.Dispose(); videos.Remove(old.Key); }
            Interlocked.Increment(ref processStarts);
            var stream = new VideoStream(executable, path, sourceTicks, width, height, token); videos.Add(key, stream); return stream;
        }
    }
    public async Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token)
    {
        if (rate != 48000 || channels != 2 || count is < 1 or > 48000) throw new InvalidDataException("Invalid forward PCM request.");
        string key = Key(path);
        if (audios.TryGetValue(key, out var found) && !found.Accepts(sourceTicks, count, token)) { found.Dispose(); audios.Remove(key); found = null; }
        if (found is null)
        {
            if (audios.Count >= 2) { var old = audios.First(); old.Value.Dispose(); audios.Remove(old.Key); }
            Interlocked.Increment(ref processStarts);
            found = new(executable, path, sourceTicks, token); audios.Add(key, found);
        }
        return await found.BlockAsync(count, token);
    }
    public void Dispose()
    {
        foreach (var stream in videos.Values) stream.Dispose(); videos.Clear();
        foreach (var stream in audios.Values) stream.Dispose(); audios.Clear();
    }

    private abstract class StreamProcess : IDisposable
    {
        protected readonly Process Process;
        protected readonly CancellationToken Owner;
        private readonly CancellationTokenRegistration cancellation;
        private readonly CancellationTokenSource lifetime;
        protected Task ErrorTask = Task.CompletedTask;
        protected readonly StringBuilder Error = new();
        protected bool disposed;
        protected StreamProcess(string executable, IEnumerable<string> args, CancellationToken owner)
        {
            owner.ThrowIfCancellationRequested(); Owner = owner;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
            Process = new() { StartInfo = MediaProcess.StartInfo(executable, args) };
            Process.StartInfo.RedirectStandardOutput = true;
            Process.Start(); cancellation = lifetime.Token.Register(() => MediaProcess.Kill(Process));
        }
        protected CancellationToken Lifetime => lifetime.Token;
        protected async Task<byte[]> ReadAsync(int size, CancellationToken token, bool padPcmTail = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var kill = timeout.Token.Register(() => MediaProcess.Kill(Process));
            byte[] data = new byte[size]; int offset = 0;
            while (offset < size)
            {
                int read = await Process.StandardOutput.BaseStream.ReadAsync(data.AsMemory(offset), timeout.Token);
                if (read == 0)
                {
                    await Process.WaitForExitAsync(timeout.Token); await ErrorTask;
                    if (Process.ExitCode != 0) throw new InvalidDataException("FFmpeg forward decode failed: " + Error);
                    if (padPcmTail && offset % 8 == 0) return data; // Match independent decoder: final missing PCM samples are silence.
                    if (offset == 0) throw new EndOfStreamException("Source has no frame at the requested time.");
                    throw new InvalidDataException("Incomplete decoded frame/PCM block.");
                }
                offset += read;
            }
            return data;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            lifetime.Cancel(); MediaProcess.Kill(Process); cancellation.Dispose();
            // Drainers own no unmanaged memory and finish after process pipe closure.
            _ = ErrorTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            Process.Dispose(); lifetime.Dispose();
        }
    }
    private sealed class VideoStream : StreamProcess
    {
        private readonly long start;
        private readonly int size;
        private long lastRequest = -1, lastPts = long.MinValue;
        private int frames;
        private ImmutableArray<byte> last;
        private readonly Channel<long> timestamps = Channel.CreateBounded<long>(64);
        private readonly TaskCompletionSource<(long Numerator, long Denominator)> timebase = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly Regex Config = new(@"config in time_base:\s*(\d+)/(\d+)");
        private static readonly Regex FramePts = new(@"\bn:\s*\d+\s+pts:\s*(-?\d+)");
        public VideoStream(string executable, string path, long tick, int width, int height, CancellationToken token)
            : base(executable, ["-v", "info", "-nostdin", "-threads", "1", "-filter_threads", "1", "-ss", FfmpegMediaDecoder.Seconds(tick),
                "-i", Path.GetFullPath(path), "-map", "0:v:0", "-an", "-t", "2", "-frames:v", "64",
                "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,format=rgba,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black@0,showinfo=checksum=0",
                "-fps_mode", "passthrough", "-threads", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"], token)
        { start = tick; size = checked(width * height * 4); ErrorTask = ReadMetadataAsync(); }
        public bool Accepts(long tick, CancellationToken token) => !disposed && Owner == token && !Owner.IsCancellationRequested &&
            tick >= lastRequest && tick >= start && tick - start < 2 * TimelineTime.TicksPerSecond;
        public async Task<ImmutableArray<byte>> FrameAsync(long tick, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var tb = await timebase.Task.WaitAsync(timeout.Token);
            long ToPts(long t) => TimelineTime.RoundHalfUp((System.Numerics.BigInteger)t * tb.Denominator,
                (System.Numerics.BigInteger)TimelineTime.TicksPerSecond * tb.Numerator);
            long target = ToPts(tick) - ToPts(start);
            lastRequest = tick;
            while (last.IsDefault || lastPts < target)
            {
                if (frames >= 64) throw new EndOfStreamException("Forward decode window ended.");
                var bytes = await ReadAsync(size, timeout.Token);
                long pts = await timestamps.Reader.ReadAsync(timeout.Token);
                if (pts <= lastPts) throw new InvalidDataException("Non-monotonic source video timestamps.");
                lastPts = pts; frames++; last = ImmutableCollectionsMarshal.AsImmutableArray(bytes);
            }
            return last;
        }
        private async Task ReadMetadataAsync()
        {
            try
            {
                var buffer = new char[2048]; var line = new StringBuilder(); int read;
                while ((read = await Process.StandardError.ReadAsync(buffer.AsMemory(), Lifetime)) > 0)
                    for (int i = 0; i < read; i++)
                    {
                        if (buffer[i] == '\n') { Parse(line.ToString()); line.Clear(); }
                        else if (line.Length < 4096) line.Append(buffer[i]);
                    }
                if (line.Length > 0) Parse(line.ToString());
                timebase.TrySetException(new InvalidDataException("FFmpeg did not report video timebase: " + Error));
                timestamps.Writer.TryComplete();
            }
            catch (Exception e) { timebase.TrySetException(e); timestamps.Writer.TryComplete(e); throw; }
        }
        private void Parse(string line)
        {
            var config = Config.Match(line);
            if (config.Success)
            {
                long n = long.Parse(config.Groups[1].Value, CultureInfo.InvariantCulture), d = long.Parse(config.Groups[2].Value, CultureInfo.InvariantCulture);
                if (n <= 0 || d <= 0) throw new InvalidDataException("Invalid source timebase.");
                timebase.TrySetResult((n, d));
            }
            var frame = FramePts.Match(line);
            if (frame.Success && !timestamps.Writer.TryWrite(long.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Source timestamp queue exceeds 64-frame window.");
            if (!line.Contains("showinfo", StringComparison.Ordinal) && Error.Length < 4096)
                Error.Append(line.AsSpan(0, Math.Min(line.Length, 4096 - Error.Length)));
        }
    }
    private sealed class AudioStream : StreamProcess
    {
        private readonly long start;
        private int consumed;
        public AudioStream(string executable, string path, long tick, CancellationToken token)
            : base(executable, ["-v", "error", "-nostdin", "-threads", "1", "-ss", FfmpegMediaDecoder.Seconds(tick), "-i", Path.GetFullPath(path),
                "-map", "0:a:0", "-vn", "-t", "2", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1"], token)
        { start = tick; ErrorTask = Drain(); }
        private async Task Drain() => Error.Append(await MediaProcess.DrainErrorAsync(Process.StandardError, Lifetime));
        public bool Accepts(long tick, int count, CancellationToken token) => !disposed && Owner == token && !Owner.IsCancellationRequested &&
            tick == start + TimelineTime.SampleToTicks(consumed, 48000) && consumed + count <= 96000;
        public async Task<ImmutableArray<float>> BlockAsync(int count, CancellationToken token)
        {
            var bytes = await ReadAsync(count * 8, token, padPcmTail: true); var samples = new float[count * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
                if (!float.IsFinite(samples[i])) throw new InvalidDataException("Non-finite source PCM.");
            }
            consumed += count; return ImmutableCollectionsMarshal.AsImmutableArray(samples);
        }
    }
}
