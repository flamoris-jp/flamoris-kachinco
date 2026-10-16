using Kachinco.Core;
using Flamoris.Logging;

namespace Kachinco.Infrastructure;

public sealed record ExportCompositionStatistics(long CpuFrames, long GpuFrames, ulong PeakPayloadBytes, string? FallbackReason);

// An export owns its device and sticky CPU recovery independently of interactive preview.
// The preview backend name is historical; all pixel/evaluation authority is shared.
public sealed class ExportFrameRenderer : IFrameRenderer, IDisposable
{
    private readonly PreviewRenderBackend backend;
    private readonly SharedFrameRenderer renderer;
    private long cpuFrames, gpuFrames;
    private ulong peakBytes;
    private string? fallback;

    public ExportFrameRenderer(IMediaDecoder decoder, string? projectPath = null, ICaptionRasterizer? captions = null,
        PreviewBackendPreference preference = PreviewBackendPreference.Auto, FlamorisLogger? logger = null,
        PreviewCompositorFactory? compositorFactory = null)
    {
        backend = new(preference, compositorFactory);
        renderer = new(decoder, projectPath, captions, logger, backend);
    }
    public ExportCompositionStatistics Statistics => new(cpuFrames, gpuFrames, peakBytes, fallback);
    public async ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long frameIndex, CancellationToken token)
    {
        var result = await renderer.RenderAsync(project, frame, frameIndex, token);
        Record(result.Success); return result;
    }
    public async ValueTask<Result<RenderedVideoFrame>> RenderPreviewAsync(Project project, EvaluatedFrame frame, PreviewQuality quality, CancellationToken token)
    {
        var result = await renderer.RenderPreviewAsync(project, frame, quality, token);
        Record(result.Success); return result;
    }
    private void Record(bool success)
    {
        var state = backend.Diagnostics;
        if (success) { if (state.Active == "D3D11") gpuFrames++; else cpuFrames++; }
        peakBytes = Math.Max(peakBytes, state.AllocatedBytes);
        // Identity-copy is an optimization, not a hardware failure. Keep the actual
        // failure when a later identity frame selects the CPU fast path.
        if (state.FallbackReason is { } reason && !reason.StartsWith("Identity frame", StringComparison.Ordinal)) fallback = reason;
    }
    public void Dispose() => backend.Dispose();
}
