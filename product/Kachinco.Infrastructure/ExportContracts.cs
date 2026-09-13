using System.Collections.Immutable;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public enum ExportPreset { YoutubeH264AacMp4 }
public enum ExportStage { Planning, Rendering, Encoding, Completed, Cancelled, Failed }
public sealed record ExportRequest(Guid JobId, Guid SequenceId, string OutputPath, ExportPreset Preset, long? ExpectedRevision = null);
public sealed record ExportProgress(Guid JobId, ExportStage Stage, long FramesCompleted, long TotalFrames, long AudioSamplesCompleted);
public sealed record ExportResult(Guid JobId, ExportStage Stage, string? OutputPath, ImmutableArray<Diagnostic> Diagnostics);

public interface IExportService
{
    // Capture the supplied revision/project once; cancellation must propagate to every stage.
    Task<ExportResult> ExportAsync(ProjectSnapshot snapshot, ExportRequest request,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken);
}

// App configuration, never Project persistence. Explicit path takes priority over PATH.
public sealed record FfmpegSettings(string? ExecutablePath);
public sealed record FfmpegInstallation(string ExecutablePath, string Version);
public interface IFfmpegExecutableResolver
{
    Task<Result<FfmpegInstallation>> ResolveAsync(FfmpegSettings settings, CancellationToken cancellationToken);
}

public sealed record EncodingRequest(Guid JobId, string OutputPath, ExportPreset Preset,
    SequenceSettings VideoSettings, long FrameCount, int AudioSampleRate, int AudioChannels, long AudioSampleCount);
public interface IRenderedMediaSource
{
    // Already rendered/mixed data. The encoder never interprets clip or subtitle semantics.
    IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync(CancellationToken cancellationToken);
}
public interface IVideoEncodingBackend
{
    Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media,
        FfmpegSettings settings, IProgress<ExportProgress>? progress, CancellationToken cancellationToken);
}

// Honest foundation placeholder. No file/process side effects; do not report encoded output.
public sealed class FfmpegEncodingBackend : IVideoEncodingBackend
{
    public Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media,
        FfmpegSettings settings, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        bool cancelled = cancellationToken.IsCancellationRequested;
        return Task.FromResult(new ExportResult(request.JobId, cancelled ? ExportStage.Cancelled : ExportStage.Failed, null,
            [Diagnostic.Error(cancelled ? "CANCELLED" : "EXPORT_NOT_IMPLEMENTED",
                cancelled ? "Export cancelled." : "FFmpeg production encoding is a later milestone; no output was written.")]));
    }
}
