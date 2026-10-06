using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Flamoris.Logging;
using Kachinco.Core;
using Kachinco.Native;

namespace Kachinco.Infrastructure;

// Codec-side forward streams only: no timeline, transforms, blending or audio mixing.
// A caller serializes video calls and audio calls independently. Each side owns a bounded LRU
// pool large enough for the normal multi-track contributor set without per-frame process churn.
public sealed class FfmpegForwardDecoder : IMediaDecoder, IDisposable
{
    public const int MaximumVideoStreams = 8;
    // Each FFmpeg D3D11VA process owns its codec surface pool. Bound simultaneous
    // hardware processes separately; other contributors retain software forward streams.
    public const int MaximumHardwareVideoStreams = 2;
    public const int MaximumAudioStreams = 8;
    private readonly Dictionary<long, PoolEntry<VideoStream>> videos = [];
    private readonly Dictionary<long, PoolEntry<AudioStream>> audios = [];
    private readonly NativeByteCache randomVideoFallbacks = new(0, 256);
    private readonly string executable;
    private readonly FlamorisLogger? logger;
    private readonly string role;
    private readonly IMediaDecoder randomVideoFallback;
    private readonly bool ownsRandomVideoFallback;
    private readonly PreviewDecodePreference decodePreference;
    private readonly Func<IEnumerable<string>, NativeMediaProcess>? startVideoProcess;
    private PreviewDecodeDiagnostics decodeDiagnostics;
    private string? hardwareFailure;
    private long accessSequence, streamSequence;
    private long processStarts;
    private long startElapsed, stopElapsed;
    public double ProcessStartMilliseconds => startElapsed * 1000d / Stopwatch.Frequency;
    public double ProcessStopMilliseconds => stopElapsed * 1000d / Stopwatch.Frequency;
    public int ActiveVideoStreams => videos.Count;
    public int ActiveHardwareVideoStreams => videos.Values.Count(entry => entry.Stream.Hardware && entry.Stream.IsOpen);
    public int ActiveAudioStreams => audios.Count;
    public PreviewDecodeDiagnostics DecodeDiagnostics => Volatile.Read(ref decodeDiagnostics);
    public FfmpegForwardDecoder(string executable = "ffmpeg", FlamorisLogger? logger = null,
        string role = "shared", IMediaDecoder? randomVideoFallback = null,
        PreviewDecodePreference decodePreference = PreviewDecodePreference.Software)
        : this(executable, logger, role, randomVideoFallback, decodePreference, null) { }
    internal FfmpegForwardDecoder(string executable, FlamorisLogger? logger, string role,
        IMediaDecoder? randomVideoFallback, PreviewDecodePreference decodePreference,
        Func<IEnumerable<string>, NativeMediaProcess>? startVideoProcess)
    {
        if (!Enum.IsDefined(decodePreference)) throw new ArgumentOutOfRangeException(nameof(decodePreference));
        this.executable = executable;
        this.logger = logger;
        this.role = role;
        this.randomVideoFallback = randomVideoFallback ?? new FfmpegMediaDecoder(executable);
        ownsRandomVideoFallback = randomVideoFallback is null;
        this.decodePreference = decodePreference;
        this.startVideoProcess = startVideoProcess;
        decodeDiagnostics = new(decodePreference, PreviewDecodePreference.Software,
            decodePreference == PreviewDecodePreference.D3D11, false, false, null);
    }
    private void Close(StreamProcess stream, string kind = "stream", long? streamId = null, string reason = "release")
    {
        long at = Stopwatch.GetTimestamp();
        try { stream.Dispose(); }
        finally
        {
            Interlocked.Add(ref stopElapsed, Stopwatch.GetTimestamp() - at);
            logger?.Debug("preview.decoder", "Closed forward decoder", new Dictionary<string, object?>
            { ["role"] = role, ["kind"] = kind, ["streamId"] = streamId, ["reason"] = reason });
        }
    }
    public long ProcessStarts => Interlocked.Read(ref processStarts);
    private static string Key(string path) => Path.GetFullPath(path) + "|" + PreviewContext.FileStamp(path);
    // Called under the preview video gate with the complete evaluated contributor
    // set. Per-call eviction would break layered clips using one source at offsets.
    internal void RetainVideoStreams(IEnumerable<(string Path, long Tick)> requests, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var needed = requests.Select(request => (Key: Key(request.Path) + $"|{width}|{height}", request.Tick)).ToArray();
        foreach (var entry in videos.Values.ToArray())
        {
            bool retain = entry.Stream.IsOwnedBy(token) && needed.Any(request => entry.BaseKey == request.Key &&
                NativePlayback.SelectDecoder([entry.Stream.Candidate(entry.Id, entry.LastUsed, true)], true, request.Tick).Selected == entry.Id);
            if (retain) continue;
            Close(entry.Stream, "video", entry.Id, "inactive-contributor"); videos.Remove(entry.Id);
        }
    }
    public async Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (MediaSourceFormats.TryGetKind(path, out var kind) && kind == MediaKind.Image)
        {
            PublishDecode(false, false, "Still images use the software codec adapter.");
            return await randomVideoFallback.VideoAsync(path, 0, width, height, token);
        }
        string baseKey = Key(path) + $"|{width}|{height}";
        string fallbackKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(baseKey)));
        if (randomVideoFallbacks.TryGet(fallbackKey, out _))
        {
            PublishDecode(false, false, hardwareFailure ?? "Forward stream uses accurate software random access.");
            return await randomVideoFallback.VideoAsync(path, sourceTicks, width, height, token);
        }
        RemoveExpired(videos, baseKey, stream => stream.IsOwnedBy(token));
        var selection = NativePlayback.SelectDecoder(videos.Values.Select(candidate => candidate.Stream.Candidate(candidate.Id,
            candidate.LastUsed, candidate.BaseKey == baseKey && candidate.Stream.IsOwnedBy(token))).ToArray(), true, sourceTicks);
        videos.TryGetValue(selection.Selected, out var entry);
        bool hardwareAttempt = entry?.Stream.Hardware ?? false;
        try
        {
            return await ReadForward();
        }
        catch (OperationCanceledException) { throw; }
        catch (MediaEndOfStreamException)
        {
            token.ThrowIfCancellationRequested();
            // Natural EOF is not a device failure. Keep only the stream's bounded
            // last-frame/EOF metadata for exact tail recovery; release GPU surfaces.
            if (entry is not null) Close(entry.Stream, "video", entry.Id, "media-ended");
            PublishDecode(false, false, "Source video ended; preserving accurate tail recovery.");
            throw;
        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {
            // Native pipe closure can race owner cancellation. Cancellation never launches
            // a replacement decoder or marks hardware permanently unavailable.
            token.ThrowIfCancellationRequested();
            if (hardwareAttempt)
            {
                hardwareFailure = CompactReason(exception);
                foreach (var hardwareEntry in videos.Values.Where(value => value.Stream.Hardware).ToArray())
                { Close(hardwareEntry.Stream, "video", hardwareEntry.Id, "hardware-fallback"); videos.Remove(hardwareEntry.Id); }
                entry = null;
                PublishDecode(false, false, hardwareFailure);
                logger?.Log(LogLevel.Warn, "preview.decoder", "D3D11VA decode failed; restarting software forward decode",
                    DecoderProperties(sourceTicks, width, height), exception);
                try { return await ReadForward(); }
                catch (OperationCanceledException) { throw; }
                catch (MediaEndOfStreamException)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry is not null) Close(entry.Stream, "video", entry.Id, "media-ended");
                    throw;
                }
                catch (Exception softwareException) when (IsDecodeFailure(softwareException))
                { token.ThrowIfCancellationRequested(); return await RandomFallback(softwareException); }
            }
            return await RandomFallback(exception);
        }
        async Task<ImmutableArray<byte>> RandomFallback(Exception exception)
        {
            if (entry is not null && videos.Remove(entry.Id)) Close(entry.Stream, "video", entry.Id, "degraded");
            randomVideoFallbacks.Put(fallbackKey, [], 0);
            PublishDecode(false, false, hardwareFailure ?? CompactReason(exception));
            logger?.Log(LogLevel.Warn, "preview.decoder", "Forward video decoder degraded to accurate random access",
                DecoderProperties(sourceTicks, width, height, entry?.Id), exception);
            return await randomVideoFallback.VideoAsync(path, sourceTicks, width, height, token);
        }
        async Task<ImmutableArray<byte>> ReadForward()
        {
            if (entry is null) entry = Open();
            entry.LastUsed = NextAccess();
            var frame = await entry.Stream.FrameAsync(sourceTicks, token);
            token.ThrowIfCancellationRequested();
            PublishDecode(entry.Stream.Hardware, entry.Stream.Hardware, SoftwareReason(entry.Stream.Hardware));
            return frame;
        }
        PoolEntry<VideoStream> Open()
        {
            MakeRoom(videos, MaximumVideoStreams);
            hardwareAttempt = decodePreference == PreviewDecodePreference.D3D11 && hardwareFailure is null &&
                ActiveHardwareVideoStreams < MaximumHardwareVideoStreams;
            PublishDecode(hardwareAttempt, false, SoftwareReason(hardwareAttempt));
            Interlocked.Increment(ref processStarts);
            long at = Stopwatch.GetTimestamp();
            var stream = new VideoStream(executable, path, sourceTicks, width, height, token, hardwareAttempt, startVideoProcess);
            Interlocked.Add(ref startElapsed, Stopwatch.GetTimestamp() - at);
            var opened = new PoolEntry<VideoStream>(NextStream(), baseKey, stream, NextAccess()); videos.Add(opened.Id, opened);
            logger?.Debug("preview.decoder", "Opened forward video decoder", DecoderProperties(sourceTicks, width, height, opened.Id));
            return opened;
        }
    }
    private string? SoftwareReason(bool hardware) => hardware ? null : hardwareFailure ??
        (decodePreference == PreviewDecodePreference.D3D11 ? "Hardware decoder stream limit reached; using software forward decode." : null);
    private void PublishDecode(bool hardware, bool confirmed, string? reason) => Volatile.Write(ref decodeDiagnostics,
        new PreviewDecodeDiagnostics(decodePreference, hardware ? PreviewDecodePreference.D3D11 : PreviewDecodePreference.Software,
            decodePreference == PreviewDecodePreference.D3D11, confirmed, hardware, reason));
    private static bool IsDecodeFailure(Exception exception) =>
        exception is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception;
    private static string CompactReason(Exception exception)
    {
        string prefix = exception.GetType().Name + ": ";
        string detail = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        int maximum = 512 - prefix.Length;
        // FFmpeg puts device/codec failure details after its version/header text.
        return prefix + (detail.Length <= maximum ? detail : "..." + detail[^(maximum - 3)..]);
    }
    public async Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token)
    {
        if (rate != 48000 || channels != 2 || count is < 1 or > 48000) throw new InvalidDataException("Invalid forward PCM request.");
        string baseKey = Key(path);
        RemoveExpired(audios, baseKey, stream => stream.IsOwnedBy(token));
        var selection = NativePlayback.SelectDecoder(audios.Values.Select(candidate => candidate.Stream.Candidate(candidate.Id,
            candidate.LastUsed, candidate.BaseKey == baseKey && candidate.Stream.IsOwnedBy(token))).ToArray(), false, sourceTicks, count);
        audios.TryGetValue(selection.Selected, out var entry);
        if (entry is null)
        {
            MakeRoom(audios, MaximumAudioStreams);
            Interlocked.Increment(ref processStarts);
            long at = Stopwatch.GetTimestamp();
            var stream = new AudioStream(executable, path, sourceTicks, token);
            Interlocked.Add(ref startElapsed, Stopwatch.GetTimestamp() - at);
            entry = new(NextStream(), baseKey, stream, NextAccess()); audios.Add(entry.Id, entry);
            logger?.Debug("preview.decoder", "Opened forward audio decoder", new Dictionary<string, object?>
            { ["role"] = role, ["sourceTicks"] = sourceTicks, ["streamId"] = entry.Id, ["sampleCount"] = count });
        }
        entry.LastUsed = NextAccess();
        return await entry.Stream.BlockAsync(count, token);
    }
    // The caller joins outstanding work first. Ordinary seek/pause/play resets
    // release codec surfaces without repeatedly probing a known broken GPU path.
    public void ResetStreams()
    {
        foreach (var entry in videos.Values) Close(entry.Stream, "video", entry.Id, "dispose"); videos.Clear();
        foreach (var entry in audios.Values) Close(entry.Stream, "audio", entry.Id, "dispose"); audios.Clear();
    }
    public void Dispose()
    {
        ResetStreams();
        randomVideoFallbacks.Dispose();
        if (ownsRandomVideoFallback && randomVideoFallback is IDisposable disposable) disposable.Dispose();
    }
    private Dictionary<string, object?> DecoderProperties(long sourceTicks, int width, int height, long? streamId = null) => new()
    {
        ["role"] = role, ["sourceTicks"] = sourceTicks, ["width"] = width, ["height"] = height, ["streamId"] = streamId,
        ["decodeRequested"] = decodePreference.ToString(), ["decodeActive"] = DecodeDiagnostics.ActiveBackend.ToString(),
        ["hardwareConfirmed"] = DecodeDiagnostics.HardwareConfirmed, ["cpuTransferRequired"] = DecodeDiagnostics.RequiresCpuTransfer,
        ["decodeFallbackReason"] = DecodeDiagnostics.FallbackReason,
    };
    private long NextAccess() => Interlocked.Increment(ref accessSequence);
    private long NextStream() => Interlocked.Increment(ref streamSequence);
    private void RemoveExpired<T>(Dictionary<long, PoolEntry<T>> pool, string baseKey, Func<T, bool> usable) where T : StreamProcess
    {
        foreach (var expired in pool.Values.Where(entry => entry.BaseKey == baseKey && !usable(entry.Stream)).ToArray())
        { Close(expired.Stream, typeof(T) == typeof(VideoStream) ? "video" : "audio", expired.Id, "owner-expired"); pool.Remove(expired.Id); }
    }
    private void MakeRoom<T>(Dictionary<long, PoolEntry<T>> pool, int maximum) where T : StreamProcess
    {
        if (pool.Count < maximum) return;
        long id = NativePlayback.SelectDecoder(pool.Values.Select(entry =>
            new NativeDecoderCandidate(entry.Id, entry.LastUsed, 0, 0, 0, 0)).ToArray(), true, 0).Oldest;
        var oldest = pool[id];
        Close(oldest.Stream, typeof(T) == typeof(VideoStream) ? "video" : "audio", id, "lru-eviction"); pool.Remove(id);
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
        protected readonly NativeMediaProcess Process;
        protected readonly CancellationToken Owner;
        private readonly CancellationTokenRegistration cancellation;
        private readonly CancellationTokenSource lifetime;
        protected Task ErrorTask = Task.CompletedTask;
        protected readonly StringBuilder Error = new();
        protected bool disposed;
        public bool IsOpen => !disposed;
        protected virtual bool RetainsEndFrame => false;
        protected StreamProcess(string executable, IEnumerable<string> args, CancellationToken owner,
            Func<IEnumerable<string>, NativeMediaProcess>? startProcess = null)
        {
            owner.ThrowIfCancellationRequested(); Owner = owner;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
            Process = startProcess is null ? NativeMediaProcess.Start(executable, args) : startProcess(args);
            cancellation = lifetime.Token.Register(() => MediaProcess.Kill(Process));
        }
        protected CancellationToken Lifetime => lifetime.Token;
        public bool IsOwnedBy(CancellationToken token) => (!disposed || RetainsEndFrame) && Owner == token && !Owner.IsCancellationRequested;
        protected async Task<byte[]> ReadAsync(int size, CancellationToken token, bool padPcmTail = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var kill = timeout.Token.Register(() => MediaProcess.Kill(Process));
            byte[] data = new byte[size];
            int offset = await Process.ReadOutputBlockAsync(data, timeout.Token);
            if (offset < size)
            {
                await Process.WaitForExitAsync(timeout.Token); await ErrorTask;
                if (Process.ExitCode != 0) throw new InvalidDataException("FFmpeg forward decode failed: " + Error);
                if (padPcmTail && offset % 8 == 0) return data; // Match independent decoder: final missing PCM samples are silence.
                if (offset == 0) throw new MediaEndOfStreamException("Source has no frame at the requested time.");
                throw new InvalidDataException("Incomplete decoded frame/PCM block.");
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
        private readonly int size, width, height;
        private long lastRequest = -1, lastPts = long.MinValue;
        private long? lastSuccessfulRequest;
        private long lastSuccessfulPts = long.MinValue;
        private MediaEndOfStreamException? ended;
        protected override bool RetainsEndFrame => ended is not null;
        private ImmutableArray<byte> last;
        private readonly Channel<long> timestamps = Channel.CreateBounded<long>(64);
        private readonly TaskCompletionSource<(long Numerator, long Denominator)> timebase = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly Regex Config = new(@"config in time_base:\s*(\d+)/(\d+)");
        private static readonly Regex FramePts = new(@"\bn:\s*\d+\s+pts:\s*(-?\d+)");
        public bool Hardware { get; }
        public VideoStream(string executable, string path, long tick, int width, int height, CancellationToken token, bool hardware,
            Func<IEnumerable<string>, NativeMediaProcess>? startProcess)
            : base(executable, FfmpegVideoDecodeArguments.Build(path, tick, width, height, hardware), token, startProcess)
        { start = tick; this.width = width; this.height = height; Hardware = hardware; size = checked(width * height * 4); ErrorTask = Task.Factory.StartNew(ReadMetadata, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        public NativeDecoderCandidate Candidate(long id, long used, bool eligible) => new(id, used, start, lastRequest, 0, eligible ? 1 : 0);
        public async Task<ImmutableArray<byte>> FrameAsync(long tick, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ended is not null) throw ended;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var tb = await timebase.Task.WaitAsync(timeout.Token);
            long ToPts(long t) => TimelineTime.RoundHalfUp((System.Numerics.BigInteger)t * tb.Denominator,
                (System.Numerics.BigInteger)TimelineTime.TicksPerSecond * tb.Numerator);
            long target = ToPts(tick) - ToPts(start);
            lastRequest = tick;
            while (last.IsDefault || lastPts < target)
            {
                byte[] bytes;
                try { bytes = await ReadAsync(size, timeout.Token); }
                catch (MediaEndOfStreamException exception)
                {
                    // A failed request may have consumed newer frames before EOF.
                    // Those pixels cannot stand in for an earlier successful tick.
                    bool exact = lastSuccessfulRequest is not null && lastSuccessfulPts == lastPts;
                    ended = new(exception.Message) { RetainedRequestTick = exact ? lastSuccessfulRequest : null,
                        RetainedFrame = exact ? last : default };
                    throw ended;
                }
                long pts = await timestamps.Reader.ReadAsync(timeout.Token);
                if (pts <= lastPts) throw new InvalidDataException("Non-monotonic source video timestamps.");
                lastPts = pts; last = ImmutableCollectionsMarshal.AsImmutableArray(NativeDecodedMedia.Rgba(bytes, width, height));
            }
            lastSuccessfulRequest = tick;
            lastSuccessfulPts = lastPts;
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
            if (frame.Success)
                timestamps.Writer.WriteAsync(long.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture), Lifetime)
                    .AsTask().GetAwaiter().GetResult();
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

        public NativeDecoderCandidate Candidate(long id, long used, bool eligible) => new(id, used, start, 0, consumed, eligible ? 1 : 0);
        public async Task<ImmutableArray<float>> BlockAsync(int count, CancellationToken token)
        {
            var bytes = await ReadAsync(count * 8, token, padPcmTail: true);
            var samples = NativeDecodedMedia.Pcm(bytes, count, 2);
            consumed += count; return ImmutableCollectionsMarshal.AsImmutableArray(samples);
        }
    }
}
