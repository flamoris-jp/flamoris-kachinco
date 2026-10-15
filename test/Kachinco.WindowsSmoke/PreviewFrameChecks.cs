using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Mcp.Core;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class PreviewFrameChecks
{
    public static async Task Run(MainWindow main)
    {
        const long T = TimelineTime.TicksPerSecond;
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-frame-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "manga.png");
            byte[] pixels = new byte[1080 * 1920 * 4];
            for (int y = 0; y < 1920; y++) for (int x = 0; x < 1080; x++)
            {
                int i = (y * 1080 + x) * 4; pixels[i] = (byte)(x < 540 ? 255 : 30); pixels[i + 1] = (byte)(y < 960 ? 30 : 255); pixels[i + 3] = 255;
            }
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1080, 1920, 96, 96, PixelFormats.Bgra32, null, pixels, 1080 * 4)));
            using (var file = File.Create(path)) png.Save(file);
            using var session = new EditorSession();
            var seq = Guid.NewGuid(); var media = Guid.NewGuid(); var lower = Guid.NewGuid(); var upper = Guid.NewGuid(); var sub = Guid.NewGuid(); var clip = Guid.NewGuid();
            Check(session.Execute(new([
                new CreateProject(Guid.NewGuid(), "Preview capture proof"), new CreateSequence(seq, "Portrait", SequenceSettings.Portrait, 6 * T),
                new RegisterMedia(new(media, "Manga", path, MediaKind.Image, 6*T)),
                new AddTrack(seq, lower, "Background", TrackKind.Video), new AddTrack(seq, upper, "Animated", TrackKind.Video), new AddTrack(seq, sub, "Caption", TrackKind.Subtitle),
                new InsertClip(seq, lower, new(Guid.NewGuid(), media, 0, 0, 6*T, true, ClipAppearance.Default, AudioProperties.Default)),
                new InsertClip(seq, upper, new(clip, media, 0, 0, 5*T, true, ClipAppearance.Default with { Opacity = .5, Blend = BlendMode.Screen }, AudioProperties.Default)),
                new SetClipPropertyCurve(seq, clip, new(VisualProperty.X, [new(Guid.NewGuid(), 0, 0), new(Guid.NewGuid(), 5*T, 400)])),
                new SetClipPropertyCurve(seq, clip, new(VisualProperty.ScaleX, [new(Guid.NewGuid(), 0, 1), new(Guid.NewGuid(), 5*T, .5)])),
                new AddCaption(seq, sub, new(Guid.NewGuid(), 0, 5*T, "FLAMORIS frame"))
            ])).Success, "Create representative portrait project");
            var rasterizer = new WindowsCaptionRasterizer(main.Dispatcher);
            var encoder = new ComparingEncoder();
            var capture = new PreviewFrameCapture(encoder, rasterizer);
            using var host = new KachincoMcpHost(session, () => false, (action, token) => main.Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Background, token).Task);
            using var boundary = new McpBoundary(host, [capture.CreateTool(session, () => null, () => host.Snapshot)], new(), new(KachincoLogging.Create().Logger));
            using var grant = await boundary.EnableAsync(McpPermission.ReadOnly);
            using var source = new InteractivePreviewSource(rasterizer, backendPreference: PreviewBackendPreference.Cpu);
            var context = PreviewContext.Create(session.GetProject(), seq).Value!;
            var before = session.GetProject();
            foreach (long tick in new[] { 0L, T * 5 / 2, T * 49 / 10, 5 * T })
            {
                var preview = await source.FrameAsync(context, tick, PreviewQuality.Full, false, default);
                Check(preview.Success, "Viewer frame"); encoder.Expected = preview.Value!;
                var result = await boundary.InvokeAsync(grant, "preview_frame", JsonSerializer.SerializeToElement(new { sequenceId = seq, timeTicks = tick.ToString(), output = new { format = "png", width = 540 } }), null);
                Check(!result.IsError && result.Image?.MimeType == "image/png", "Capture PNG: " + result.Error);
                using var stream = new MemoryStream(result.Image!.Bytes.ToArray());
                var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Check(decoded.PixelWidth == 540 && decoded.PixelHeight == 960, "PNG dimensions");
                Check(session.GetProject() == before, "Read-only history/revision");
            }
            var jpeg = await new WindowsPreviewImageEncoder().EncodeAsync(encoder.Expected!, 540, 960, "jpeg", 85, default);
            using var jpg = new MemoryStream(jpeg);
            Check(BitmapDecoder.Create(jpg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0].PixelHeight == 960, "JPEG codec");
            Console.WriteLine("Preview frame: portrait still, overlap/screen/opacity, animated position/scale, caption, boundary, full viewer parity, PNG/JPEG dimensions and read-only history: PASS");
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class ComparingEncoder : IPreviewImageEncoder
    {
        public RenderedVideoFrame? Expected { get; set; }
        public Task<byte[]> EncodeAsync(RenderedVideoFrame frame, int width, int height, string format, int quality, CancellationToken token)
        {
            Check(Expected!.Rgba8.SequenceEqual(frame.Rgba8), "Full-resolution viewer and capture pixels differ");
            Check(Expected.Tick == frame.Tick, "Resolved time differs");
            return new WindowsPreviewImageEncoder().EncodeAsync(frame, width, height, format, quality, token);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
