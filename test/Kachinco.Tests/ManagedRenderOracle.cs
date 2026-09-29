using System.Collections.Immutable;
using Kachinco.Core;
namespace Kachinco.Tests;

// Frozen pre-#34 implementation, test oracle only. Never referenced by Product.
internal static class ManagedRenderOracle
{
    public static void Composite(byte[] output, ImmutableArray<byte> source, int width, int height, ClipAppearance appearance, CancellationToken token = default)
    {
        if (output.Length != checked(width * height * 4) || source.Length != output.Length) throw new InvalidDataException("Invalid RGBA buffer dimensions.");
        var t = appearance.Transform;
        if (t == Transform2D.Identity && appearance.Opacity == 1 && appearance.Blend == BlendMode.Normal)
        {
            bool opaque = true;
            for (int i = 3; i < source.Length; i += 4)
            {
                if ((i & 65535) == 3) token.ThrowIfCancellationRequested();
                if (source[i] != 255) { opaque = false; break; }
            }
            if (opaque) { source.AsSpan().CopyTo(output); return; }
            for (int i = 0; i < source.Length; i += 4)
            {
                if (i % (width * 4) == 0) token.ThrowIfCancellationRequested();
                if (source[i + 3] == 0) continue;
                if (source[i + 3] == 255) { source.AsSpan(i, 4).CopyTo(output.AsSpan(i, 4)); continue; }
                var mixed = Blend(new(output[i]/255d,output[i+1]/255d,output[i+2]/255d,output[i+3]/255d),
                    new(source[i]/255d,source[i+1]/255d,source[i+2]/255d,source[i+3]/255d), appearance.Blend, 1);
                output[i]=Byte(mixed.R); output[i+1]=Byte(mixed.G); output[i+2]=Byte(mixed.B); output[i+3]=Byte(mixed.A);
            }
            return;
        }
        double radians = t.RotationDegrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 - t.X, py = y + 0.5 - t.Y;
                double sx = (cos * px + sin * py) / t.ScaleX, sy = (-sin * px + cos * py) / t.ScaleY;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                int src = ((int)sy * width + (int)sx) * 4, dst = (y * width + x) * 4;
                if (source[src + 3] == 0) continue;
                var mixed = Blend(new(output[dst] / 255d, output[dst + 1] / 255d, output[dst + 2] / 255d, output[dst + 3] / 255d),
                    new(source[src] / 255d, source[src + 1] / 255d, source[src + 2] / 255d, source[src + 3] / 255d), appearance.Blend, appearance.Opacity);
                output[dst] = Byte(mixed.R); output[dst + 1] = Byte(mixed.G); output[dst + 2] = Byte(mixed.B); output[dst + 3] = Byte(mixed.A);
            }
        }
    }
    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
    public static Rgba Blend(Rgba backdrop, Rgba source, BlendMode mode, double opacity = 1)
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
