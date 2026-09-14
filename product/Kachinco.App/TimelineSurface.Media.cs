using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class TimelineSurface
{
    private readonly MediaVisualizationService visualizer = new();
    private readonly PreviewCache<VisualEntry> mediaVisuals = new(16 * 1024 * 1024, 256);
    private readonly Dictionary<string, VisualWork> visualPlan = [];
    private readonly Dictionary<string, CancellationTokenSource> visualActive = [];
    private readonly Queue<VisualWork> visualPending = new();
    private string? visualizationProjectPath;
    private bool visualDisposed;
    private int visualWorkerCount;
    public int VisualizationWorkers => visualWorkerCount;
    public int PendingVisualizations => visualPending.Count;
    public CacheStatistics VisualizationCache => mediaVisuals.Statistics;
    public void SetMediaContext(string? projectPath) { visualizationProjectPath = projectPath; Rebuild(); }
    public void DisposeVisualizations()
    {
        visualDisposed = true; foreach (var token in visualActive.Values) token.Cancel();
        visualPending.Clear(); visualPlan.Clear(); mediaVisuals.Clear();
    }
    private void SynchronizeMediaVisuals() { } // Rebuild derives visible dependencies from the immutable project.
    private void BeginVisualPlan() => visualPlan.Clear();
    private void SubmitVisualPlan()
    {
        if (visualDisposed) return;
        foreach (var pair in visualActive) if (!visualPlan.ContainsKey(pair.Key)) pair.Value.Cancel();
        visualPending.Clear();
        foreach (var work in visualPlan.Values)
            if (!visualActive.ContainsKey(work.Key) && !mediaVisuals.TryGet(work.Key, out _)) visualPending.Enqueue(work);
        while (visualWorkerCount < 2 && visualPending.Count > 0) { visualWorkerCount++; _ = RunVisualWorker(); }
    }
    private async Task RunVisualWorker()
    {
        try
        {
            while (!visualDisposed && visualPending.TryDequeue(out var work))
            {
                if (mediaVisuals.TryGet(work.Key, out _) || visualActive.ContainsKey(work.Key)) continue;
                using var cancellation = new CancellationTokenSource(); visualActive.Add(work.Key, cancellation);
                VisualEntry? entry = null;
                try
                {
                    var data = await Task.Run(async () => work.Asset.Kind == MediaKind.Mov ?
                        new MediaVisualization(await new FfmpegMediaDecoder().VideoAsync(work.Path, work.SourceTicks, 160, 90, cancellation.Token), 160, 90, [], work.Asset.DurationTicks) :
                        await visualizer.GenerateAsync(work.Asset, work.Path, cancellation.Token), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (work.Stamp != PreviewContext.FileStamp(work.Path)) throw new IOException("Media changed while decoding.");
                    BitmapSource? bitmap = null;
                    if (!data.Rgba.IsDefaultOrEmpty)
                    {
                        var bytes = data.Rgba.ToArray();
                        for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
                        bitmap = BitmapSource.Create(data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null, bytes, data.Width * 4); bitmap.Freeze();
                        data = data with { Rgba = [] };
                    }
                    entry = new(data, bitmap, null);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception e) { entry = new(null, null, e.Message); }
                finally { visualActive.Remove(work.Key); }
                if (entry is not null && !visualDisposed && !cancellation.IsCancellationRequested)
                    mediaVisuals.Put(work.Key, entry, 1024 + (entry.Image is null ? 0 : 160 * 90 * 4) + (entry.Data?.Peaks.Length ?? 0) * 4);
                if (!visualDisposed && drag is null) Rebuild();
            }
        }
        finally { visualWorkerCount--; if (!visualDisposed) SubmitVisualPlan(); }
    }
    private VisualEntry? RequestVisual(MediaAsset asset, long tick)
    {
        var path = MediaReferenceResolver.Inspect(project!, visualizationProjectPath).First(a => a.MediaAssetId == asset.Id);
        if (!path.IsAvailable || path.ResolvedPath is null) return new(null, null, EditorText.VisualFailed);
        string stamp = PreviewContext.FileStamp(path.ResolvedPath);
        string key = $"{asset.Id}|{asset.SourcePath}|{asset.DurationTicks}|{asset.Provenance}|{path.ResolvedPath}|{stamp}|{tick}";
        if (mediaVisuals.TryGet(key, out var cached)) return cached;
        if (visualPlan.Count < 96) visualPlan.TryAdd(key, new(key, asset, path.ResolvedPath, stamp, tick));
        return null;
    }
    private void AddMediaVisual(Grid grid, Clip clip, double width)
    {
        if (project?.Assets.FirstOrDefault(a => a.Id == clip.MediaAssetId) is not { } asset) return;
        double clipLeft = (double)viewport.TicksToPixels(clip.StartTicks);
        double start = Math.Max(0, HorizontalScroll.Value - clipLeft);
        double end = Math.Min(width, HorizontalScroll.Value + TimelineViewportHost.ActualWidth - clipLeft);
        if (end <= start) return;
        var area = new Canvas { Margin = new Thickness(start + 3, 21, 0, 3), Width = Math.Max(0, end - start - 6),
            HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, ClipToBounds = true };
        grid.Children.Insert(0, area);
        if (asset.Kind == MediaKind.Mov)
        {
            foreach (var slot in ThumbnailStrip.Plan(clip, viewport, start, end - start))
            {
                var entry = RequestVisual(asset, slot.SourceTicks);
                FrameworkElement tile = entry?.Image is { } image ? new Image { Source = image, Stretch = Stretch.UniformToFill, Width = Math.Max(.1, slot.Width - 1), Height = 40 } :
                    new TextBlock { Text = entry?.Error is null ? EditorText.VisualLoading : "⚠ " + EditorText.VisualFailed,
                        Width = Math.Max(.1, slot.Width - 1), Foreground = Brushes.White, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
                Canvas.SetLeft(tile, slot.Left); area.Children.Add(tile);
                if (entry?.Error is { } error) grid.ToolTip += "\n" + error;
            }
            return;
        }
        var audio = RequestVisual(asset, 0);
        if (audio?.Data is not { } data || data.Peaks.IsDefaultOrEmpty)
        {
            area.Children.Add(new TextBlock { Text = audio?.Error is null ? EditorText.VisualLoading : "⚠ " + EditorText.VisualFailed, Foreground = Brushes.White, FontSize = 10 });
            return;
        }
        int columns = Math.Clamp((int)Math.Ceiling(end - start), 1, 1024);
        long sourceIn = clip.SourceInTicks + Math.Min(clip.DurationTicks - 1, viewport.PixelsToTicks((decimal)start));
        long duration = Math.Max(1, Math.Min(clip.SourceInTicks + clip.DurationTicks - sourceIn, viewport.PixelsToTicks((decimal)(end - start))));
        var peaks = WaveformProjection.Crop(data.Peaks, data.DurationTicks, sourceIn, duration, columns);
        var bars = new GeometryGroup();
        for (int i = 0; i < peaks.Length; i++)
        {
            double peak = peaks[i] * .48;
            bars.Children.Add(new RectangleGeometry(new Rect(i + .1, .5 - peak, .8, Math.Max(.003, 2 * peak))));
        }
        bars.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, columns, 1))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(182, 238, 204)), null, bars)); group.Freeze();
        area.Children.Add(new Image { Source = new DrawingImage(group), Width = area.Width, Height = 40, Stretch = Stretch.Fill });
    }
    private sealed record VisualWork(string Key, MediaAsset Asset, string Path, string Stamp, long SourceTicks);
    private sealed record VisualEntry(MediaVisualization? Data, BitmapSource? Image, string? Error);
}
