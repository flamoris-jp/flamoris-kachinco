using System.Runtime.InteropServices;

namespace Kachinco.Native;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeAppearance(double X, double Y, double ScaleX, double ScaleY, double Rotation, double Opacity, int Blend, int Reserved = 0);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeRgba(double R, double G, double B, double A);

public static class NativeComposition
{
    public static NativeRgba Blend(NativeRgba backdrop, NativeRgba source, int mode, double opacity)
    {
        var status = Methods.Blend(backdrop, source, mode, opacity, out var result);
        if (status == NativeStatus.InvalidArgument) throw new ArgumentOutOfRangeException(nameof(source));
        NativeMediaProcess.Check(status); return result;
    }
    public static unsafe void Composite(Span<byte> output, ReadOnlySpan<byte> source, int width, int height,
        NativeAppearance appearance, CancellationToken token)
    {
        if (width <= 0 || height <= 0 || output.Length != checked(width * height * 4) || source.Length != output.Length)
            throw new InvalidDataException("Invalid RGBA buffer dimensions.");
        fixed (byte* dst = output, src = source)
            for (int y = 0; y < height; y += 16)
            {
                token.ThrowIfCancellationRequested();
                NativeMediaProcess.Check(Methods.Composite((IntPtr)dst, (IntPtr)src, (uint)output.Length,
                    width, height, in appearance, y, Math.Min(16, height - y)));
            }
    }
    public static unsafe void RgbaToBgra(ReadOnlySpan<byte> source, Span<byte> output)
    {
        if (source.Length != output.Length) throw new InvalidDataException("Invalid presentation buffer size.");
        fixed (byte* src = source, dst = output)
            NativeMediaProcess.Check(Methods.RgbaToBgra((IntPtr)dst, (IntPtr)src, checked((uint)source.Length)));
    }
    public static unsafe void Mix(Span<double> mix, ReadOnlySpan<float> source, int offset, double gain)
    {
        fixed (double* dst = mix)
        fixed (float* src = source)
            NativeMediaProcess.Check(Methods.Mix((IntPtr)dst, (uint)mix.Length, (IntPtr)src, (uint)source.Length, checked((uint)offset), gain));
    }
    public static unsafe float[] Finish(ReadOnlySpan<double> mix)
    {
        var output = new float[mix.Length];
        fixed (double* src = mix)
        fixed (float* dst = output)
            NativeMediaProcess.Check(Methods.Finish((IntPtr)src, (IntPtr)dst, (uint)mix.Length));
        return output;
    }
    private static class Methods
    {
        private const string Library = "Kachinco.Native.Runtime";
        [DllImport(Library, EntryPoint = "kn_rgba_to_bgra", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus RgbaToBgra(IntPtr output, IntPtr source, uint size);
        [DllImport(Library, EntryPoint = "kn_blend", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Blend(NativeRgba backdrop, NativeRgba source, int mode, double opacity, out NativeRgba output);
        [DllImport(Library, EntryPoint = "kn_composite_rows", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Composite(IntPtr output, IntPtr source, uint size, int width, int height, in NativeAppearance appearance, int first, int count);
        [DllImport(Library, EntryPoint = "kn_mix_add", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Mix(IntPtr mix, uint size, IntPtr source, uint count, uint offset, double gain);
        [DllImport(Library, EntryPoint = "kn_mix_finish", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Finish(IntPtr mix, IntPtr output, uint count);
    }
}
