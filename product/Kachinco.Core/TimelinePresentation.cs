namespace Kachinco.Core;

// Presentation-only DIPs. Never persisted. Both WPF columns consume the same row.
public readonly record struct TimelineRow(double Top, double Height, double ClipInset)
{
    public double Bottom => Top + Height;
    public double ClipTop => Top + ClipInset;
    public double ClipHeight => Height - 2 * ClipInset;
    public bool Contains(double y) => y >= Top && y < Bottom;
}

public sealed class TimelineTrackGeometry
{
    public const double HeaderWidth = 104;
    public const double RulerHeight = 30;
    public const double DefaultRowHeight = 72;
    public const double ClipInset = 4;
    public int Count { get; }
    public double RowHeight { get; }
    public double Height => Count * RowHeight;

    public TimelineTrackGeometry(int count, double rowHeight = DefaultRowHeight)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (!double.IsFinite(rowHeight) || rowHeight < 32) throw new ArgumentOutOfRangeException(nameof(rowHeight));
        Count = count; RowHeight = rowHeight;
    }
    public TimelineRow Row(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new(index * RowHeight, RowHeight, ClipInset);
    }
    public int HitRow(double y) => !double.IsFinite(y) || y < 0 || y >= Height ? -1 : (int)(y / RowHeight);
}

// Content is time-zero-relative; view is lane-host-relative; surface includes headers.
public readonly record struct TimelineCoordinates(TimelineViewport Viewport, decimal HorizontalOffset)
{
    public decimal ContentX(long ticks) => Viewport.TicksToPixels(ticks);
    public decimal ViewX(long ticks) => ContentX(ticks) - HorizontalOffset;
    public decimal SurfaceX(long ticks) => (decimal)TimelineTrackGeometry.HeaderWidth + ViewX(ticks);
    public long ViewXToTicks(decimal x) => Viewport.PixelsToTicks(Math.Max(0, x + HorizontalOffset));
    public long SurfaceXToTicks(decimal x) => ViewXToTicks(x - (decimal)TimelineTrackGeometry.HeaderWidth);
    public long PlacementTicks(decimal viewX, FrameRate fps, bool snapping, IEnumerable<long> targets) =>
        snapping ? TimelineSnapping.Snap(TimelineSnapping.QuantizeToFrame(ViewXToTicks(viewX), fps), 8, Viewport, targets).Ticks :
            TimelineSnapping.QuantizeToFrame(ViewXToTicks(viewX), fps);
}

public enum EditorGuidance { ImportMedia, CreateSequence, PlaceMedia, Edit }
public static class EditorStartup
{
    public static EditorGuidance Guidance(Project? project, Sequence? sequence) =>
        project is null || project.Assets.IsEmpty ? EditorGuidance.ImportMedia :
        sequence is null ? EditorGuidance.CreateSequence :
        sequence.Tracks.All(t => t.Clips.IsEmpty && t.Captions.IsEmpty) ? EditorGuidance.PlaceMedia : EditorGuidance.Edit;

    public static EditBatch Import(ProjectSnapshot snapshot, MediaAsset asset, Guid newProjectId, string projectName) =>
        new(snapshot.Project is null ? [new CreateProject(newProjectId, projectName), new RegisterMedia(asset)] :
            [new RegisterMedia(asset)], snapshot.Revision);
}
