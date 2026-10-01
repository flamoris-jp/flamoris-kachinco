using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Kachinco.Core;

namespace Kachinco.App;

public partial class TimelineSurface
{
    private Line? rippleMarker;

    private void Request(EditBatch batch)
    {
        CommandRequested?.Invoke(this, new(batch));
        Rebuild();
    }

    private void DispatchBatch(Result<EditBatch> result)
    {
        if (result.Success) Request(result.Value!);
        else { InteractionFailed?.Invoke(this, new(result.Diagnostics)); Rebuild(); }
    }

    private void AttachClipMenu(Grid grid, Guid clipId)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem());
        grid.ContextMenu = menu;
        grid.ContextMenuOpening += (_, e) =>
        {
            e.Handled = !PrepareClipMenu(menu, clipId);
        };
    }

    // Prepare before the popup owns focus. Keyboard opening uses the same path.
    private bool PrepareClipMenu(ContextMenu menu, Guid clipId)
    {
            if (drag is not null || project is null || sequence is null || !IsEnabled)
                return false;
            Select(clipId, rebuild: false);
            menu.Items.Clear();
            var split = TimelineEditPlanner.Split(project, sequence.Id, clipId, playheadTicks, Guid.NewGuid());
            var splitBatch = new EditBatch(split.Success ? [split.Value!] : [], revision);
            Add("分割", "Split", split.Success, () => Request(splitBatch));
            var deletion = new EditBatch([new DeleteClip(sequence.Id, clipId)], revision);
            Add("削除", "Delete", true, () => Request(deletion));
            var duplicate = TimelineEditPlanner.Duplicate(project, sequence.Id, clipId, Guid.NewGuid(), revision);
            Add("複製", "Duplicate", duplicate.Success, () => DispatchBatch(duplicate));
            var earlier = TimelineEditPlanner.ReorderAdjacent(project, sequence.Id, clipId, true, revision);
            var later = TimelineEditPlanner.ReorderAdjacent(project, sequence.Id, clipId, false, revision);
            Add("前へ移動", "Move earlier", earlier.Success, () => Request(earlier.Value!.Batch));
            Add("後ろへ移動", "Move later", later.Success, () => Request(later.Value!.Batch));

            void Add(string ja, string en, bool enabled, Action action)
            {
                var item = new MenuItem { Header = EditorText.Choose(ja, en), IsEnabled = enabled };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            return true;
    }

    private Guid DragTargetTrack(DragState current)
    {
        var rows = TimelineLanes.Create(sequence!);
        var origin = geometry.Row(current.Visual.Row);
        int row = geometry.HitRow(Math.Clamp(origin.Top + origin.Height / 2 + current.DeltaY, 0, geometry.Height - .01));
        return rows[row].TrackId ?? Guid.Empty;
    }

    private Result<MoveClip> PlanFreeMove(DragState current)
    {
        try
        {
            long delta = viewport.DeltaPixelsToTicks((decimal)current.DeltaX);
            long start = Math.Max(0, checked(current.Visual.Clip.StartTicks + delta));
            return TimelineEditPlanner.Move(project!, sequence!.Id, current.Visual.Clip.Id,
                DragTargetTrack(current), SnapMove(start, current.Visual.Clip));
        }
        catch (OverflowException)
        { return Result<MoveClip>.Fail(Diagnostic.Error("INVALID_TIMELINE_RANGE", "The clip move is outside the timeline.", current.Visual.Clip.Id)); }
    }

    private void UpdateRipplePreview(long? pointerTicks = null)
    {
        if (drag is not { } current || project is null || sequence is null) return;
        var track = sequence.Tracks.First(t => t.Id == current.Visual.TrackId);
        foreach (var grid in TimelineCanvas.Children.OfType<Grid>())
            if (grid != current.Element && grid.Tag is Guid id && track.Clips.FirstOrDefault(c => c.Id == id) is { } clip)
                Canvas.SetLeft(grid, ToDouble(viewport.TicksToPixels(clip.StartTicks)));
        ClearRippleMarker();
        bool sameTrack = DragTargetTrack(current) == track.Id;
        long ticks = pointerTicks ?? Coordinates.ViewXToTicks((decimal)Mouse.GetPosition(TimelineViewportHost).X);
        var insertion = sameTrack ? TimelineEditPlanner.InsertionTarget(track, current.Visual.Clip.Id,
            Snap(ticks, current.Visual.Clip.Id)) : null;
        if (insertion?.Success == true)
        {
            // Native projects only when the insertion target changes, never per pixel.
            if (!current.HasRippleTarget || current.RippleTarget != insertion.Value)
                current.Ripple = TimelineEditPlanner.Reorder(project, sequence.Id, current.Visual.Clip.Id, insertion.Value, revision);
            current.HasRippleTarget = true;
            current.RippleTarget = insertion.Value;
        }
        else
        {
            current.HasRippleTarget = false;
            current.Ripple = null;
        }
        bool valid = current.Ripple is { } ripple ? ripple.Success || ripple.Diagnostics.Any(d => d.Code == "REORDER_UNCHANGED")
            : PlanFreeMove(current).Success;
        current.Element.Opacity = valid ? 1 : .45;
        current.Thumb.Cursor = valid ? Cursors.SizeAll : Cursors.No;
        if (current.Ripple is not { Success: true, Value: { } plan }) return;
        foreach (var grid in TimelineCanvas.Children.OfType<Grid>())
            if (grid.Tag is Guid id && plan.Starts.TryGetValue(id, out long start))
                Canvas.SetLeft(grid, ToDouble(viewport.TicksToPixels(start)));
        double x = ToDouble(viewport.TicksToPixels(plan.InsertionTicks));
        var row = geometry.Row(current.Visual.Row);
        rippleMarker = new Line { X1 = x, X2 = x, Y1 = row.Top, Y2 = row.Bottom,
            Stroke = Brushes.Gold, StrokeThickness = 3, IsHitTestVisible = false };
        Panel.SetZIndex(rippleMarker, 5);
        TimelineCanvas.Children.Add(rippleMarker);
    }

    private void ClearRippleMarker()
    {
        if (rippleMarker is not null) TimelineCanvas.Children.Remove(rippleMarker);
        rippleMarker = null;
    }
}
