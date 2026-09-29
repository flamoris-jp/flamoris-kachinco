namespace Kachinco.Core;

// Compatibility entry point; native is the shared straight-alpha encoded SDR authority.
public readonly record struct Rgba(double R, double G, double B, double A);
public static class BlendReference
{
    public static Rgba Composite(Rgba backdrop, Rgba source, BlendMode mode, double opacity = 1)
    {
        var value = Kachinco.Native.NativeComposition.Blend(new(backdrop.R, backdrop.G, backdrop.B, backdrop.A),
            new(source.R, source.G, source.B, source.A), (int)mode, opacity);
        return new(value.R, value.G, value.B, value.A);
    }
}
