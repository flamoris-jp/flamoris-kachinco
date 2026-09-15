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
// A caller serializes video calls and audio calls independently. Each side owns a bounded LRU
// pool large enough for the normal multi-track contributor set without per-frame process churn.
public sealed class FfmpegForwardDecoder(string executable = "ffmpeg") : IMediaDecoder, IDisposable
{
    public const int MaximumVideoStreams = 8;
    public const int MaximumAudioStreams = 8;
    private readonly Dictionary<long, PoolEntry<VideoStream>> videos = [];
    private readonly Dictionary<long, PoolEntry<AudioStream>> audios = [];
    private long accessSequence, streamSequence;
    private long processStarts;
    private long startElapsed, stopElapsed;
    public double ProcessStartMilliseconds => startElapsed * 1000d / Stopwatch.Frequency;
    public double ProcessStopMilliseconds => stopElapsed * 1000d / Stopwatch.Frequency;
    public int ActiveVideoStreams => videos.Count;
    public int ActiveAudioStreams => audios.Count;
    private void Close(StreamProcess stream) { long at = Stopwatch.GetTimestamp(); try { stream.Dispose(); } finally { Interlocked.Add(ref stopElapsed, Stopwatch.GetTimestamp() - at); } }
    public long ProcessStarts => Interlocked.Read(ref processStarts);
    private static string Key(string path) => Path.GetFullPath(path) + "|" + PreviewContext.FileStamp(path);
    public async Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token)
    {
        string baseKey = Key(path) + $"|{width}|{height}";
        RemoveExpired(videos, baseKey, stream => stream.IsOwnedBy(token));
        var entry = videos.Values.Where(candidate => candidate.BaseKey == baseKey && candidate.Stream.Accepts(sourceTicks, token))
            .OrderBy(candidate => candidate.Stream.ForwardDistance(sourceTicks)).ThenByDescending(candidate => candidate.LastUsed).FirstOrDefault();
        if (entry is null) entry = Open();
        entry.LastUsed = NextAccess();
        try { return await entry.Stream.FrameAsync(sourceTicks, token); }
        catch (EndOfStreamException)
        {
            Close(entry.Stream); videos.Remove(entry.Id); entry = Open();
            return await entry.Stream.FrameAsync(sourceTicks, token);
        }
        PoolEntry<VideoStream> Open()
        {
            MakeRoom(videos, MaximumVideoStreams);
            Interlocked.Increment(ref processStarts);
            long at = Stopwatch.GetTimestamp();
            var stream = new VideoStream(executable, path, sourceTicks, width, height, token);
            Interlocked.Add(ref startElapsed, Stopwatch.GetTimestamp() - at);
            var opened = new PoolEntry<VideoStream>(NextStream(), baseKey, stream, NextAccess()); videos.Add(opened.Id, opened); return opened;
        }
    }
    public async Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token)
    {
        if (rate != 48000 || channels != 2 || count is < 1 or > 48000) throw new InvalidDataException("Invalid forward PCM request.");
        string baseKey = Key(path);
        RemoveExpired(audios, baseKey, stream => stream.IsOwnedBy(token));
        var entry = audios.Values.Where(candidate => candidate.BaseKey == baseKey && candidate.Stream.Accepts(sourceTicks, count, token))
            .MaxBy(candidate => candidate.LastUsed);
        if (entry is null)
        {
            MakeRoom(audios, MaximumAudioStreams);
            Interlocked.Increment(ref processStarts);
            long at = Stopwatch.GetTimestamp();
            var stream = new AudioStream(executable, path, sourceTicks, token);
            Interlocked.Add(ref startElapsed, Stopwatch.GetTimestamp() - at);
            entry = new(NextStream(), baseKey, stream, NextAccess()); audios.Add(entry.Id, entry);
        }
        entry.LastUsed = NextAccess();
        return await entry.Stream.BlockAsync(count, token);
    }
    public void Dispose()
    {
        foreach (var entry in videos.Values) Close(entry.Stream); videos.Clear();
        foreach (var entry in audios.Values) Close(entry.Stream); audios.Clear();
    }
    private long NextAccess() => Interlocked.Increment(ref accessSequence);
    private long NextStream() => Interlocked.Increment(ref streamSequence);
    private void RemoveExpired<T>(Dictionary<long, PoolEntry<T>> pool, string baseKey, Func<T, bool> usable) where T : StreamProcess
    {
        foreach (var expired in pool.Values.Where(entry => entry.BaseKey == baseKey && !usable(entry.Stream)).ToArray())
        { Close(expired.Stream); pool.Remove(expired.Id); }
    }
    private void MakeRoom<T>(Dictionary<long, PoolEntry<T>> pool, int maximum) where T : StreamProcess
    {
        if (pool.Count < maximum) return;
        var oldest = pool.MinBy(pair => pair.Value.LastUsed);
        Close(oldest.Value.Stream); pool.Remove(oldest.Key);
    }
    private sealed class PoolEntry<T>(long id, string baseKey, T stream, long lastUsed) where T : StreamProcess
    {
        public long Id { get; } = id;
        public string BaseKey { get; } = baseKey;
        public T Stream { get; } = stream;
        public long LastUsed { get; set; } = lastUsed;
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
        public bool IsOwnedBy(CancellationToken token) => !disposed && Owner == token && !Owner.IsCancellationRequested;
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
        { start = tick; size = checked(width * height * 4); ErrorTask = Task.Factory.StartNew(ReadMetadata, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        public bool Accepts(long tick, CancellationToken token) => IsOwnedBy(token) &&
            tick >= lastRequest && tick >= start && tick - start < 2 * TimelineTime.TicksPerSecond;
        public long ForwardDistance(long tick) => tick - lastRequest;
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
        private void ReadMetadata()
        {
            try
            {
                var buffer = new char[2048]; var line = new StringBuilder(); int read;
                while ((read = Process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
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

        public bool Accepts(long tick, int count, CancellationToken token) => IsOwnedBy(token) &&
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
