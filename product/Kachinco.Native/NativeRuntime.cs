using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kachinco.Native;

public enum NativeStatus : int
{
    Ok = 0, InvalidArgument = 1, AbiMismatch = 2, Overflow = 3, OutOfMemory = 4, InternalError = 5
}

public sealed class NativeRuntimeException(NativeStatus status, string message) : Exception(message)
{
    public NativeStatus Status { get; } = status;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeRuntimeInfo
{
    public uint AbiVersion;
    public uint Reserved;
    public ulong Capabilities;
    public long TicksPerSecond;
}

/// <summary>Blittable transport sample only; never a second persistent media model.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeMediaValue
{
    public long DurationTicks;
    public long SourceInTicks;
    public int Width;
    public int Height;
    public int FpsNumerator;
    public int FpsDenominator;
}

/// <summary>Owns a native ABI context, not an editor session. Time methods are parity candidates.</summary>
public sealed class NativeRuntime : IDisposable
{
    public const uint AbiVersion = 1;
    public const ulong RequiredCapabilities = 3;
    private readonly NativeRuntimeHandle handle;

    private NativeRuntime(NativeRuntimeHandle handle) => this.handle = handle;

    public static NativeRuntime Create()
    {
        try
        {
            if (NativeMethods.AbiVersion() != AbiVersion)
                throw new NativeRuntimeException(NativeStatus.AbiMismatch, "NATIVE_ABI_MISMATCH");
            var status = NativeMethods.Create(AbiVersion, out var handle);
            if (status != NativeStatus.Ok) { handle.Dispose(); Check(status); }
            var runtime = new NativeRuntime(handle);
            try
            {
                var info = runtime.GetInfo();
                if (info.AbiVersion != AbiVersion || (info.Capabilities & RequiredCapabilities) != RequiredCapabilities ||
                    info.TicksPerSecond != 35_280_000)
                    throw new NativeRuntimeException(NativeStatus.AbiMismatch, "NATIVE_ABI_MISMATCH");
                return runtime;
            }
            catch { runtime.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("Kachinco native runtime is missing or incompatible. Use the complete win-x64 portable package, or rebuild with CMake and the x64 C++ build tools.", ex);
        }
    }

    public NativeRuntimeInfo GetInfo()
    {
        Check(NativeMethods.GetInfo(handle, out var info, (uint)Marshal.SizeOf<NativeRuntimeInfo>()));
        return info;
    }
    public NativeMediaValue RoundTrip(NativeMediaValue value)
    {
        uint size = (uint)Marshal.SizeOf<NativeMediaValue>();
        Check(NativeMethods.RoundTrip(handle, in value, size, out var output, size));
        return output;
    }
    public long FrameToTicks(long index, int numerator, int denominator)
    {
        Check(NativeMethods.FrameToTicks(handle, index, numerator, denominator, out long output));
        return output;
    }
    public long FrameCount(long duration, int numerator, int denominator)
    {
        Check(NativeMethods.FrameCount(handle, duration, numerator, denominator, out long output));
        return output;
    }
    public long SampleToTicks(long index, int rate)
    {
        Check(NativeMethods.SampleToTicks(handle, index, rate, out long output));
        return output;
    }
    public long SampleCount(long duration, int rate)
    {
        Check(NativeMethods.SampleCount(handle, duration, rate, out long output));
        return output;
    }
    public static string StatusMessage(NativeStatus status) =>
        Marshal.PtrToStringUTF8(NativeMethods.StatusMessage(status)) ?? "NATIVE_UNKNOWN_STATUS";
    private static void Check(NativeStatus status)
    {
        if (status != NativeStatus.Ok) throw new NativeRuntimeException(status, StatusMessage(status));
    }
    public void Dispose() => handle.Dispose();
}

internal sealed class NativeRuntimeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeRuntimeHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeMethods.Destroy(handle); return true; }
}

internal static class NativeMethods
{
    private const string Library = "Kachinco.Native.Runtime";
    [DllImport(Library, EntryPoint = "kn_abi_version", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    internal static extern uint AbiVersion();
    [DllImport(Library, EntryPoint = "kn_status_message", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr StatusMessage(NativeStatus status);
    [DllImport(Library, EntryPoint = "kn_runtime_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Create(uint abi, out NativeRuntimeHandle handle);
    [DllImport(Library, EntryPoint = "kn_runtime_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "kn_runtime_get_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus GetInfo(NativeRuntimeHandle handle, out NativeRuntimeInfo output, uint size);
    [DllImport(Library, EntryPoint = "kn_value_roundtrip", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus RoundTrip(NativeRuntimeHandle handle, in NativeMediaValue input, uint inputSize, out NativeMediaValue output, uint outputSize);
    [DllImport(Library, EntryPoint = "kn_frame_to_ticks", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus FrameToTicks(NativeRuntimeHandle handle, long index, int numerator, int denominator, out long output);
    [DllImport(Library, EntryPoint = "kn_frame_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus FrameCount(NativeRuntimeHandle handle, long duration, int numerator, int denominator, out long output);
    [DllImport(Library, EntryPoint = "kn_sample_to_ticks", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus SampleToTicks(NativeRuntimeHandle handle, long index, int rate, out long output);
    [DllImport(Library, EntryPoint = "kn_sample_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus SampleCount(NativeRuntimeHandle handle, long duration, int rate, out long output);
}
