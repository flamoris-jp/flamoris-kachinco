using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kachinco.Core;

namespace Kachinco.App;

public partial class TimelineSurface
{
    private CaptionDrag? captionDrag;

    private void DrawCaption(Caption caption, int row)
    {
        double width = ToDouble(viewport.TicksToPixels(caption.DurationTicks));
        var grid = new Grid { Width = width, Height = geometry.Row(row).ClipHeight,
            Background = Brushes.DarkMagenta, Tag = caption.Id, ClipToBounds = true,
            Opacity = sequence!.Tracks.First(t => t.Captions.Any(c => c.Id == caption.Id)).Enabled ? 1 : .45,
            ToolTip = CaptionTooltip(caption.Text, caption.StartTicks, caption.DurationTicks) };
        grid.Children.Add(new Border { BorderBrush = caption.Id == selectedClipId ? Brushes.White : Brushes.Transparent,
            BorderThickness = new(2), IsHitTestVisible = false });
        AddHandle(null, HorizontalAlignment.Stretch);
        grid.Children.Add(new TextBlock { Text = caption.Text, Foreground = Brushes.White, Margin = new(9, 2, 9, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });
        AddHandle(TrimEdge.Start, HorizontalAlignment.Left);
        AddHandle(TrimEdge.End, HorizontalAlignment.Right);
        Canvas.SetLeft(grid, ToDouble(viewport.TicksToPixels(caption.StartTicks)));
        Canvas.SetTop(grid, geometry.Row(row).ClipTop);
        Panel.SetZIndex(grid, 1); TimelineCanvas.Children.Add(grid);

        void AddHandle(TrimEdge? edge, HorizontalAlignment alignment)
        {
            var thumb = GestureThumb(); thumb.HorizontalAlignment = alignment;
            thumb.Cursor = edge is null ? Cursors.SizeAll : Cursors.SizeWE;
            if (edge is not null)
            {
                thumb.Width = Math.Min(10, width / 3);
                thumb.ForceCursor = true;
                Panel.SetZIndex(thumb, 2);
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));
                thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
            }
            thumb.ToolTip = edge switch
            {
                TrimEdge.Start => EditorText.Choose("字幕の開始を調整", "Adjust caption start"),
                TrimEdge.End => EditorText.Choose("字幕の終了を調整", "Adjust caption end"),
                _ => EditorText.Choose("字幕を左右に移動", "Move caption horizontally")
            };
            thumb.DragStarted += (_, _) =>
            {
                if (sequence is null || captionDrag is not null || drag is not null) { thumb.CancelDrag(); return; }
                followSuspended = true;
                Select(caption.Id, rebuild: false);
                captionDrag = new(sequence, caption, revision, grid, thumb, edge);
                Panel.SetZIndex(grid, 2); Focus();
            };
            thumb.DragDelta += (_, e) =>
            {
                if (captionDrag is not { } current) return;
                current.DeltaPixels += e.HorizontalChange;
                PreviewCaptionDrag();
            };
            thumb.DragCompleted += (_, e) => CompleteCaptionDrag(e.Canceled);
            grid.Children.Add(thumb);
        }
    }

    private static string CaptionTooltip(string text, long start, long duration) =>
        $"{text}\n{(decimal)start / TimelineTime.TicksPerSecond:0.000}s ? {(decimal)(start + duration) / TimelineTime.TicksPerSecond:0.000}s";

    private void PreviewCaptionDrag()
    {
        if (captionDrag is not { } current) return;
        try
        {
            long delta = viewport.DeltaPixelsToTicks((decimal)current.DeltaPixels);
            current.Pending = CaptionTimingEdits.Plan(current.Sequence, current.Caption, delta, current.Edge,
                viewport, SnappingEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt), playheadTicks);
            Canvas.SetLeft(current.Element, ToDouble(viewport.TicksToPixels(current.Pending.StartTicks)));
            current.Element.Width = ToDouble(viewport.TicksToPixels(current.Pending.DurationTicks));
            current.Element.ToolTip = CaptionTooltip(current.Caption.Text, current.Pending.StartTicks, current.Pending.DurationTicks);
        }
        catch (OverflowException) { current.Pending = null; }
    }

    private void CompleteCaptionDrag(bool canceled)
    {
        if (captionDrag is not { } current) return;
        captionDrag = null;
        if (!canceled && current.Pending is { } update &&
            (update.StartTicks != current.Caption.StartTicks || update.DurationTicks != current.Caption.DurationTicks))
            Request(new([update], current.Revision));
        Rebuild();
    }

    private void CancelCaptionDrag()
    {
        if (captionDrag is not { } current) return;
        current.Thumb.CancelDrag();
        // Capture may already have been released by a refresh/teardown.
        if (captionDrag is not null) CompleteCaptionDrag(true);
    }

    private sealed class CaptionDrag(Sequence sequence, Caption caption, long? revision, Grid element, Thumb thumb, TrimEdge? edge)
    {
        public Sequence Sequence { get; } = sequence;
        public Caption Caption { get; } = caption;
        public long? Revision { get; } = revision;
        public Grid Element { get; } = element;
        public Thumb Thumb { get; } = thumb;
        public TrimEdge? Edge { get; } = edge;
        public double DeltaPixels { get; set; }
        public UpdateCaption? Pending { get; set; }
    }
}
