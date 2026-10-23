using System.Collections.Immutable;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public enum VideoEncoderPreference { Auto, Cpu, Nvenc, Vaapi }
public enum ExportPreset { YoutubeH264AacMp4, ClipPreviewH264Mp4 }
public enum ExportStage { Planning, Rendering, Encoding, Completed, Cancelled, Failed }
public sealed record ExportRequest(Guid JobId, Guid SequenceId, string OutputPath, ExportPreset Preset, long? ExpectedRevision = null);
public sealed record ExportProgress(Guid JobId, ExportStage Stage, long FramesCompleted, long TotalFrames, long AudioSamplesCompleted, string? Detail = null);
public sealed record ExportResult(Guid JobId, ExportStage Stage, string? OutputPath, ImmutableArray<Diagnostic> Diagnostics, ExportMetrics? Metrics = null);

public interface IExportService
{
    // Capture the supplied revision/project once; cancellation must propagate to every stage.
    Task<ExportResult> ExportAsync(ProjectSnapshot snapshot, ExportRequest request,
        IProgress<ExportProgress>? progress, CancellationToken cancellationToken);
}

// App configuration, never Project persistence. Explicit path takes priority over PATH.
public sealed record FfmpegSettings(string? ExecutablePath, VideoEncoderPreference Encoder = VideoEncoderPreference.Auto,
    int NvencDevice = 0, string VaapiDevice = "/dev/dri/renderD128", bool RequireHardware = false);
public sealed record ExportMetrics(string VideoEncoder, int? NvencDevice, string? EncoderFallbackReason,
    double TotalMilliseconds, double VideoRenderMilliseconds, double VideoPipelineMilliseconds,
    double AudioRenderMilliseconds, double MuxMilliseconds, ExportCompositionStatistics? Composition = null,
    string? VaapiDevice = null);
public sealed record FfmpegInstallation(string ExecutablePath, string Version);
public interface IFfmpegExecutableResolver
{
    Task<Result<FfmpegInstallation>> ResolveAsync(FfmpegSettings settings, CancellationToken cancellationToken);
}

public sealed record EncodingRequest(Guid JobId, string OutputPath, ExportPreset Preset,
    SequenceSettings VideoSettings, long FrameCount, int AudioSampleRate, int AudioChannels, long AudioSampleCount);
public interface IRenderedMediaSource
{
    // Replayable from frame zero on every enumeration (one hardware failure retry).
    // Already rendered/mixed data. The encoder never interprets clip or subtitle semantics.
    IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync(CancellationToken cancellationToken);
}
public interface IVideoEncodingBackend
{
    Task<ExportResult> EncodeAsync(EncodingRequest request, IRenderedMediaSource media,
        FfmpegSettings settings, IProgress<ExportProgress>? progress, CancellationToken cancellationToken);
}
