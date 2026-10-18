using System.Numerics;

namespace Kachinco.Core;

// Transient pointer planning only. UpdateCaption and native validation/history
// remain the sole editing authority; every delta uses the original snapshot.
public static class CaptionTimingEdits
{
    public static UpdateCaption Plan(Sequence sequence, Caption caption, long deltaTicks,
        TrimEdge? edge, TimelineViewport viewport, bool snapping, long playheadTicks)
    {
        long start = caption.StartTicks, end = checked(caption.StartTicks + caption.DurationTicks);
        if (deltaTicks == 0) return new(sequence.Id, caption.Id, start, caption.DurationTicks, caption.Text, caption.Enabled);
        long minimum = Math.Min(caption.DurationTicks, TimelineTime.FrameToTicks(1, sequence.Settings.FrameRate));
        long lower = edge == TrimEdge.End ? start + minimum : 0;
        long upper = edge switch
        {
            TrimEdge.Start => end - minimum,
            TrimEdge.End => sequence.DurationTicks,
            _ => sequence.DurationTicks - caption.DurationTicks
        };
        long origin = edge == TrimEdge.End ? end : start;
        long candidate = (long)BigInteger.Clamp((BigInteger)origin + deltaTicks, lower, upper);
        candidate = Math.Clamp(TimelineSnapping.QuantizeToFrame(candidate, sequence.Settings.FrameRate), lower, upper);
        if (snapping)
        {
            var targets = TimelineEditPlanner.SnapTargets(sequence, null, playheadTicks)
                .Concat(sequence.Tracks.SelectMany(t => t.Captions).Where(c => c.Id != caption.Id)
                    .SelectMany(c => new[] { c.StartTicks, checked(c.StartTicks + c.DurationTicks) })).Append(sequence.DurationTicks);
            var snapped = TimelineSnapping.Snap(candidate, 8, viewport, targets.Where(t => t >= lower && t <= upper));
            if (edge is null)
            {
                // Also snap the trailing boundary without changing the duration.
                var trailing = TimelineSnapping.Snap(candidate + caption.DurationTicks, 8, viewport,
                    targets.Where(t => t >= caption.DurationTicks && t <= sequence.DurationTicks));
                if (trailing.Snapped && (!snapped.Snapped ||
                    BigInteger.Abs((BigInteger)trailing.Ticks - candidate - caption.DurationTicks) <
                    BigInteger.Abs((BigInteger)snapped.Ticks - candidate)))
                    candidate = trailing.Ticks - caption.DurationTicks;
                else candidate = snapped.Ticks;
            }
            else candidate = snapped.Ticks;
        }
        if (edge == TrimEdge.Start) start = candidate;
        else if (edge == TrimEdge.End) end = candidate;
        else { start = candidate; end = start + caption.DurationTicks; }
        return new(sequence.Id, caption.Id, start, end - start, caption.Text, caption.Enabled);
    }
}
