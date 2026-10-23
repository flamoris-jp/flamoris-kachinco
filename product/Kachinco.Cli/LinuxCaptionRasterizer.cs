using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Cli;

// Only a text-to-RGBA adapter. Native evaluation/composition remain authoritative.
public sealed class LinuxCaptionRasterizer : ICaptionRasterizer
{
    public ValueTask<ImmutableArray<byte>> RasterizeAsync(ImmutableArray<EvaluatedCaption> captions,
        int width, int height, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Headless captions require Linux Pango/Cairo.");
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8_294_400)
            throw new InvalidDataException("Caption canvas exceeds the headless limit.");
        token.ThrowIfCancellationRequested();
        var surface = N.cairo_image_surface_create(0, width, height); // native-endian premultiplied ARGB32
        var cr = N.cairo_create(surface);
        try
        {
            if (N.cairo_surface_status(surface) != 0 || N.cairo_status(cr) != 0) throw new IOException("Cairo allocation failed.");
            double scale = Math.Min(width, height) / 1080d, bottom = height * .9;
            foreach (var caption in captions.Reverse())
            {
                token.ThrowIfCancellationRequested();
                var s = caption.Style ?? CaptionStyle.Default;
                var layout = N.pango_cairo_create_layout(cr);
                var font = N.pango_font_description_new();
                try
                {
                    N.pango_font_description_set_family(font, s.FontFamily == "Yu Gothic" ? "Noto Sans CJK JP" : s.FontFamily);
                    N.pango_font_description_set_absolute_size(font, s.FontSize * scale * 1024);
                    N.pango_layout_set_font_description(layout, font);
                    N.pango_layout_set_text(layout, caption.Text, -1); // plain text, never markup
                    N.pango_layout_set_width(layout, checked((int)Math.Round(width * s.MaxWidth * 1024)));
                    N.pango_layout_set_wrap(layout, 2); // WORD_CHAR
                    N.pango_layout_set_alignment(layout, s.Alignment == "Left" ? 0 : s.Alignment == "Center" ? 1 : 2);
                    if (N.pango_layout_get_unknown_glyphs_count(layout) != 0)
                        throw new InvalidDataException("CAPTION_GLYPH_MISSING: install fonts covering the caption text.");
                    N.pango_layout_get_pixel_size(layout, out _, out int textHeight);
                    double left = width * s.X - width * s.MaxWidth * (s.Alignment == "Left" ? 0 : s.Alignment == "Center" ? .5 : 1);
                    double anchor = caption.Style is null ? bottom : height * s.Y;
                    double top = anchor - textHeight * (s.VerticalAnchor == "Top" ? 0 : s.VerticalAnchor == "Center" ? .5 : 1);
                    if (s.ShadowEnabled) Draw(left + s.ShadowX * scale, top + s.ShadowY * scale, s.ShadowColor, false);
                    Draw(left, top, s.Color, true);
                    if (caption.Style is null) bottom -= textHeight + s.FontSize * scale * .4;

                    void Draw(double x, double y, string color, bool outline)
                    {
                        N.cairo_new_path(cr); N.cairo_move_to(cr, x, y); N.pango_cairo_layout_path(cr, layout);
                        if (outline && s.OutlineWidth > 0)
                        {
                            SetColor(cr, s.OutlineColor); N.cairo_set_line_width(cr, s.OutlineWidth * scale);
                            N.cairo_set_line_join(cr, 1); N.cairo_stroke_preserve(cr);
                        }
                        SetColor(cr, color); N.cairo_fill(cr);
                    }
                }
                finally { N.pango_font_description_free(font); N.g_object_unref(layout); }
            }
            if (N.cairo_status(cr) != 0) throw new IOException("Cairo text rendering failed.");
            N.cairo_surface_flush(surface);
            int stride = N.cairo_image_surface_get_stride(surface);
            var bgra = new byte[checked(stride * height)]; Marshal.Copy(N.cairo_image_surface_get_data(surface), bgra, 0, bgra.Length);
            var rgba = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int i = y * stride + x * 4, o = (y * width + x) * 4, a = bgra[i + 3];
                    rgba[o + 3] = (byte)a;
                    if (a == 0) continue;
                    rgba[o] = (byte)Math.Min(255, (bgra[i + 2] * 255 + a / 2) / a);
                    rgba[o + 1] = (byte)Math.Min(255, (bgra[i + 1] * 255 + a / 2) / a);
                    rgba[o + 2] = (byte)Math.Min(255, (bgra[i] * 255 + a / 2) / a);
                }
            }
            return ValueTask.FromResult(ImmutableArray.CreateRange(rgba));
        }
        finally { N.cairo_destroy(cr); N.cairo_surface_destroy(surface); }
    }
    private static void SetColor(IntPtr cr, string hex)
    {
        uint v = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        N.cairo_set_source_rgba(cr, (v >> 16 & 255) / 255d, (v >> 8 & 255) / 255d, (v & 255) / 255d,
            hex.Length == 9 ? (v >> 24) / 255d : 1);
    }
    private static class N
    {
        private const string C = "libcairo.so.2", P = "libpango-1.0.so.0", PC = "libpangocairo-1.0.so.0";
        [DllImport(C)] internal static extern IntPtr cairo_image_surface_create(int format, int width, int height);
        [DllImport(C)] internal static extern IntPtr cairo_create(IntPtr surface);
        [DllImport(C)] internal static extern int cairo_status(IntPtr cr);
        [DllImport(C)] internal static extern int cairo_surface_status(IntPtr surface);
        [DllImport(C)] internal static extern void cairo_destroy(IntPtr cr);
        [DllImport(C)] internal static extern void cairo_surface_destroy(IntPtr surface);
        [DllImport(C)] internal static extern void cairo_surface_flush(IntPtr surface);
        [DllImport(C)] internal static extern IntPtr cairo_image_surface_get_data(IntPtr surface);
        [DllImport(C)] internal static extern int cairo_image_surface_get_stride(IntPtr surface);
        [DllImport(C)] internal static extern void cairo_move_to(IntPtr cr, double x, double y);
        [DllImport(C)] internal static extern void cairo_new_path(IntPtr cr);
        [DllImport(C)] internal static extern void cairo_set_source_rgba(IntPtr cr, double r, double g, double b, double a);
        [DllImport(C)] internal static extern void cairo_set_line_width(IntPtr cr, double width);
        [DllImport(C)] internal static extern void cairo_set_line_join(IntPtr cr, int join);
        [DllImport(C)] internal static extern void cairo_stroke_preserve(IntPtr cr);
        [DllImport(C)] internal static extern void cairo_fill(IntPtr cr);
        [DllImport(PC)] internal static extern IntPtr pango_cairo_create_layout(IntPtr cr);
        [DllImport(PC)] internal static extern void pango_cairo_layout_path(IntPtr cr, IntPtr layout);
        [DllImport(P)] internal static extern IntPtr pango_font_description_new();
        [DllImport(P)] internal static extern void pango_font_description_set_family(IntPtr font, [MarshalAs(UnmanagedType.LPUTF8Str)] string family);
        [DllImport(P)] internal static extern void pango_font_description_set_absolute_size(IntPtr font, double size);
        [DllImport(P)] internal static extern void pango_font_description_free(IntPtr font);
        [DllImport(P)] internal static extern void pango_layout_set_font_description(IntPtr layout, IntPtr font);
        [DllImport(P)] internal static extern void pango_layout_set_text(IntPtr layout, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length);
        [DllImport(P)] internal static extern void pango_layout_set_width(IntPtr layout, int width);
        [DllImport(P)] internal static extern void pango_layout_set_wrap(IntPtr layout, int wrap);
        [DllImport(P)] internal static extern void pango_layout_set_alignment(IntPtr layout, int alignment);
        [DllImport(P)] internal static extern int pango_layout_get_unknown_glyphs_count(IntPtr layout);
        [DllImport(P)] internal static extern void pango_layout_get_pixel_size(IntPtr layout, out int width, out int height);
        [DllImport("libgobject-2.0.so.0")] internal static extern void g_object_unref(IntPtr obj);
    }
}
