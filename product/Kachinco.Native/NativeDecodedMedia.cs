using System.Runtime.InteropServices;

namespace Kachinco.Native;

public static class NativeDecodedMedia
{
    public static byte[] Rgba(byte[] bytes, int width, int height)
    {
        var status = NativeDecodedMethods.Rgba(bytes, checked((uint)bytes.Length), width, height, out var buffer);
        return Copy(status, buffer);
    }
    public static float[] Pcm(byte[] bytes, int sampleCount, int channels)
    {
        var status = NativeDecodedMethods.Pcm(bytes, checked((uint)bytes.Length), sampleCount, channels, out var buffer);
        var payload = Copy(status, buffer);
        var samples = new float[payload.Length / 4];
        for (int i = 0; i < samples.Length; ++i)
            samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(i * 4));
        return samples;
    }
    private static byte[] Copy(NativeStatus status, NativeBufferHandle buffer)
    {
        using (buffer)
        {
            if (status == NativeStatus.EndOfStream) throw new EndOfStreamException("Source has no video frame at the requested time.");
            if (status == NativeStatus.InvalidMedia) throw new InvalidDataException("Decoder returned incomplete RGBA or partial/non-finite PCM.");
            NativeMediaProcess.Check(status);
            NativeMediaProcess.Check(NativeCacheMethods.Size(buffer, out uint size));
            var bytes = new byte[checked((int)size)];
            NativeMediaProcess.Check(NativeCacheMethods.Copy(buffer, bytes, size));
            return bytes;
        }
    }
}
internal static class NativeDecodedMethods
{
    private const string Library = "Kachinco.Native.Runtime";
    [DllImport(Library, EntryPoint = "kn_decode_rgba", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Rgba(byte[] data, uint size, int width, int height, out NativeBufferHandle buffer);
    [DllImport(Library, EntryPoint = "kn_decode_pcm", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Pcm(byte[] data, uint size, int sampleCount, int channels, out NativeBufferHandle buffer);
}
