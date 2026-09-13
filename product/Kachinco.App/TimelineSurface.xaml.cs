using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kachinco.Core;
using Track = Kachinco.Core.Track;

namespace Kachinco.App;

public sealed class TimelineCommandEventArgs(EditCommand command) : EventArgs
{
    public EditCommand Command { get; } = command;
}

public sealed class TimelineSelectionEventArgs(Guid? clipId) : EventArgs
{
    public Guid? ClipId { get; } = clipId;
}

public sealed class TimelineDiagnosticsEventArgs(ImmutableArray<Diagnostic> diagnostics) : EventArgs
{
    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
}

public sealed class MediaPlacementEventArgs(Guid mediaId, Guid trackId, long startTicks) : EventArgs
{
    public Guid MediaId { get; } = mediaId;
    public Guid TrackId { get; } = trackId;
    public long StartTicks { get; } = startTicks;
}

public partial class TimelineSurface : UserControl
{
    public const string MediaDragFormat = "Kachinco.MediaAssetId";
    public event EventHandler<MediaPlacementEventArgs>? MediaPlacementRequested;
    public event EventHandler? PlayheadChanged;
    private const double TrackHeight = 46;
    private const double MinimumClipWidth = 8;
    private const int SnapThresholdPixels = 8;
    private readonly Brush videoBrush = new SolidColorBrush(Color.FromRgb(47, 112, 170));
    private readonly Brush audioBrush = new SolidColorBrush(Color.FromRgb(52, 139, 91));
    private Project? project;
    private Sequence? sequence;
    private TimelineViewport viewport = new(80m);
    private Guid? selectedClipId;
    private long playheadTicks;
    private DragState? drag;
    private Line? rulerPlayhead;
    private Line? canvasPlayhead;

    public TimelineSurface()
    {
        InitializeComponent();
        TimelineCanvas.AllowDrop = true;
        TimelineCanvas.DragOver += Media_DragOver;
        TimelineCanvas.Drop += Media_Drop;
        SizeChanged += (_, _) => Rebuild();
    }

    public event EventHandler<TimelineCommandEventArgs>? CommandRequested;
    public event EventHandler<TimelineSelectionEventArgs>? ClipSelectionChanged;
    public event EventHandler<TimelineDiagnosticsEventArgs>? InteractionFailed;

    public Guid? SelectedClipId => selectedClipId;
    public long PlayheadTicks => playheadTicks;
    public bool SnappingEnabled { get; private set; } = true;
    public decimal PixelsPerSecond => viewport.PixelsPerSecond;

    public void LoadProject(Project? value, Guid? sequenceId, Guid? selectedId = null)
    {
        if (sequence?.Id != sequenceId || project?.Id != value?.Id)
        {
            playheadTicks = 0;
            TimelineScroll.ScrollToHorizontalOffset(0);
        }
        project = value;
        sequence = value?.Sequences.FirstOrDefault(x => x.Id == sequenceId);
        selectedClipId = selectedId is { } id && sequence?.Tracks.Any(t => t.Clips.Any(c => c.Id == id)) == true ? id : null;
        if (sequence is null) playheadTicks = 0;
        else playheadTicks = Math.Clamp(playheadTicks, 0, sequence.DurationTicks);
        Rebuild();
    }

    public void ToggleSnapping()
    {
        SnappingEnabled = !SnappingEnabled;
        Rebuild();
    }

    public void ZoomBy(decimal factor)
    {
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        var value = Math.Clamp(viewport.PixelsPerSecond * factor,
            TimelineViewport.MinimumPixelsPerSecond, TimelineViewport.MaximumPixelsPerSecond);
        if (value == viewport.PixelsPerSecond) return;
        viewport = new(value);
        Rebuild();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var x = ToDouble(viewport.TicksToPixels(playheadTicks));
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - TimelineScroll.ViewportWidth / 2));
        });
    }

    public void FitSequence()
    {
        if (sequence is null) return;
        var available = Math.Max(1, TimelineScroll.ViewportWidth);
        var pps = (decimal)available * TimelineTime.TicksPerSecond / sequence.DurationTicks;
        viewport = new(Math.Clamp(pps, TimelineViewport.MinimumPixelsPerSecond, TimelineViewport.MaximumPixelsPerSecond));
        Rebuild();
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    private bool TryDrop(DragEventArgs e, out Guid mediaId, out Guid trackId, out long ticks)
    {
        mediaId = trackId = Guid.Empty; ticks = 0;
        if (project is null || sequence is null || e.Data.GetData(MediaDragFormat) is not Guid id) return false;
        var point = e.GetPosition(TimelineCanvas);
        var rows = sequence.Tracks.Reverse().ToArray();
        int row = (int)Math.Floor(point.Y / TrackHeight);
        if (row < 0 || row >= rows.Length || point.X < 0) return false;
        var asset = project.Assets.FirstOrDefault(x => x.Id == id);
        if (asset is null || rows[row].Kind != (asset.Kind == MediaKind.Mov ? TrackKind.Video : TrackKind.Audio)) return false;
        mediaId = id; trackId = rows[row].Id;
        ticks = Snap(TimelineSnapping.QuantizeToFrame(viewport.PixelsToTicks((decimal)point.X), sequence.Settings.FrameRate), Guid.Empty);
        return true;
    }
    private void Media_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryDrop(e, out _, out _, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Media_Drop(object sender, DragEventArgs e)
    {
        if (TryDrop(e, out var mediaId, out var trackId, out var ticks))
            MediaPlacementRequested?.Invoke(this, new(mediaId, trackId, ticks));
        e.Handled = true;
    }

    // The standard Windows Thumb template paints its own chrome even with a transparent background.
    private static Thumb GestureThumb()
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new Thumb { Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory } };
    }

    public void DeleteSelected()
    {
        if (sequence is not null && selectedClipId is { } clipId)
            CommandRequested?.Invoke(this, new(new DeleteClip(sequence.Id, clipId)));
    }

    public void SplitSelected()
    {
        if (project is null || sequence is null || selectedClipId is not { } clipId) return;
        var result = TimelineEditPlanner.Split(project, sequence.Id, clipId, playheadTicks, Guid.NewGuid());
        if (result.Success) CommandRequested?.Invoke(this, new(result.Value!));
        else InteractionFailed?.Invoke(this, new(result.Diagnostics));
    }

    private void Rebuild()
    {
        RulerCanvas.Children.Clear();
        TimelineCanvas.Children.Clear();
        TrackHeaders.Children.Clear();
        if (sequence is null)
        {
            RulerCanvas.Width = TimelineCanvas.Width = Math.Max(1, ActualWidth - 104);
            TimelineCanvas.Height = 1;
            return;
        }

        var visibleTracks = sequence.Tracks.Reverse().ToArray();
        double width = Math.Max(Math.Max(1, ActualWidth - 104), ToDouble(viewport.TicksToPixels(sequence.DurationTicks)));
        double height = Math.Max(1, visibleTracks.Length * TrackHeight);
        RulerCanvas.Width = TimelineCanvas.Width = width;
        TimelineCanvas.Height = height;

        DrawRuler(width, height);
        var labels = BuildTrackLabels(sequence);
        for (int row = 0; row < visibleTracks.Length; row++)
        {
            var track = visibleTracks[row];
            DrawTrack(track, labels[track.Id], row, width);
            foreach (var clip in TimelineQueries.ListClips(track)) DrawClip(track, clip, row);
            foreach (var caption in TimelineQueries.ListCaptions(track))
            {
                var block = new Border { Width = Math.Max(8, ToDouble(viewport.TicksToPixels(caption.DurationTicks))), Height = TrackHeight - 8,
                    Background = Brushes.DarkMagenta, IsHitTestVisible = false,
                    Child = new TextBlock { Text = caption.Text, Foreground = Brushes.White, Margin = new(5), TextTrimming = TextTrimming.CharacterEllipsis } };
                Canvas.SetLeft(block, ToDouble(viewport.TicksToPixels(caption.StartTicks))); Canvas.SetTop(block, row * TrackHeight + 4);
                TimelineCanvas.Children.Add(block);
            }
        }
        DrawPlayhead(height);
    }

    private void DrawRuler(double width, double timelineHeight)
    {
        long seconds = ChooseRulerSeconds();
        long step = checked(seconds * TimelineTime.TicksPerSecond);
        for (long ticks = 0; ticks <= sequence!.DurationTicks; ticks = Next(ticks, step))
        {
            double x = ToDouble(viewport.TicksToPixels(ticks));
            if (x > width) break;
            RulerCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 18, Y2 = 30, Stroke = Brushes.Gray, StrokeThickness = 1 });
            var label = new TextBlock
            {
                Text = FormatTime(ticks), Foreground = Brushes.LightGray, FontSize = 10,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, x + 3); Canvas.SetTop(label, 2); RulerCanvas.Children.Add(label);
            if (ticks == sequence.DurationTicks || step > sequence.DurationTicks - ticks) break;
        }
    }

    private void DrawTrack(Track track, string label, int row, double width)
    {
        var header = new Border
        {
            Height = TrackHeight,
            Background = row % 2 == 0 ? new SolidColorBrush(Color.FromRgb(45, 50, 59)) : new SolidColorBrush(Color.FromRgb(40, 45, 53)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(74, 81, 92)), BorderThickness = new(0, 0, 1, 1),
            Child = new StackPanel
            {
                Margin = new(10, 5, 4, 3),
                Children =
                {
                    new TextBlock { Text = label, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = track.Name, Foreground = Brushes.LightGray, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };
        TrackHeaders.Children.Add(header);
        var background = new Rectangle
        {
            Width = width, Height = TrackHeight,
            Fill = row % 2 == 0 ? new SolidColorBrush(Color.FromRgb(34, 39, 47)) : new SolidColorBrush(Color.FromRgb(31, 35, 42)),
            Stroke = new SolidColorBrush(Color.FromRgb(57, 63, 73)), StrokeThickness = 0.5,
            IsHitTestVisible = false
        };
        Canvas.SetTop(background, row * TrackHeight);
        TimelineCanvas.Children.Add(background);
    }

    private void DrawClip(Track track, Clip clip, int row)
    {
        double left = ToDouble(viewport.TicksToPixels(clip.StartTicks));
        double width = Math.Max(MinimumClipWidth, ToDouble(viewport.TicksToPixels(clip.DurationTicks)));
        var state = new ClipVisual(track.Id, clip, row, left, width);
        var grid = new Grid
        {
            Width = width, Height = TrackHeight - 8,
            Background = track.Kind == TrackKind.Video ? videoBrush : audioBrush,
            ToolTip = $"{AssetName(clip.MediaAssetId)}\n{FormatTime(clip.StartTicks)} — {FormatTime(clip.EndTicks)}",
            ClipToBounds = true
        };
        grid.Children.Add(new Border
        {
            BorderBrush = clip.Id == selectedClipId ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(145, 175, 202)),
            BorderThickness = clip.Id == selectedClipId ? new(2) : new(1),
            IsHitTestVisible = false
        });
        var body = GestureThumb(); body.Cursor = Cursors.SizeAll; body.Tag = state;
        body.DragStarted += Body_DragStarted; body.DragDelta += Body_DragDelta; body.DragCompleted += Body_DragCompleted;
        grid.Children.Add(body);
        grid.Children.Add(new TextBlock
        {
            Text = AssetName(clip.MediaAssetId), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
            Margin = new(10, 5, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        });
        grid.Children.Add(TrimThumb(state, HorizontalAlignment.Left, TrimEdge.Start));
        grid.Children.Add(TrimThumb(state, HorizontalAlignment.Right, TrimEdge.End));
        Canvas.SetLeft(grid, left); Canvas.SetTop(grid, row * TrackHeight + 4);
        TimelineCanvas.Children.Add(grid);
    }

    private Thumb TrimThumb(ClipVisual state, HorizontalAlignment alignment, TrimEdge edge)
    {
        var thumb = GestureThumb();
        thumb.Width = 7; thumb.HorizontalAlignment = alignment;
        thumb.Cursor = Cursors.SizeWE; thumb.Tag = new TrimVisual(state, edge);
        thumb.DragStarted += Trim_DragStarted;
        thumb.DragDelta += Trim_DragDelta;
        thumb.DragCompleted += Trim_DragCompleted;
        return thumb;
    }

    private void DrawPlayhead(double height)
    {
        double x = ToDouble(viewport.TicksToPixels(playheadTicks));
        var rulerLine = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 30, Stroke = Brushes.OrangeRed, StrokeThickness = 2, IsHitTestVisible = false };
        rulerPlayhead = rulerLine; RulerCanvas.Children.Add(rulerLine);
        var line = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = Brushes.OrangeRed, StrokeThickness = 1.5, IsHitTestVisible = false };
        canvasPlayhead = line; TimelineCanvas.Children.Add(line);
    }

    private void Body_DragStarted(object sender, DragStartedEventArgs e)
    {
        var thumb = (Thumb)sender;
        var state = (ClipVisual)thumb.Tag;
        Select(state.Clip.Id, rebuild: false);
        drag = new(state, thumb.Parent as FrameworkElement ?? thumb, null);
    }

    private void Body_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (drag is null) return;
        drag.DeltaX += e.HorizontalChange; drag.DeltaY += e.VerticalChange;
        Canvas.SetLeft(drag.Element, Math.Max(0, drag.Visual.Left + drag.DeltaX));
        Canvas.SetTop(drag.Element, Math.Clamp(drag.Visual.Row * TrackHeight + 4 + drag.DeltaY, 4,
            Math.Max(4, TimelineCanvas.Height - TrackHeight + 4)));
    }

    private void Body_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (drag is null || project is null || sequence is null) return;
        var current = drag; drag = null;
        if (e.Canceled || (current.DeltaX == 0 && current.DeltaY == 0)) { Rebuild(); return; }
        long delta = viewport.DeltaPixelsToTicks((decimal)current.DeltaX);
        long candidate;
        try { candidate = Math.Max(0, checked(current.Visual.Clip.StartTicks + delta)); }
        catch (OverflowException) { Fail("INVALID_TIMELINE_RANGE", "The clip move is outside the timeline.", current.Visual.Clip.Id); Rebuild(); return; }
        candidate = SnapMove(candidate, current.Visual.Clip);

        var rows = sequence.Tracks.Reverse().ToArray();
        int targetRow = Math.Clamp((int)Math.Round(current.Visual.Row + current.DeltaY / TrackHeight,
            MidpointRounding.AwayFromZero), 0, Math.Max(0, rows.Length - 1));
        Guid targetTrack = rows.Length == 0 ? current.Visual.TrackId : rows[targetRow].Id;
        var result = TimelineEditPlanner.Move(project, sequence.Id, current.Visual.Clip.Id, targetTrack, candidate);
        Dispatch(result);
    }

    private void Trim_DragStarted(object sender, DragStartedEventArgs e)
    {
        var thumb = (Thumb)sender;
        var value = (TrimVisual)thumb.Tag;
        Select(value.Visual.Clip.Id, rebuild: false);
        drag = new(value.Visual, thumb.Parent as FrameworkElement ?? thumb, value.Edge);
    }

    private void Trim_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (drag is null) return;
        drag.DeltaX += e.HorizontalChange;
        if (drag.Edge == TrimEdge.Start)
        {
            double delta = Math.Clamp(drag.DeltaX, -drag.Visual.Left, drag.Visual.Width - MinimumClipWidth);
            Canvas.SetLeft(drag.Element, drag.Visual.Left + delta);
            drag.Element.Width = drag.Visual.Width - delta;
        }
        else drag.Element.Width = Math.Max(MinimumClipWidth, drag.Visual.Width + drag.DeltaX);
    }

    private void Trim_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (drag is null || project is null || sequence is null || drag.Edge is not { } edge) return;
        var current = drag; drag = null;
        if (e.Canceled || (current.DeltaX == 0 && current.DeltaY == 0)) { Rebuild(); return; }
        long delta = viewport.DeltaPixelsToTicks((decimal)current.DeltaX);
        long candidate;
        try { candidate = checked((edge == TrimEdge.Start ? current.Visual.Clip.StartTicks : current.Visual.Clip.EndTicks) + delta); }
        catch (OverflowException) { Fail("INVALID_TRIM", "The trim edge is outside the media range.", current.Visual.Clip.Id); Rebuild(); return; }
        candidate = Math.Max(0, candidate);
        candidate = Snap(candidate, current.Visual.Clip.Id);
        Dispatch(TimelineEditPlanner.Trim(project, sequence.Id, current.Visual.Clip.Id, edge, candidate));
    }

    private void Dispatch<T>(Result<T> result) where T : EditCommand
    {
        if (result.Success) CommandRequested?.Invoke(this, new(result.Value!));
        else { InteractionFailed?.Invoke(this, new(result.Diagnostics)); Rebuild(); }
    }

    private long SnapMove(long candidateStart, Clip clip)
    {
        if (!SnappingEnabled || sequence is null) return candidateStart;
        var targets = TimelineEditPlanner.SnapTargets(sequence, clip.Id, playheadTicks);
        var start = TimelineSnapping.Snap(candidateStart, SnapThresholdPixels, viewport, targets);
        long endCandidate;
        try { endCandidate = checked(candidateStart + clip.DurationTicks); }
        catch (OverflowException) { return start.Ticks; }
        var end = TimelineSnapping.Snap(endCandidate, SnapThresholdPixels, viewport, targets);
        if (!end.Snapped) return start.Ticks;
        if (!start.Snapped) return Math.Max(0, end.Ticks - clip.DurationTicks);
        var startDistance = BigInteger.Abs((BigInteger)start.Ticks - candidateStart);
        var endDistance = BigInteger.Abs((BigInteger)end.Ticks - endCandidate);
        return endDistance < startDistance ? Math.Max(0, end.Ticks - clip.DurationTicks) : start.Ticks;
    }

    private long Snap(long candidate, Guid excludedClipId)
    {
        if (!SnappingEnabled || sequence is null) return candidate;
        return TimelineSnapping.Snap(candidate, SnapThresholdPixels, viewport,
            TimelineEditPlanner.SnapTargets(sequence, excludedClipId, playheadTicks)).Ticks;
    }

    private void Ruler_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetPlayhead(e.GetPosition(RulerCanvas).X);
    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == TimelineCanvas) { Select(null); SetPlayhead(e.GetPosition(TimelineCanvas).X); }
    }

    public void SetCursorTicks(long ticks)
    {
        if (sequence is null) return;
        long next = Math.Clamp(ticks, 0, sequence.DurationTicks);
        if (next == playheadTicks) return;
        playheadTicks = next;
        double pixel = ToDouble(viewport.TicksToPixels(next));
        if (rulerPlayhead is not null) rulerPlayhead.X1 = rulerPlayhead.X2 = pixel;
        if (canvasPlayhead is not null) canvasPlayhead.X1 = canvasPlayhead.X2 = pixel;
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetPlayhead(double pixel)
    {
        if (sequence is null) return;
        long value = viewport.PixelsToTicks((decimal)Math.Max(0, pixel));
        value = Math.Min(sequence.DurationTicks, TimelineSnapping.QuantizeToFrame(value, sequence.Settings.FrameRate));
        playheadTicks = value;
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
        Rebuild();
    }

    private void Select(Guid? clipId, bool rebuild = true)
    {
        if (selectedClipId == clipId) return;
        selectedClipId = clipId;
        ClipSelectionChanged?.Invoke(this, new(clipId));
        if (rebuild) Rebuild();
    }

    private void TimelineScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        RulerScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        HeaderScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private Dictionary<Guid, string> BuildTrackLabels(Sequence value)
    {
        var counts = new Dictionary<TrackKind, int>();
        var labels = new Dictionary<Guid, string>();
        foreach (var track in value.Tracks)
        {
            counts.TryGetValue(track.Kind, out int number); number++; counts[track.Kind] = number;
            labels[track.Id] = $"{(track.Kind == TrackKind.Video ? "V" : track.Kind == TrackKind.Audio ? "A" : "S")}{number}";
        }
        return labels;
    }

    private string AssetName(Guid id) => project?.Assets.FirstOrDefault(x => x.Id == id)?.Name ?? "Missing media";
    private long ChooseRulerSeconds()
    {
        long secondsForPixels = checked((long)decimal.Ceiling(72m / viewport.PixelsPerSecond));
        long totalSeconds = checked((long)(((BigInteger)sequence!.DurationTicks + TimelineTime.TicksPerSecond - 1) /
            TimelineTime.TicksPerSecond));
        long secondsForCount = Math.Max(1, (totalSeconds + 999) / 1000);
        long required = Math.Max(secondsForPixels, secondsForCount);
        long magnitude = 1;
        while (magnitude <= required / 10) magnitude *= 10;
        foreach (long multiplier in new long[] { 1, 2, 5, 10 })
            if (magnitude * multiplier >= required) return magnitude * multiplier;
        return required;
    }
    private static long Next(long value, long step) => value > long.MaxValue - step ? long.MaxValue : value + step;
    private static double ToDouble(decimal value) => decimal.ToDouble(value);
    private static string FormatTime(long ticks)
    {
        var total = (decimal)ticks / TimelineTime.TicksPerSecond;
        var span = TimeSpan.FromSeconds(decimal.ToDouble(total));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
    private void Fail(string code, string message, Guid id) =>
        InteractionFailed?.Invoke(this, new([Diagnostic.Error(code, message, id)]));

    private sealed record ClipVisual(Guid TrackId, Clip Clip, int Row, double Left, double Width);
    private sealed record TrimVisual(ClipVisual Visual, TrimEdge Edge);
    private sealed class DragState(ClipVisual visual, FrameworkElement element, TrimEdge? edge)
    {
        public ClipVisual Visual { get; } = visual;
        public FrameworkElement Element { get; } = element;
        public TrimEdge? Edge { get; } = edge;
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
    }
}
