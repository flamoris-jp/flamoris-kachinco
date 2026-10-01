using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Kachinco.Native;

namespace Kachinco.Infrastructure;

// Owned immutable BGRA32 pixels; never mutate renderer/cache RGBA or export pixels.
public sealed record PreviewPresentation(RenderedVideoFrame Frame, ImmutableArray<byte> Bgra8)
{
    public static Task<PreviewPresentation> PrepareAsync(RenderedVideoFrame frame, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Rgba8.Length != checked(frame.Width * frame.Height * 4))
            throw new InvalidDataException("Invalid preview frame dimensions.");
        var pixels = new byte[frame.Rgba8.Length];
        NativeComposition.RgbaToBgra(frame.Rgba8.AsSpan(), pixels);
        token.ThrowIfCancellationRequested();
        return new PreviewPresentation(frame, ImmutableCollectionsMarshal.AsImmutableArray(pixels));
    }, token);
}
