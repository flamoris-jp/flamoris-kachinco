using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kachinco.Native;

/// <summary>One native editor handle. UTF-8 responses are copied out of immutable native leases.</summary>
public sealed class NativeEditorSession : IDisposable
{
    private readonly NativeEditorHandle handle;
    public NativeEditorSession(int historyLimit = 100)
    {
        if (historyLimit < 1) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        NativeMediaProcess.Check(NativeEditorMethods.Create(historyLimit, out handle));
    }
    public string Request(string json) => Call(handle, json);
    public static string CodecRequest(string json) => Call(null, json);
    private static string Call(NativeEditorHandle? handle, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] data = Encoding.UTF8.GetBytes(json);
        NativeStatus status = handle is null ? NativeEditorMethods.Codec(IntPtr.Zero, data, checked((uint)data.Length), out var buffer) :
            NativeEditorMethods.Request(handle, data, checked((uint)data.Length), out buffer);
        using (buffer)
        {
            if (status != NativeStatus.Ok) throw new NativeRuntimeException(status, NativeRuntime.StatusMessage(status));
            NativeMediaProcess.Check(NativeCacheMethods.Size(buffer, out uint size));
            byte[] response = new byte[checked((int)size)];
            NativeMediaProcess.Check(NativeCacheMethods.Copy(buffer, response, size));
            return Encoding.UTF8.GetString(response);
        }
    }
    public void Dispose() => handle.Dispose();
}
internal sealed class NativeEditorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeEditorHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeEditorMethods.Destroy(handle); return true; }
}
internal static class NativeEditorMethods
{
    private const string Library = "Kachinco.Native.Runtime";
    [DllImport(Library, EntryPoint = "kn_editor_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Create(int historyLimit, out NativeEditorHandle handle);
    [DllImport(Library, EntryPoint = "kn_editor_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "kn_editor_request", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Request(NativeEditorHandle handle, byte[] data, uint size, out NativeBufferHandle buffer);
    [DllImport(Library, EntryPoint = "kn_editor_request", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Codec(IntPtr handle, byte[] data, uint size, out NativeBufferHandle buffer);
}
