using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kachinco.Native;

[StructLayout(LayoutKind.Sequential)]
public struct NativeCacheStatistics
{
    public long Bytes, Entries, Hits, Misses, Evictions;
}

/// <summary>Native-owned immutable payload LRU; a get lease survives eviction/clear.</summary>
public sealed class NativeByteCache : IDisposable
{
    private readonly NativeCacheHandle handle;
    public NativeByteCache(long byteLimit, int entryLimit = 256)
    {
        if (byteLimit < 0 || entryLimit < 0) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        var status = NativeCacheMethods.Create(byteLimit, entryLimit, out handle);
        if (status != NativeStatus.Ok) { handle.Dispose(); NativeMediaProcess.Check(status); }
    }
    public NativeCacheStatistics Statistics
    {
        get
        {
            NativeMediaProcess.Check(NativeCacheMethods.Stats(handle, out var stats, (uint)Marshal.SizeOf<NativeCacheStatistics>()));
            return stats;
        }
    }
    public void Put(string key, byte[] data, long charge)
    {
        ValidateKey(key); ArgumentNullException.ThrowIfNull(data);
        if (charge < 0) throw new ArgumentOutOfRangeException(nameof(charge));
        NativeMediaProcess.Check(NativeCacheMethods.Put(handle, key, data, checked((uint)data.Length), charge));
    }
    public bool TryGet(string key, out byte[] data)
    {
        ValidateKey(key);
        var status = NativeCacheMethods.Get(handle, key, out var buffer);
        using (buffer)
        {
            NativeMediaProcess.Check(status);
            if (buffer.IsInvalid) { data = []; return false; }
            NativeMediaProcess.Check(NativeCacheMethods.Size(buffer, out uint size));
            data = new byte[checked((int)size)];
            NativeMediaProcess.Check(NativeCacheMethods.Copy(buffer, data, size));
            return true;
        }
    }
    public void Clear() => NativeMediaProcess.Check(NativeCacheMethods.Clear(handle));
    public void Dispose() => handle.Dispose();
    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(key) > 1024)
            throw new ArgumentException("Invalid cache key.", nameof(key));
    }
}
internal sealed class NativeCacheHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeCacheHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeCacheMethods.Destroy(handle); return true; }
}
internal sealed class NativeBufferHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeBufferHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeCacheMethods.ReleaseBuffer(handle); return true; }
}
internal static class NativeCacheMethods
{
    private const string Library = "Kachinco.Native.Runtime";
    [DllImport(Library, EntryPoint = "kn_cache_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Create(long bytes, int entries, out NativeCacheHandle handle);
    [DllImport(Library, EntryPoint = "kn_cache_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "kn_cache_put", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Put(NativeCacheHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, byte[] data, uint size, long charge);
    [DllImport(Library, EntryPoint = "kn_cache_get", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Get(NativeCacheHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out NativeBufferHandle buffer);
    [DllImport(Library, EntryPoint = "kn_cache_clear", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Clear(NativeCacheHandle handle);
    [DllImport(Library, EntryPoint = "kn_cache_stats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Stats(NativeCacheHandle handle, out NativeCacheStatistics stats, uint size);
    [DllImport(Library, EntryPoint = "kn_buffer_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ReleaseBuffer(IntPtr handle);
    [DllImport(Library, EntryPoint = "kn_buffer_size", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Size(NativeBufferHandle handle, out uint size);
    [DllImport(Library, EntryPoint = "kn_buffer_copy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Copy(NativeBufferHandle handle, [Out] byte[] data, uint size);
}
