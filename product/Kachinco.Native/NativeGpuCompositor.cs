using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kachinco.Native;

/// <summary>Optional D3D11 preview composition. Owns no timeline, media, or effect authoring state.</summary>
public sealed class NativeGpuCompositor : IDisposable
{
    private readonly object gate = new();
    private readonly GpuHandle handle;
    private int frameBytes;
    private bool disposed;

    private NativeGpuCompositor(GpuHandle handle) => this.handle = handle;

    public static bool TryCreate(out NativeGpuCompositor? compositor, out string reason,
        ulong budgetBytes = 268_435_456, bool forceWarp = false)
    {
        compositor = null;
        try
        {
            int status = Methods.Create(budgetBytes, forceWarp ? 1 : 0, out var handle, out int error);
            if (status != 0) { handle.Dispose(); reason = Describe("create", status, error); return false; }
            compositor = new(handle); reason = "D3D11 preview composition available."; return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            reason = $"D3D11 preview native adapter unavailable: {ex.Message}"; return false;
        }
    }

    /// <summary>Charged texture/constant-buffer payload; excludes driver/device/shader bookkeeping.</summary>
    public ulong AllocatedBytes
    {
        get { lock (gate) { ThrowIfDisposed(); return Methods.AllocatedBytes(handle); } }
    }

    public void Begin(int width, int height)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            frameBytes = 0;
            int status = Methods.Begin(handle, width, height, out int error);
            Check("begin", status, error);
            frameBytes = checked(width * height * 4);
        }
    }

    public unsafe void Composite(ReadOnlySpan<byte> source, NativeAppearance appearance)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (frameBytes == 0 || source.Length != frameBytes)
                throw new InvalidDataException("D3D11 composition requires a canvas-sized RGBA buffer after Begin.");
            fixed (byte* data = source)
            {
                int status = Methods.Composite(handle, (IntPtr)data, checked((uint)source.Length), in appearance, out int error);
                Check("composite", status, error);
            }
        }
    }

    /// <summary>One immutable-output readback after all frame layers have been submitted.</summary>
    public unsafe byte[] Read()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (frameBytes == 0) throw new InvalidOperationException("D3D11 preview requires Begin before Read.");
            var output = new byte[frameBytes];
            fixed (byte* data = output)
            {
                int status = Methods.Read(handle, (IntPtr)data, checked((uint)output.Length), out int error);
                Check("read", status, error);
            }
            return output;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            ThrowIfDisposed(); frameBytes = 0;
            int status = Methods.Reset(handle, out int error);
            Check("reset", status, error);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; frameBytes = 0; handle.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static void Check(string operation, int status, int error)
    {
        if (status != 0) throw new InvalidOperationException(Describe(operation, status, error));
    }
    private static string Describe(string operation, int status, int error)
    {
        string detail = status switch
        {
            1 => "invalid dimensions, appearance, buffer, or budget",
            4 => "allocation failed",
            8 => "GPU completion timed out; the context requires disposal",
            11 => "D3D11 shader-model-5 double precision/division or RGBA8 integer texture support unavailable on this host",
            12 => "D3D11 device, shader, resource, or readback failure",
            13 => "canvas exceeds the configured GPU payload budget",
            _ => $"native status {status}"
        };
        string nativeDetail = Marshal.PtrToStringUTF8(Methods.Diagnostic()) ?? "";
        return $"D3D11 preview {operation}: {detail}" + (error != 0 ? $" (HRESULT 0x{unchecked((uint)error):X8})." : ".") +
            (nativeDetail.Length != 0 ? $" {nativeDetail.Trim()}" : "");
    }

    private sealed class GpuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public GpuHandle() : base(true) { }
        protected override bool ReleaseHandle() { Methods.Destroy(handle); return true; }
    }
    private static class Methods
    {
        private const string Library = "Kachinco.Native.Runtime";
        [DllImport(Library, EntryPoint = "kn_gpu_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Create(ulong budgetBytes, int forceWarp, out GpuHandle handle, out int error);
        [DllImport(Library, EntryPoint = "kn_gpu_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);
        [DllImport(Library, EntryPoint = "kn_gpu_begin", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Begin(GpuHandle handle, int width, int height, out int error);
        [DllImport(Library, EntryPoint = "kn_gpu_composite", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Composite(GpuHandle handle, IntPtr source, uint size, in NativeAppearance appearance, out int error);
        [DllImport(Library, EntryPoint = "kn_gpu_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Read(GpuHandle handle, IntPtr output, uint size, out int error);
        [DllImport(Library, EntryPoint = "kn_gpu_reset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Reset(GpuHandle handle, out int error);
        [DllImport(Library, EntryPoint = "kn_gpu_allocated_bytes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong AllocatedBytes(GpuHandle handle);
        [DllImport(Library, EntryPoint = "kn_gpu_diagnostic", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Diagnostic();
    }
}
