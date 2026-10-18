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
    private readonly HashSet<string> installedFonts = Fonts.SystemFontFamilies.Select(f=>f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    public async ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions, int width, int height, CancellationToken token)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                double scale = Math.Min(width, height) / 1080d;
                double bottom = height * .90;
                foreach (var caption in captions.Reverse())
                {
                    token.ThrowIfCancellationRequested();
                    var style = caption.Style ?? CaptionStyle.Default;
                    var font = installedFonts.Contains(style.FontFamily) ? style.FontFamily : "Yu Gothic";
                    double fontSize = style.FontSize * scale, textWidth = width * style.MaxWidth;
                    var text = new FormattedText(caption.Text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                        new Typeface(font), fontSize, Brush(style.Color), 1d)
                    { MaxTextWidth = textWidth, TextAlignment = Enum.Parse<TextAlignment>(style.Alignment) };
                    double left = width * style.X - (style.Alignment == "Left" ? 0 : style.Alignment == "Right" ? textWidth : textWidth / 2);
                    double anchor = caption.Style is null ? bottom : height * style.Y;
                    double top = anchor - (style.VerticalAnchor == "Top" ? 0 : style.VerticalAnchor == "Center" ? text.Height / 2 : text.Height);
                    var origin = new Point(left,top);
                    var geometry = text.BuildGeometry(origin);
                    if(style.ShadowEnabled)
                    {
                        drawing.PushTransform(new TranslateTransform(style.ShadowX * scale,style.ShadowY * scale));
                        drawing.DrawGeometry(Brush(style.ShadowColor),null,geometry); drawing.Pop();
                    }
                    drawing.DrawGeometry(Brush(style.Color), style.OutlineWidth == 0 ? null : new Pen(Brush(style.OutlineColor), style.OutlineWidth * scale), geometry);
                    if(caption.Style is null) bottom -= text.Height + fontSize * .4;
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
