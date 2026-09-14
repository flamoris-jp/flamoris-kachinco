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
    private readonly SemaphoreSlim visualWorkers = new(2);
    private readonly Dictionary<Guid, VisualEntry> mediaVisuals = [];
    private CancellationTokenSource visualLifetime = new();
    private string? visualizationProjectPath;
    private bool visualDisposed;

    public void SetMediaContext(string? projectPath)
    {
        visualizationProjectPath = projectPath;
        SynchronizeMediaVisuals();
    }
    public void DisposeVisualizations()
    {
        visualDisposed = true; visualLifetime.Cancel(); mediaVisuals.Clear();
    }
    private void SynchronizeMediaVisuals()
    {
        if (visualDisposed) return;
        var used = sequence?.Tracks.SelectMany(t => t.Clips).Select(c => c.MediaAssetId).Distinct().ToHashSet() ?? [];
        // Cache is bounded to currently placed assets, with a fixed memory budget.
        var desired = (project?.Assets.Where(a => used.Contains(a.Id)).Take(128) ?? []).ToDictionary(a => a.Id);
        bool changed = mediaVisuals.Keys.Any(id => !desired.ContainsKey(id)) || desired.Any(pair =>
            !mediaVisuals.TryGetValue(pair.Key, out var entry) || entry.Key != VisualKey(pair.Value));
        if (!changed) return;
        visualLifetime.Cancel(); visualLifetime.Dispose(); visualLifetime = new();
        foreach (var id in mediaVisuals.Keys.ToArray())
            if (!desired.TryGetValue(id, out var asset) || mediaVisuals[id].Key != VisualKey(asset) || mediaVisuals[id].Loading)
                mediaVisuals.Remove(id);
        foreach (var asset in desired.Values)
        {
            if (mediaVisuals.ContainsKey(asset.Id)) continue;
            var entry = new VisualEntry(VisualKey(asset)); mediaVisuals.Add(asset.Id, entry);
            _ = BuildVisualAsync(asset, entry, visualLifetime.Token);
        }
    }
    private string VisualKey(MediaAsset asset)
    {
        var path = MediaReferenceResolver.Inspect(project!, visualizationProjectPath).First(a => a.MediaAssetId == asset.Id);
        if (!path.IsAvailable) return asset.SourcePath + "|unresolved";
        try
        {
            var info = new FileInfo(path.ResolvedPath!);
            return $"{path.ResolvedPath}|{asset.Kind}|{asset.DurationTicks}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return asset.SourcePath + "|unavailable"; }
    }
    private async Task BuildVisualAsync(MediaAsset asset, VisualEntry entry, CancellationToken token)
    {
        try
        {
            var path = MediaReferenceResolver.Inspect(project!, visualizationProjectPath).First(a => a.MediaAssetId == asset.Id);
            await visualWorkers.WaitAsync(token);
            try
            {
                if (!path.IsAvailable) throw new IOException(string.Join(" / ", path.Diagnostics.Select(d => d.Message)));
                entry.Data = await Task.Run(() => visualizer.GenerateAsync(asset, path.ResolvedPath!, token), token);
                if (!entry.Data.Rgba.IsDefaultOrEmpty)
                {
                    // Decoder bytes are RGBA; WPF uses BGRA32.
                    var bytes = entry.Data.Rgba.ToArray();
                    for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
                    var bitmap = BitmapSource.Create(entry.Data.Width, entry.Data.Height, 96, 96, PixelFormats.Bgra32, null, bytes, entry.Data.Width * 4);
                    bitmap.Freeze(); entry.Image = bitmap;
                }
            }
            finally { visualWorkers.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception e) { entry.Error = e.Message; }
        entry.Loading = false;
        if (!visualDisposed && !token.IsCancellationRequested && mediaVisuals.TryGetValue(asset.Id, out var current) && ReferenceEquals(current, entry))
        {
            // Never replace Thumb visuals while they own capture.
            if (drag is null) Rebuild();
        }
    }
    private void AddMediaVisual(Grid grid, Clip clip, double width)
    {
        var area = new Grid { Margin = new Thickness(3, 21, 3, 3), IsHitTestVisible = false, ClipToBounds = true };
        grid.Children.Insert(0, area);
        if (!mediaVisuals.TryGetValue(clip.MediaAssetId, out var entry))
        {
            area.Children.Add(new TextBlock { Text = "表示キャッシュの上限（128素材）", Foreground = Brushes.White, FontSize = 10 });
            return;
        }
        if (entry.Loading)
        {
            area.Children.Add(new TextBlock { Text = EditorText.VisualLoading, Foreground = Brushes.White, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
            return;
        }
        if (entry.Error is not null)
        {
            area.Children.Add(new TextBlock { Text = "⚠ " + EditorText.VisualFailed, Foreground = Brushes.White, FontSize = 10 });
            grid.ToolTip += "\n" + entry.Error;
        }
        else if (entry.Image is not null)
            area.Children.Add(new Image { Source = entry.Image, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
        else if (entry.Data is { } data && !data.Peaks.IsDefaultOrEmpty)
        {
            int columns = Math.Clamp((int)Math.Min(1024, Math.Ceiling(width)), 1, 1024);
            var peaks = WaveformProjection.Crop(data.Peaks, data.DurationTicks, clip.SourceInTicks, clip.DurationTicks, columns);
            var bars = new GeometryGroup();
            for (int i = 0; i < peaks.Length; i++)
            {
                double peak = peaks[i] * .48;
                bars.Children.Add(new RectangleGeometry(new Rect(i + .1, .5 - peak, .8, Math.Max(.003, 2 * peak))));
            }
            bars.Freeze();
            // Explicit unit viewbox prevents quiet sections from being normalized to full height.
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, columns, 1))));
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(182, 238, 204)), null, bars));
            group.Freeze();
            area.Children.Add(new Image { Source = new DrawingImage(group), Stretch = Stretch.Fill });
        }
    }
    private sealed class VisualEntry(string key)
    {
        public string Key { get; } = key;
        public bool Loading { get; set; } = true;
        public MediaVisualization? Data { get; set; }
        public BitmapSource? Image { get; set; }
        public string? Error { get; set; }
    }
}
