using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

public static class CliOperations
{
    public static bool ForeignPath(string path) => OperatingSystem.IsLinux() &&
        (path.Contains('\\') || path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');
    public static System.Collections.Immutable.ImmutableArray<MediaAvailability> InspectMedia(Project project, string path) =>
        [.. MediaReferenceResolver.Inspect(project, path).Select(p => ForeignPath(p.SourcePath)
            ? p with { ResolvedPath = null, IsAvailable = false, Diagnostics = [new("MEDIA_PATH_RELINK_REQUIRED", DiagnosticSeverity.Warning,
                "Explicitly relink this Windows media path on Linux.", p.MediaAssetId)] } : p)];
    public static void CheckCanvas(Sequence sequence)
    {
        var s = sequence.Settings;
        if (s.Width > 4096 || s.Height > 4096 || (long)s.Width * s.Height > 8_294_400)
            throw new InvalidDataException("CANVAS_LIMIT: headless rendering supports at most 4096 per edge and 8294400 pixels.");
    }
    public static void CheckMedia(Project project, string path)
    {
        var missing = InspectMedia(project, path).Where(p => !p.IsAvailable).ToArray();
        if (missing.Length != 0) throw new InvalidDataException("MEDIA_MISSING: relink assets " + string.Join(", ", missing.Select(p => p.MediaAssetId)));
    }
    public static async Task<ExportResult> ExportAsync(ProjectSnapshot snapshot, string projectPath, ExportRequest request,
        IProgress<ExportProgress>? progress, CancellationToken token, FfmpegSettings? settings = null)
    {
        using var decoder = new FfmpegMediaDecoder();
        using var renderer = new ExportFrameRenderer(decoder, projectPath, new LinuxCaptionRasterizer(), PreviewBackendPreference.Cpu);
        return await new SnapshotExportService(renderer,
            new SharedAudioRenderer(decoder, projectPath), new FfmpegEncodingBackend(), settings ?? new(null, VideoEncoderPreference.Cpu), projectPath)
            .ExportAsync(snapshot, request, progress, token);
    }
    public static FfmpegSettings Encoding(string? encoder, string? device) => new(null, encoder switch {
        null or "auto" => VideoEncoderPreference.Auto, "cpu" => VideoEncoderPreference.Cpu,
        "vaapi" => VideoEncoderPreference.Vaapi, _ => throw new ArgumentException("Encoder must be auto, cpu or vaapi.") },
        VaapiDevice: device ?? "/dev/dri/renderD128", RequireHardware: encoder == "vaapi");
    public static async Task<byte[]> FrameAsync(Project project, string path, Guid sequenceId, long ticks, string format, CancellationToken token)
    {
        var sequence = project.Sequences.Single(s => s.Id == sequenceId); CheckCanvas(sequence); CheckMedia(project, path);
        if (ticks < 0 || ticks >= sequence.DurationTicks) throw new ArgumentException("Frame time is outside the sequence.");
        var plan = ExportPlanner.Create(project, sequenceId);
        if (!plan.Success) throw new InvalidDataException("Export plan failed.");
        using var evaluation = plan.Value!.Evaluator;
        long index = Math.Min(TimelineTime.TicksToFrame(ticks, sequence.Settings.FrameRate), plan.Value.FrameCount - 1);
        using var decoder = new FfmpegMediaDecoder();
        var frame = await new PreviewService(new SharedFrameRenderer(decoder, path, new LinuxCaptionRasterizer())).RenderAsync(plan.Value, index, token);
        if (!frame.Success) throw new InvalidDataException(string.Join("; ", frame.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var size = PreviewFrameCapture.OutputSize(sequence.Settings.Width, sequence.Settings.Height, 1920);
        return await new FfmpegImageEncoder().EncodeAsync(frame.Value!, size.Width, size.Height, format, 90, token);
    }
    public static void NewOutput(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new IOException("OUTPUT_EXISTS: choose a new output path.");
        if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path)))) throw new IOException("Output directory does not exist.");
    }
    public static async Task PublishAsync(string path, byte[] bytes, CancellationToken token)
    {
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".kachinco-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { await File.WriteAllBytesAsync(temp, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temp, path, false); }
        finally { File.Delete(temp); }
    }
}
