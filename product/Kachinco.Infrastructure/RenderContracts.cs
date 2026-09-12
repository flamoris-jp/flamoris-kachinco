using System.Collections.Immutable;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Owned immutable buffers; future pooling must retain ownership through consumption.
// RGBA8: top-to-bottom, tightly packed width*4 stride, encoded SDR sRGB, straight alpha.
public sealed record RenderedVideoFrame(long FrameIndex, long Tick, int Width, int Height, ImmutableArray<byte> Rgba8);
// Interleaved normalized float32 PCM, channel order specified by production audio policy.
public sealed record RenderedAudioBlock(long FirstSample, int SampleRate, int Channels, ImmutableArray<float> Samples);

public interface IFrameRenderer
{
    ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long frameIndex, CancellationToken cancellationToken);
}
public interface IAudioRenderer
{
    // Map each sample using TimelineTime; use evaluator.EvaluateAudioRange, never video-frame sampling.
    ValueTask<Result<RenderedAudioBlock>> RenderAsync(TimelineEvaluator evaluator, long firstSample,
        int sampleCount, int sampleRate, int channels, CancellationToken cancellationToken);
}

public sealed class PreviewService(IFrameRenderer renderer)
{
    public async ValueTask<Result<RenderedVideoFrame>> RenderAsync(ExportPlan plan, long frameIndex, CancellationToken cancellationToken = default)
    {
        var evaluated = plan.EvaluateFrame(frameIndex);
        if (!evaluated.Success) return new(null, evaluated.Diagnostics);
        return await renderer.RenderAsync(plan.Evaluator.Project, evaluated.Value!, frameIndex, cancellationToken);
    }
}

public sealed class ExportPlan
{
    public TimelineEvaluator Evaluator { get; }
    public long FrameCount { get; }
    public int AudioSampleRate => 48000;
    public int AudioChannels => 2;
    public long AudioSampleCount { get; }
    internal ExportPlan(TimelineEvaluator evaluator)
    {
        Evaluator = evaluator;
        FrameCount = TimelineTime.FrameCount(evaluator.Sequence.DurationTicks, evaluator.Sequence.Settings.FrameRate);
        AudioSampleCount = TimelineTime.SampleCount(evaluator.Sequence.DurationTicks, AudioSampleRate);
    }
    public Result<EvaluatedFrame> EvaluateFrame(long frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= FrameCount)
            return Result<EvaluatedFrame>.Fail(Diagnostic.Error("FRAME_OUT_OF_RANGE", "Frame index is outside the export plan.", Evaluator.Sequence.Id));
        return Evaluator.Evaluate(TimelineTime.FrameToTicks(frameIndex, Evaluator.Sequence.Settings.FrameRate));
    }
}

public static class ExportPlanner
{
    public static Result<ExportPlan> Create(Project project, Guid sequenceId)
    {
        var evaluator = TimelineEvaluator.Create(project, sequenceId);
        if (!evaluator.Success) return new(null, evaluator.Diagnostics);
        try { return Result<ExportPlan>.Ok(new(evaluator.Value!)); }
        catch (OverflowException) { return Result<ExportPlan>.Fail(Diagnostic.Error("EXPORT_RANGE_OVERFLOW", "Export frame/sample count is too large.", sequenceId)); }
    }
}
