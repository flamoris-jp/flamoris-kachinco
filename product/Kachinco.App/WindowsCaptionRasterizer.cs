using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

// A shared raster input, never a screenshot of the editor's viewport.
public sealed class WindowsCaptionRasterizer(Dispatcher dispatcher) : ICaptionRasterizer
{
    public async ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                double fontSize = Math.Min(width, height) / 24d;
                double bottom = height * .90;
                foreach (var caption in captions.Reverse())
                {
                    var text = new FormattedText(caption.Text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                        new Typeface("Yu Gothic"), fontSize, Brushes.White, 1d)
                    { MaxTextWidth = width * .84, TextAlignment = TextAlignment.Center };
                    var origin = new Point(width * .08, bottom - text.Height);
                    var geometry = text.BuildGeometry(origin);
                    drawing.DrawGeometry(Brushes.White, new Pen(Brushes.Black, fontSize / 12), geometry);
                    bottom -= text.Height + fontSize * .4;
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var bgra = new byte[checked(width * height * 4)]; bitmap.CopyPixels(bgra, width * 4, 0);
            var rgba = new byte[bgra.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int alpha = bgra[i + 3]; rgba[i + 3] = (byte)alpha;
                if (alpha == 0) continue;
                rgba[i] = (byte)Math.Min(255, (bgra[i + 2] * 255 + alpha / 2) / alpha);
                rgba[i + 1] = (byte)Math.Min(255, (bgra[i + 1] * 255 + alpha / 2) / alpha);
                rgba[i + 2] = (byte)Math.Min(255, (bgra[i] * 255 + alpha / 2) / alpha);
            }
            return ImmutableArray.CreateRange(rgba);
        }, DispatcherPriority.Background, token);
    }
}
