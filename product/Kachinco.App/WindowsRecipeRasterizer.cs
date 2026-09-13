using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public sealed class WindowsRecipeRasterizer(Dispatcher dispatcher) : IRecipeRasterizer
{
    public async ValueTask<ImmutableArray<byte>> RenderAsync(RecipeIr ir, Recipe recipe, Clapper clapper, SequenceSettings settings, long localTicks, CancellationToken token)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var visual = new DrawingVisual();
            double time = (double)localTicks / TimelineTime.TicksPerSecond;
            var region = clapper.Geometry;
            double originX = region?.X ?? 0, originY = region?.Y ?? 0;
            using (var drawing = visual.RenderOpen())
            {
                if (region?.Kind == ClapperGeometryKind.Rectangle) drawing.PushClip(new RectangleGeometry(new Rect(region.X, region.Y, region.Width, region.Height)));
                uint state = unchecked((uint)recipe.Seed) | 1u;
                double Random() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state / 4294967296d; }
                foreach (var op in ir.Operations)
                {
                    double x = originX + op.X + op.Vx * time, y = originY + op.Y + op.Vy * time;
                    if (op.Kind == "text")
                    {
                        var text = new FormattedText(op.Text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                            new Typeface("Yu Gothic"), op.Size, Brushes.White, 1d);
                        drawing.DrawGeometry(Brushes.White, new Pen(Brushes.Black, op.Size / 12), text.BuildGeometry(new Point(x,y)));
                    }
                    else
                    {
                        for (int i = 0; i < op.Count; i++)
                        {
                            double spread = (Random() - .5) * 200, rise = Random() * 100, radius = op.Size * (.5 + Random() * .5);
                            drawing.DrawEllipse(Brushes.Gold, null, new Point(x + spread * time, y - rise * time), radius, radius);
                        }
                    }
                }
                if (region?.Kind == ClapperGeometryKind.Rectangle) drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(settings.Width, settings.Height, 96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
            var bgra = new byte[checked(settings.Width * settings.Height * 4)]; bitmap.CopyPixels(bgra, settings.Width * 4, 0);
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int a = bgra[i+3]; rgba[i+3] = (byte)a; if (a == 0) continue;
                rgba[i] = (byte)Math.Min(255,(bgra[i+2]*255+a/2)/a); rgba[i+1] = (byte)Math.Min(255,(bgra[i+1]*255+a/2)/a); rgba[i+2] = (byte)Math.Min(255,(bgra[i]*255+a/2)/a);
            }
            return ImmutableArray.CreateRange(rgba);
        }, DispatcherPriority.Background, token);
    }
}
