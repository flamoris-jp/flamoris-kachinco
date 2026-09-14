using System.Runtime.CompilerServices;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class SnapshotExportService(IFrameRenderer video, IAudioRenderer audio, IVideoEncodingBackend backend, FfmpegSettings settings, string? projectPath = null) : IExportService
{
    public async Task<ExportResult> ExportAsync(ProjectSnapshot snapshot, ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        if (snapshot.Project is null || request.ExpectedRevision is { } rev && rev != snapshot.Revision)
            return Failure(request.JobId, "REVISION_CONFLICT", "Query the current project before export.");
        try
        {
            string output = Path.GetFullPath(request.OutputPath);
            var paths = MediaReferenceResolver.Inspect(snapshot.Project, projectPath);
            if (paths.Any(x => x.ResolvedPath is { } path && string.Equals(path, output, StringComparison.OrdinalIgnoreCase)) ||
                projectPath is not null && string.Equals(Path.GetFullPath(projectPath), output, StringComparison.OrdinalIgnoreCase))
                return Failure(request.JobId, "OUTPUT_IS_INPUT", "Choose an output separate from project and media sources.");
            var planned = ExportPlanner.Create(snapshot.Project, request.SequenceId);
            if (!planned.Success) return new(request.JobId, ExportStage.Failed, null, planned.Diagnostics);
            var plan = planned.Value!;
            return await backend.EncodeAsync(new(request.JobId, output, request.Preset, plan.Evaluator.Sequence.Settings,
                plan.FrameCount, plan.AudioSampleRate, plan.AudioChannels, plan.AudioSampleCount), new RenderSource(plan, video, audio), settings, progress, cancellationToken);
        }
        catch (OperationCanceledException) { return new(request.JobId, ExportStage.Cancelled, null, []); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        { return Failure(request.JobId, "EXPORT_FAILED", e.Message); }
    }
    private static ExportResult Failure(Guid id, string code, string message) => new(id, ExportStage.Failed, null, [Diagnostic.Error(code, message)]);
    private sealed class RenderSource(ExportPlan plan, IFrameRenderer video, IAudioRenderer audio) : IRenderedMediaSource
    {
        public async IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (long i = 0; i < plan.FrameCount; i++)
            {
                var evaluated = plan.EvaluateFrame(i);
                if (!evaluated.Success) throw new InvalidDataException(evaluated.Diagnostics[0].Message);
                var result = await video.RenderAsync(plan.Evaluator.Project, evaluated.Value!, i, cancellationToken);
                if (!result.Success) throw new InvalidDataException(result.Diagnostics[0].Message);
                yield return result.Value!;
            }
        }
        public async IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (long i = 0; i < plan.AudioSampleCount; i += 48000)
            {
                var result = await audio.RenderAsync(plan.Evaluator, i, (int)Math.Min(48000, plan.AudioSampleCount - i), 48000, 2, cancellationToken);
                if (!result.Success) throw new InvalidDataException(result.Diagnostics[0].Message);
                yield return result.Value!;
            }
        }
    }
}
