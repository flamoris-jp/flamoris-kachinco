using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public enum PreviewQuality { Full = 1, Half = 2, Quarter = 4 }
public sealed record CacheStatistics(long Bytes, int Entries, long Hits, long Misses, long Evictions);

// Owned immutable values only. Entry and byte bounds both apply, including zero-size values.
public sealed class PreviewCache<T>(long byteLimit, int entryLimit = 256)
{
    private readonly object gate = byteLimit >= 0 && entryLimit >= 0 ? new() : throw new ArgumentOutOfRangeException(nameof(byteLimit));
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value, long Size)>> entries = [];
    private readonly LinkedList<(string Key, T Value, long Size)> lru = new();
    private long bytes, hits, misses, evictions;
    public CacheStatistics Statistics { get { lock (gate) return new(bytes, entries.Count, hits, misses, evictions); } }
    public bool TryGet(string key, out T value)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node)) { misses++; value = default!; return false; }
            hits++; lru.Remove(node); lru.AddFirst(node); value = node.Value.Value; return true;
        }
    }
    public void Put(string key, T value, long size)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        lock (gate)
        {
            if (entries.Remove(key, out var old)) { bytes -= old.Value.Size; lru.Remove(old); }
            if (size > byteLimit || entryLimit <= 0) return;
            while (entries.Count >= entryLimit || bytes > byteLimit - size)
            {
                var last = lru.Last!; bytes -= last.Value.Size; entries.Remove(last.Value.Key); lru.RemoveLast(); evictions++;
            }
            entries.Add(key, lru.AddFirst((key, value, size))); bytes += size;
        }
    }
    public void Clear() { lock (gate) { entries.Clear(); lru.Clear(); bytes = 0; } }
}

public sealed class PreviewContext
{
    public ProjectSnapshot Snapshot { get; }
    public TimelineEvaluator Evaluator { get; }
    public Project Project => Evaluator.Project;
    public Sequence Sequence => Evaluator.Sequence;
    public string? ProjectPath { get; }
    private readonly Dictionary<Guid, MediaAsset> assets;
    private readonly Dictionary<Guid, string?> paths;
    private readonly Dictionary<Guid, string> initialMediaKeys;
    private PreviewContext(ProjectSnapshot snapshot, TimelineEvaluator evaluator, string? path)
    {
        Snapshot = snapshot; Evaluator = evaluator; ProjectPath = path;
        assets = Project.Assets.ToDictionary(a => a.Id);
        paths = MediaReferenceResolver.Inspect(Project, path).ToDictionary(p => p.MediaAssetId, p => p.ResolvedPath);
        initialMediaKeys = assets.Keys.ToDictionary(id => id, MediaKey);
    }
    public static Result<PreviewContext> Create(ProjectSnapshot snapshot, Guid sequenceId, string? projectPath = null)
    {
        if (snapshot.Project is null) return Result<PreviewContext>.Fail(Diagnostic.Error("PROJECT_REQUIRED", "Create a project first."));
        var evaluator = TimelineEvaluator.Create(snapshot.Project, sequenceId);
        return evaluator.Success ? Result<PreviewContext>.Ok(new(snapshot, evaluator.Value!, projectPath)) : new(null, evaluator.Diagnostics);
    }
    public string MediaKey(Guid id) => Hash(new { Asset = assets[id], Path = paths[id], Stamp = FileStamp(paths[id]) });
    public static string FileStamp(string? path)
    {
        if (path is null) return "unresolved";
        try { var f = new FileInfo(path); return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }
    public string VideoKey(long tick, PreviewQuality quality)
    {
        var f = Evaluator.Evaluate(tick);
        if (!f.Success) throw new ArgumentOutOfRangeException(nameof(tick));
        return Hash(new { Sequence.Id, f.Value!.Tick, f.Value.Settings, quality,
            f.Value.VideoLayers, f.Value.Captions, Media = f.Value.VideoLayers.Select(l => MediaKey(l.MediaAssetId)).ToArray() });
    }
    public string AudioKey(long first, int count)
    {
        long start = TimelineTime.SampleToTicks(first, 48000);
        long end = Math.Min(Sequence.DurationTicks, TimelineTime.SampleToTicks(first + count, 48000));
        var plan = Evaluator.EvaluateAudioRange(start, end - start);
        if (!plan.Success) throw new ArgumentOutOfRangeException(nameof(first));
        return Hash(new { Sequence.Id, first, count, Layers = plan.Value, Media = plan.Value.Select(l => MediaKey(l.MediaAssetId)).ToArray() });
    }
    // Conservative intersection signature for the device queue plus in-flight forward work.
    public string WindowKey(long start, long end)
    {
        return Hash(new { ProjectId = Project.Id, SequenceId = Sequence.Id, Sequence.Settings, Sequence.DurationTicks,
            Tracks = Sequence.Tracks.Where(t => t.Enabled && (t.Clips.Any(c => c.Enabled && c.StartTicks < end && c.EndTicks > start) ||
                t.Captions.Any(c => c.Enabled && c.StartTicks < end && c.StartTicks + c.DurationTicks > start))).Select(t => new { t.Id, t.Kind,
                Clips = t.Clips.Where(c => c.Enabled && c.StartTicks < end && c.EndTicks > start)
                    .Select(c => new { Clip = c, Media = initialMediaKeys[c.MediaAssetId] }).ToArray(),
                Captions = t.Captions.Where(c => c.Enabled && c.StartTicks < end && c.StartTicks + c.DurationTicks > start).ToArray() }).ToArray() });
    }
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public interface IInteractivePreviewSource
{
    ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext context, long tick, PreviewQuality quality, bool forward, CancellationToken token);
    ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext context, long firstSample, int count, CancellationToken token);
}

public sealed class InteractivePreviewSource(ICaptionRasterizer? captions = null,
    IMediaDecoder? randomDecoder = null, IMediaDecoder? forwardVideo = null, IMediaDecoder? forwardAudio = null) : IInteractivePreviewSource, IDisposable
{
    private readonly IMediaDecoder random = randomDecoder ?? new FfmpegMediaDecoder();
    private readonly IMediaDecoder video = forwardVideo ?? new FfmpegForwardDecoder();
    private readonly IMediaDecoder audio = forwardAudio ?? new FfmpegForwardDecoder();
    public PreviewCache<RenderedVideoFrame> Frames { get; } = new(96 * 1024 * 1024);
    public PreviewCache<RenderedAudioBlock> Audio { get; } = new(8 * 1024 * 1024);
    public async ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext context, long tick, PreviewQuality quality, bool forward, CancellationToken token)
    {
        if (quality is not (PreviewQuality.Full or PreviewQuality.Half or PreviewQuality.Quarter)) throw new ArgumentOutOfRangeException(nameof(quality));
        token.ThrowIfCancellationRequested();
        string key = context.VideoKey(tick, quality);
        if (Frames.TryGet(key, out var cached)) return Result<RenderedVideoFrame>.Ok(cached);
        var frame = context.Evaluator.Evaluate(tick);
        if (!frame.Success) return new(null, frame.Diagnostics);
        var renderer = new SharedFrameRenderer(forward ? video : random, context.ProjectPath, captions);
        var result = await Task.Run(async () => await renderer.RenderPreviewAsync(context.Project, frame.Value!, quality, token), token);
        token.ThrowIfCancellationRequested();
        if (key != context.VideoKey(tick, quality)) return Result<RenderedVideoFrame>.Fail(Diagnostic.Error("SOURCE_CHANGED", "Media changed while decoding. Retry preview."));
        if (result.Success) Frames.Put(key, result.Value!, result.Value!.Rgba8.Length);
        return result;
    }
    public async ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext context, long firstSample, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string key = context.AudioKey(firstSample, count);
        if (Audio.TryGet(key, out var cached)) return Result<RenderedAudioBlock>.Ok(cached);
        var result = await Task.Run(async () => await new SharedAudioRenderer(audio, context.ProjectPath)
            .RenderAsync(context.Evaluator, firstSample, count, 48000, 2, token), token);
        token.ThrowIfCancellationRequested();
        if (key != context.AudioKey(firstSample, count)) return Result<RenderedAudioBlock>.Fail(Diagnostic.Error("SOURCE_CHANGED", "Media changed while decoding. Retry preview."));
        if (result.Success) Audio.Put(key, result.Value!, result.Value!.Samples.Length * 4L);
        return result;
    }
    public void Dispose()
    {
        Frames.Clear(); Audio.Clear();
        (random as IDisposable)?.Dispose(); (video as IDisposable)?.Dispose(); (audio as IDisposable)?.Dispose();
    }
}
