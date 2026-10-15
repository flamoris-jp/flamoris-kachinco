using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed record ClipDownloadInput(Guid SequenceId, Guid ClipId, long StartTicks = 0,
    long? DurationTicks = null, PreviewQuality Quality = PreviewQuality.Half, bool InspectDisabled = false);

// Attachment-scoped derived output, never an editor or a raw filesystem capability.
public sealed class ClipDownloadStore : IDisposable
{
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    public const int MaximumChunkBytes = 128 * 1024;
    public const long MaximumDurationTicks = 30 * TimelineTime.TicksPerSecond;
    private readonly object gate = new();
    private readonly SemaphoreSlim admission;
    private readonly TimeProvider clock;
    private readonly Func<IMediaDecoder> decoderFactory;
    private readonly IVideoEncodingBackend encoder;
    private readonly string root;
    private readonly ITimer expiry;
    private Job? job;
    private bool disposed;
    private Task completion = Task.CompletedTask;
    public Task Completion { get { lock (gate) return completion; } }

    private sealed class Job(Guid id, ProjectSnapshot snapshot, string document, string? projectPath,
        ClipDownloadInput input, Project project, Sequence sequence, MediaAsset asset, long start, long duration,
        string sourcePath, string stamp, DateTimeOffset now)
    {
        public Guid Id { get; } = id;
        public ProjectSnapshot Snapshot { get; } = snapshot;
        public string Document { get; } = document;
        public string? ProjectPath { get; } = projectPath;
        public ClipDownloadInput Input { get; } = input;
        public Project Project { get; } = project;
        public Sequence Sequence { get; } = sequence;
        public MediaAsset Asset { get; } = asset;
        public long Start { get; } = start;
        public long Duration { get; } = duration;
        public string SourcePath { get; } = sourcePath;
        public string Stamp { get; } = stamp;
        public DateTimeOffset Touched { get; set; } = now;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public string State { get; set; } = "rendering";
        public string? Error { get; set; }
        public string? Path { get; set; }
        public object? Metadata { get; set; }
        public long Size { get; set; }
        public bool Retired { get; set; }
    }

    public ClipDownloadStore(SemaphoreSlim admission, string? temporaryRoot = null, TimeProvider? clock = null,
        Func<IMediaDecoder>? decoderFactory = null, IVideoEncodingBackend? encoder = null)
    {
        this.admission = admission; this.clock = clock ?? TimeProvider.System;
        this.decoderFactory = decoderFactory ?? (() => new FfmpegMediaDecoder());
        this.encoder = encoder ?? new FfmpegEncodingBackend();
        root = Path.Combine(temporaryRoot ?? Path.GetTempPath(), "kachinco-download-" + Guid.NewGuid().ToString("N"));
        expiry = this.clock.CreateTimer(_ => { lock (gate) { Expire(); } }, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // Called under the host's read lane; no asynchronous work or project mutation here.
    public object Begin(ProjectSnapshot snapshot, string document, string? projectPath, ClipDownloadInput input)
    {
        var project = snapshot.Project ?? throw new McpFault("project_required");
        var sequence = project.Sequences.FirstOrDefault(s => s.Id == input.SequenceId) ?? throw new McpFault("sequence_not_found");
        var track = sequence.Tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == input.ClipId)) ?? throw new McpFault("clip_not_found");
        var clip = track.Clips.Single(c => c.Id == input.ClipId);
        var asset = project.Assets.Single(a => a.Id == clip.MediaAssetId);
        long duration = input.DurationTicks ?? (clip.DurationTicks - input.StartTicks);
        if (input.StartTicks < 0 || duration <= 0 || input.StartTicks >= clip.DurationTicks || duration > clip.DurationTicks - input.StartTicks ||
            duration > MaximumDurationTicks || input.Quality is not (PreviewQuality.Full or PreviewQuality.Half or PreviewQuality.Quarter))
            throw new McpFault("invalid_download_range");
        if (sequence.Settings is not { Width: 1920, Height: 1080 } and not { Width: 1080, Height: 1920 })
            throw new McpFault("download_canvas_limit");
        var source = MediaReferenceResolver.Inspect(project, projectPath).Single(p => p.MediaAssetId == asset.Id);
        if (!source.IsAvailable || source.ResolvedPath is null) throw new McpFault("MEDIA_MISSING");
        if (input.InspectDisabled) clip = clip with { Enabled = true, Audio = clip.Audio with { Muted = false } };
        var isolated = sequence with { Tracks = [track with { Enabled = track.Enabled || input.InspectDisabled, Clips = [clip], Captions = [] }], Clappers = [], Recipes = [] };
        var isolatedProject = project with { Assets = [asset with { Provenance = null }], Sequences = [isolated] };
        var errors = ProjectValidator.Validate(isolatedProject);
        if (!errors.IsEmpty) throw new McpFault(errors[0].Code);
        lock (gate)
        {
            Demand(); Expire();
            if (job is not null) throw new McpFault("download_limit");
            if (!admission.Wait(0)) throw new McpFault(McpErrors.Busy);
            var next = new Job(Guid.NewGuid(), snapshot, document, projectPath, input, isolatedProject, isolated, asset,
                checked(clip.StartTicks + input.StartTicks), duration, source.ResolvedPath, PreviewContext.FileStamp(source.ResolvedPath), clock.GetUtcNow());
            job = next;
            try { next.Completion = Task.Run(() => RenderAsync(next)); completion = next.Completion; }
            catch { job = null; admission.Release(); next.Cancellation.Dispose(); throw; }
            return Describe(next);
        }
    }

    public object Status(Guid id, ProjectSnapshot snapshot, string document, string? path)
    {
        lock (gate) { var current = Get(id, snapshot, document, path); return Describe(current); }
    }
    public object Read(Guid id, long offset, int count, ProjectSnapshot snapshot, string document, string? path)
    {
        if (offset < 0 || count is < 1 or > MaximumChunkBytes) throw new McpFault("invalid_download_chunk");
        lock (gate)
        {
            var current = Get(id, snapshot, document, path);
            if (current.State != "ready" || current.Path is null) throw new McpFault("download_not_ready");
            if (offset >= current.Size) throw new McpFault("download_offset");
            byte[] bytes = new byte[(int)Math.Min(count, current.Size - offset)];
            using var file = new FileStream(current.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            file.Position = offset; file.ReadExactly(bytes);
            return new { downloadId = id, offset = Text(offset), count = bytes.Length, dataBase64 = Convert.ToBase64String(bytes),
                chunkSha256 = MediaUploadStore.Hash(bytes), eof = offset + bytes.Length == current.Size };
        }
    }
    public bool Cancel(Guid id)
    {
        lock (gate)
        {
            Demand(); Expire();
            if (job is null || job.Id != id) return false;
            Retire(job); return true;
        }
    }
    private Job Get(Guid id, ProjectSnapshot snapshot, string document, string? path)
    {
        Demand(); Expire();
        var current = job is { } value && value.Id == id ? value : throw new McpFault("download_not_found");
        string? error = current.Document != document ? McpErrors.StaleDocument :
            current.Snapshot.Revision != snapshot.Revision || current.ProjectPath != path ? McpErrors.StaleRevision :
            PreviewContext.FileStamp(current.SourcePath) != current.Stamp ? "source_changed" : null;
        if (error is not null) { Retire(current); throw new McpFault(error); }
        current.Touched = clock.GetUtcNow(); return current;
    }
    private static object Describe(Job value) => new { downloadId = value.Id, state = value.State,
        error = value.Error, metadata = value.Metadata, chunkBytes = MaximumChunkBytes,
        maximumDurationTicks = Text(MaximumDurationTicks), maximumFileBytes = Text(MaximumFileBytes), expiresAfterSeconds = 600 };

    private async Task RenderAsync(Job current)
    {
        var token = current.Cancellation.Token;
        current.Cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid temporary directory.");
            bool audio = current.Asset.Kind == MediaKind.Wav;
            string path = Path.Combine(root, current.Id.ToString("N") + (audio ? ".wav" : ".mp4"));
            using var evaluator = TimelineEvaluator.Create(current.Project, current.Sequence.Id).Value ?? throw new InvalidDataException("Invalid render snapshot.");
            var decoder = decoderFactory();
            long samples = TimelineTime.SampleCount(current.Duration, 48000);
            long frames = TimelineTime.FrameCount(current.Duration, current.Sequence.Settings.FrameRate);
            try
            {
                var media = new ClipSource(current, evaluator, new SharedFrameRenderer(decoder, current.ProjectPath), new SharedAudioRenderer(decoder, current.ProjectPath));
                if (audio) await WriteWavAsync(path, media, samples, token);
                else
                {
                    var settings = current.Sequence.Settings with { Width = current.Sequence.Settings.Width / (int)current.Input.Quality,
                        Height = current.Sequence.Settings.Height / (int)current.Input.Quality };
                    var result = await encoder.EncodeAsync(new(current.Id, path, ExportPreset.ClipPreviewH264Mp4,
                        settings, frames, 48000, 2, samples), media, new(null), null, token);
                    if (result.Stage != ExportStage.Completed) throw new InvalidDataException(result.Stage == ExportStage.Cancelled ? "download_cancelled" : "download_render_failed");
                }
            }
            finally { (decoder as IDisposable)?.Dispose(); }
            token.ThrowIfCancellationRequested();
            if (PreviewContext.FileStamp(current.SourcePath) != current.Stamp) throw new InvalidDataException("source_changed");
            long size = new FileInfo(path).Length;
            if (size is < 1 or > MaximumFileBytes) throw new InvalidDataException("download_size_limit");
            string digest;
            await using (var file = File.OpenRead(path)) digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (disposed || current.Retired) return;
                var fps = current.Sequence.Settings.FrameRate;
                current.Path = path; current.Size = size;
                current.Metadata = new { sequenceId = current.Sequence.Id, clipId = current.Input.ClipId,
                    documentToken = current.Document, projectRevision = Text(current.Snapshot.Revision),
                    startTicks = Text(current.Input.StartTicks), durationTicks = Text(current.Duration),
                    inspectDisabled = current.Input.InspectDisabled, edited = true,
                    fileName = "clip-" + current.Input.ClipId.ToString("N") + (audio ? ".wav" : ".mp4"),
                    mimeType = audio ? "audio/wav" : "video/mp4", sizeBytes = Text(size), sha256 = digest,
                    sampleRate = audio ? 48000 : (int?)null, channels = audio ? 2 : (int?)null,
                    sampleCount = audio ? Text(samples) : null, frameCount = audio ? null : Text(frames),
                    encodedDurationTicks = Text(audio ? TimelineTime.SampleToTicks(samples, 48000) : TimelineTime.FrameToTicks(frames, fps)),
                    width = audio ? null : (int?)(current.Sequence.Settings.Width / (int)current.Input.Quality),
                    height = audio ? null : (int?)(current.Sequence.Settings.Height / (int)current.Input.Quality),
                    fpsNumerator = audio ? null : (int?)fps.Numerator, fpsDenominator = audio ? null : (int?)fps.Denominator,
                    embeddedAudio = false, background = audio ? null : "opaque-black" };
                current.State = "ready";
            }
        }
        catch (OperationCanceledException) { lock (gate) { current.State = "cancelled"; current.Error = "download_cancelled"; } }
        catch (Exception e)
        { lock (gate) { current.State = "failed"; current.Error = e is InvalidDataException && e.Message is "source_changed" or "download_size_limit" ? e.Message : "download_render_failed"; } }
        finally
        {
            lock (gate) { if (current.State != "ready" || current.Retired || disposed) DeleteOutput(); if (current.Retired || disposed) current.Cancellation.Dispose(); }
            admission.Release();
        }
    }

    private sealed class ClipSource(Job job, TimelineEvaluator evaluator, SharedFrameRenderer video, SharedAudioRenderer audio) : IRenderedMediaSource
    {
        public async IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync([EnumeratorCancellation] CancellationToken token)
        {
            long count = TimelineTime.FrameCount(job.Duration, job.Sequence.Settings.FrameRate);
            for (long index = 0; index < count; index++)
            {
                long tick = TimelineTime.FrameToTicks(index, job.Sequence.Settings.FrameRate);
                var frame = evaluator.Evaluate(checked(job.Start + Math.Min(tick, job.Duration - 1)));
                if (!frame.Success) throw new InvalidDataException("download_render_failed");
                var rendered = await video.RenderPreviewAsync(job.Project, frame.Value!, job.Input.Quality, token);
                if (!rendered.Success) throw new InvalidDataException("download_render_failed");
                yield return rendered.Value! with { FrameIndex = index, Tick = tick };
            }
        }
        public async IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync([EnumeratorCancellation] CancellationToken token)
        {
            long count = TimelineTime.SampleCount(job.Duration, 48000);
            long first = Kachinco.Native.NativePlayback.FirstSample(job.Start, 48000);
            long available = TimelineTime.SampleCount(job.Sequence.DurationTicks, 48000);
            for (long index = 0; index < count; index += 48000)
            {
                int length = (int)Math.Min(48000, count - index);
                int renderCount = (int)Math.Min(length, Math.Max(0, available - first - index));
                var samples = new float[length * 2];
                if (renderCount > 0)
                {
                    var rendered = await audio.RenderAsync(evaluator, first + index, renderCount, 48000, 2, token);
                    if (!rendered.Success) throw new InvalidDataException("download_render_failed");
                    rendered.Value!.Samples.CopyTo(samples);
                }
                yield return new(index, 48000, 2, ImmutableArray.CreateRange(samples));
            }
        }
    }
    private static async Task WriteWavAsync(string path, IRenderedMediaSource source, long frames, CancellationToken token)
    {
        long size = checked(44 + frames * 4);
        if (size > MaximumFileBytes) throw new InvalidDataException("download_size_limit");
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), checked((int)size - 8));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 192000);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 4); BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), checked((int)(frames * 4)));
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await file.WriteAsync(header, token);
        await foreach (var block in source.ReadAudioAsync(token))
        {
            var bytes = new byte[block.Samples.Length * 2];
            for (int i = 0; i < block.Samples.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp(Math.Round(block.Samples[i] * 32767), -32768, 32767));
            await file.WriteAsync(bytes, token);
        }
        if (file.Length != size) throw new InvalidDataException("download_render_failed");
    }
    private void Expire() { if (job is { } current && clock.GetUtcNow() - current.Touched >= TimeSpan.FromMinutes(10)) Retire(current); }
    private void Retire(Job current)
    {
        current.Retired = true; current.Cancellation.Cancel(); job = null;
        if (current.Completion.IsCompleted) { DeleteOutput(); current.Cancellation.Dispose(); }
    }
    private void DeleteOutput() { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    private void Demand() { if (disposed) throw new McpFault("download_closed"); }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; if (job is { } current) Retire(current); }
        expiry.Dispose();
    }
}
