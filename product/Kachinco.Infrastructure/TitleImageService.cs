using System.Collections.Immutable;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Uses the ordinary native evaluator and compositor, like preview_frame. Captured
// PNGs are immutable owned assets, retained after commit for shared Undo/Redo.
public sealed class TitleImageService(string directory, IPreviewImageEncoder encoder, ICaptionRasterizer? captions = null,
    Func<string?, IFrameRenderer>? rendererFactory = null)
{
    public async Task<RenderedVideoFrame> RenderFrameAsync(Project project, Guid sequenceId, long ticks, string? projectPath,
        CancellationToken token = default)
    {
        var sequence = project.Sequences.First(s => s.Id == sequenceId);
        if (ticks < 0 || ticks >= sequence.DurationTicks) throw new InvalidDataException("Frame is outside the sequence.");
        long frame = Math.Min(TimelineTime.TicksToFrame(ticks, sequence.Settings.FrameRate),
            TimelineTime.FrameCount(sequence.DurationTicks, sequence.Settings.FrameRate) - 1);
        long tick = TimelineTime.FrameToTicks(frame, sequence.Settings.FrameRate);
        var created = TimelineEvaluator.Create(project, sequenceId);
        if (!created.Success) throw new InvalidDataException(string.Join(" / ", created.Diagnostics.Select(d => d.Message)));
        using var evaluator = created.Value!;
        var evaluated = evaluator.Evaluate(tick);
        if (!evaluated.Success) throw new InvalidDataException(string.Join(" / ", evaluated.Diagnostics.Select(d => d.Message)));
        if (evaluated.Value!.VideoLayers.Length > 64) throw new InvalidDataException("Too many layers to capture.");
        using var decoder = new FfmpegMediaDecoder();
        var renderer = rendererFactory?.Invoke(projectPath) ?? new SharedFrameRenderer(decoder, projectPath, captions);
        var rendered = await renderer.RenderAsync(project, evaluated.Value, frame, token);
        if (!rendered.Success) throw new InvalidDataException(string.Join(" / ", rendered.Diagnostics.Select(d => d.Message)));
        return rendered.Value!;
    }
    public async Task<PreparedTitleImage> PrepareAsync(Project project, Guid sequenceId, long ticks, string? projectPath,
        CancellationToken token = default)
    {
        var frame = await RenderFrameAsync(project, sequenceId, ticks, projectPath, token);
        var bytes = await encoder.EncodeAsync(frame, frame.Width, frame.Height, "png", 100, token);
        if (bytes.Length is < 1 or > 64 * 1024 * 1024) throw new InvalidDataException("Title image is too large.");
        Directory.CreateDirectory(directory);
        Guid id = Guid.NewGuid();
        string path = Path.GetFullPath(Path.Combine(directory, id.ToString("N") + ".png"));
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
            token.ThrowIfCancellationRequested();
            return new(new(id, "Title frame", path, MediaKind.Image, TimelineTime.TicksPerSecond), new(id, frame.Tick));
        }
        catch { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }
    public async Task<RenderedVideoFrame> RenderSelectionAsync(Project project, Guid sequenceId, string? projectPath,
        CancellationToken token = default)
    {
        var sequence = project.Sequences.First(s => s.Id == sequenceId);
        var title = sequence.TitleImage ?? throw new InvalidDataException("Choose a title image first.");
        var clip = new Clip(Guid.NewGuid(), title.MediaAssetId, 0, 0, TimelineTime.FrameToTicks(1, sequence.Settings.FrameRate),
            true, ClipAppearance.Default, AudioProperties.Default);
        // Retain all source objects/provenance for validation; only the new image
        // layer is enabled in this derived read-only render snapshot.
        var isolated = sequence with { Tracks = [.. sequence.Tracks.Select(t => t with { Enabled = false }),
            new Track(Guid.NewGuid(), "Title export", TrackKind.Video, true, [clip], [])] };
        var derived = project with { Sequences = project.Sequences.Replace(sequence, isolated) };
        return await RenderFrameAsync(derived, sequenceId, 0, projectPath, token);
    }
}
public sealed class PreparedTitleImage(MediaAsset asset, TitleImageSelection selection) : IDisposable
{
    private bool committed;
    public MediaAsset Asset { get; } = asset;
    public TitleImageSelection Selection { get; } = selection;
    public EditBatch Batch(Guid sequenceId, long revision) => new([new RegisterMedia(Asset), new SetTitleImage(sequenceId, Selection)], revision);
    public void Keep() => committed = true;
    public void Dispose()
    {
        if (committed) return;
        try { File.Delete(Asset.SourcePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
