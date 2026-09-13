namespace Kachinco.Core;

// Straight-alpha normalized encoded SDR RGB. Output is straight alpha too.
public readonly record struct Rgba(double R, double G, double B, double A);

public static class BlendReference
{
    public static Rgba Composite(Rgba backdrop, Rgba source, BlendMode mode, double opacity = 1)
    {
        if (!Valid(backdrop) || !Valid(source) || !double.IsFinite(opacity) || opacity is < 0 or > 1 || !Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(source));
        double a = source.A * opacity, b = backdrop.A;
        double outputAlpha = a + b * (1 - a);
        if (outputAlpha == 0) return new(0, 0, 0, 0);
        double Channel(double cb, double cs)
        {
            double blend = mode == BlendMode.Screen ? 1 - (1 - cb) * (1 - cs) : cs;
            return ((1 - a) * b * cb + a * (1 - b) * cs + a * b * blend) / outputAlpha;
        }
        return new(Channel(backdrop.R, source.R), Channel(backdrop.G, source.G), Channel(backdrop.B, source.B), outputAlpha);
    }
    private static bool Valid(Rgba c) => Unit(c.R) && Unit(c.G) && Unit(c.B) && Unit(c.A);
    private static bool Unit(double x) => double.IsFinite(x) && x is >= 0 and <= 1;
}
