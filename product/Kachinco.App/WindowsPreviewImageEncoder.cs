using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flamoris.Mcp.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public sealed class WindowsPreviewImageEncoder(int maximumBytes = McpImage.MaximumBytes) : IPreviewImageEncoder
{
    public async Task<byte[]> EncodeAsync(RenderedVideoFrame frame, int width, int height, string format, int quality, CancellationToken token)
    {
        var presentation = await PreviewPresentation.PrepareAsync(frame, token);
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            BitmapSource bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
                null, presentation.Bgra8.ToArray(), frame.Width * 4);
            bitmap.Freeze();
            if (width != frame.Width || height != frame.Height)
            {
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform((double)width / frame.Width, (double)height / frame.Height));
                bitmap.Freeze();
            }
            BitmapEncoder encoder = format == "png" ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new BoundedImageStream(maximumBytes);
            try { encoder.Save(stream); }
            catch (Exception) when (stream.Exceeded) { throw new PreviewImageTooLargeException(); }
            if (stream.Exceeded) throw new PreviewImageTooLargeException();
            token.ThrowIfCancellationRequested();
            return stream.ToArray();
        }, token);
    }
    private sealed class BoundedImageStream(int limit) : MemoryStream
    {
        public bool Exceeded { get; private set; }
        private void Check(long end) { if (end > limit) { Exceeded = true; throw new PreviewImageTooLargeException(); } }
        public override long Position { get => base.Position; set { Check(value); base.Position = value; } }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long end = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset);
            Check(end); return base.Seek(offset, origin);
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(checked(Position + buffer.Length)); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(checked(Position + 1)); base.WriteByte(value); }
    }
}
