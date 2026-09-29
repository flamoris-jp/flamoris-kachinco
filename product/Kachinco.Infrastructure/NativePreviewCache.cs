using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Kachinco.Native;

namespace Kachinco.Infrastructure;

// Cache keys still come from PreviewContext; the native LRU owns cached pixel/PCM bytes.
public sealed class NativePreviewCache<T>(long byteLimit, Func<T, byte[]> encode, Func<byte[], T> decode, int entryLimit = 256) : IDisposable
{
    private readonly NativeByteCache cache = new(byteLimit, entryLimit);
    public CacheStatistics Statistics
    {
        get { var s = cache.Statistics; return new(s.Bytes, checked((int)s.Entries), s.Hits, s.Misses, s.Evictions); }
    }
    public bool TryGet(string key, out T value)
    {
        if (!cache.TryGet(key, out var data)) { value = default!; return false; }
        value = decode(data); return true;
    }
    public void Put(string key, T value, long size) => cache.Put(key, encode(value), size);
    public void Clear() => cache.Clear();
    public void Dispose() => cache.Dispose();
}

internal static class NativePreviewPayload
{
    internal static byte[] Encode(RenderedVideoFrame frame)
    {
        var bytes = new byte[checked(24 + frame.Rgba8.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, frame.FrameIndex);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), frame.Tick);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), frame.Height);
        frame.Rgba8.AsSpan().CopyTo(bytes.AsSpan(24)); return bytes;
    }
    internal static RenderedVideoFrame Video(byte[] bytes) => new(
        BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)),
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20)),
        ImmutableArray.CreateRange(bytes.AsSpan(24).ToArray()));
    internal static byte[] Encode(RenderedAudioBlock block)
    {
        var bytes = new byte[checked(16 + block.Samples.Length * 4)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, block.FirstSample);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), block.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), block.Channels);
        for (int i = 0; i < block.Samples.Length; ++i)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + i * 4), block.Samples[i]);
        return bytes;
    }
    internal static RenderedAudioBlock Audio(byte[] bytes)
    {
        var samples = new float[(bytes.Length - 16) / 4];
        for (int i = 0; i < samples.Length; ++i) samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16 + i * 4));
        return new(BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)), ImmutableCollectionsMarshal.AsImmutableArray(samples));
    }
}
