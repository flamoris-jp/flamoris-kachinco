namespace Kachinco.Core;

// Presentation-only page policy; time and playback remain in their existing authorities.
public static class TimelineFollow
{
    public static double NextOffset(double cursor, double width, double offset, double maximum)
    {
        if (!double.IsFinite(cursor) || !double.IsFinite(width) || !double.IsFinite(offset) ||
            !double.IsFinite(maximum) || width <= 0 || maximum <= 0) return offset;
        if (cursor >= offset + width * .85)
            return Math.Clamp(cursor - width * .20, 0, maximum);
        if (cursor < offset)
            return Math.Clamp(cursor - width * .20, 0, maximum);
        return offset;
    }
}
