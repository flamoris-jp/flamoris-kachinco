using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Kachinco.Native;

/// <summary>Native document authority; managed values are immutable wire projections.</summary>
public sealed class NativeKineticDocument : IDisposable
{
    private readonly TextDocumentHandle handle;
    public NativeKineticDocument() {
        using var runtime = NativeRuntime.Create();
        if ((runtime.GetInfo().Capabilities & 131072) == 0) throw new InvalidOperationException("Matching KineticTypo native runtime required.");
        NativeMediaProcess.Check(TextMethods.Create(out handle));
    }
    public JsonObject Request(JsonObject request)
    {
        var data = Encoding.UTF8.GetBytes(request.ToJsonString());
        NativeMediaProcess.Check(TextMethods.Request(handle, data, checked((uint)data.Length), out var buffer));
        using (buffer) return JsonNode.Parse(TextMethods.Read(buffer))!.AsObject();
    }
    public JsonObject Get() => Request(new() { ["action"] = "get" });
    public JsonObject Edit(JsonObject document, long expectedRevision, bool replace = false) =>
        Request(new() { ["action"] = replace ? "replace" : "edit", ["expectedRevision"] = expectedRevision, ["document"] = document.DeepClone() });
    public void Dispose() => handle.Dispose();
}

public sealed record KineticPixels(byte[] BgraPremultiplied, byte[] Rgba, byte[] Rgb, byte[] Mask);

/// <summary>Hardware DirectWrite/Direct2D/D3D11 text, never WPF glyph rasterization.
/// Requests are serialized; the native device and bounded layout cache are reused.</summary>
public sealed class NativeKineticRenderer : IDisposable
{
    private readonly object gate = new();
    private TextRendererHandle handle;
    private readonly bool warp;
    private int deviceResets;
    public NativeKineticRenderer(bool forceWarp = false)
    {
        warp = forceWarp;
        int status = TextMethods.RendererCreate(forceWarp ? 1 : 0, out handle, out int error);
        if (status != 0) { handle.Dispose(); throw new InvalidOperationException($"Native text GPU unavailable ({status}, HRESULT 0x{error:X8})."); }
    }
    public KineticPixels Render(string document, long tick, int width, int height)
    {
        lock (gate)
        {
            if (handle.IsClosed) throw new ObjectDisposedException(nameof(NativeKineticRenderer));
            if (width < 1 || height < 1 || width > 4096 || height > 4096) throw new ArgumentOutOfRangeException(nameof(width));
            int count = checked(width * height);
            byte[] data = Encoding.UTF8.GetBytes(document), bgra = new byte[count * 4];
            int status = TextMethods.Render(handle, data, checked((uint)data.Length), tick, width, height, bgra, (uint)bgra.Length, out int error);
            if (status == 12 && unchecked((uint)error) is 0x8899000C or 0x887A0005 or 0x887A0006 or 0x887A0007) {
                handle.Dispose();
                int reset = TextMethods.RendererCreate(warp ? 1 : 0, out handle, out int resetError);
                if (reset != 0) throw new InvalidOperationException($"Native text GPU reset failed ({reset}, HRESULT 0x{resetError:X8}).");
                ++deviceResets;
                status = TextMethods.Render(handle, data, checked((uint)data.Length), tick, width, height, bgra, (uint)bgra.Length, out error);
            }
            if (status != 0) throw new InvalidOperationException($"Native text render failed ({status}, HRESULT 0x{error:X8}). Reset the text GPU device to retry.");
            byte[] rgba = new byte[count * 4], rgb = new byte[count * 3], mask = new byte[count * 3];
            NativeMediaProcess.Check(TextMethods.Convert(bgra, rgba, rgb, mask, (uint)count));
            return new(bgra, rgba, rgb, mask);
        }
    }
    public JsonObject Info
    {
        get { lock (gate) { NativeMediaProcess.Check(TextMethods.Info(handle, out var buffer)); using (buffer) { var info = JsonNode.Parse(TextMethods.Read(buffer))!.AsObject(); info["deviceResets"] = deviceResets; return info; } } }
    }
    public static byte[] ApplyMask(byte[] rgbRgba, byte[] maskRgba)
    {
        if (rgbRgba.Length != maskRgba.Length || rgbRgba.Length % 4 != 0) throw new ArgumentException("Mask pair size mismatch.");
        var result = new byte[rgbRgba.Length];
        NativeMediaProcess.Check(TextMethods.ApplyMask(rgbRgba, maskRgba, result, checked((uint)result.Length / 4)));
        return result;
    }
    public void Dispose() { lock (gate) handle.Dispose(); }
}
internal sealed class TextDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public TextDocumentHandle() : base(true) { }
    protected override bool ReleaseHandle() { TextMethods.Destroy(handle); return true; }
}
internal sealed class TextRendererHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public TextRendererHandle() : base(true) { }
    protected override bool ReleaseHandle() { TextMethods.RendererDestroy(handle); return true; }
}
internal static class TextMethods
{
    private const string Library = "Kachinco.Native.Runtime";
    internal static string Read(NativeBufferHandle buffer)
    {
        NativeMediaProcess.Check(NativeCacheMethods.Size(buffer, out uint size));
        byte[] bytes = new byte[checked((int)size)];
        NativeMediaProcess.Check(NativeCacheMethods.Copy(buffer, bytes, size));
        return Encoding.UTF8.GetString(bytes);
    }
    [DllImport(Library, EntryPoint = "kn_text_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Create(out TextDocumentHandle handle);
    [DllImport(Library, EntryPoint = "kn_text_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "kn_text_request", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Request(TextDocumentHandle handle, byte[] data, uint size, out NativeBufferHandle buffer);
    [DllImport(Library, EntryPoint = "kn_text_renderer_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RendererCreate(int forceWarp, out TextRendererHandle handle, out int error);
    [DllImport(Library, EntryPoint = "kn_text_renderer_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void RendererDestroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "kn_text_render", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Render(TextRendererHandle handle, byte[] data, uint size, long tick, int width, int height, byte[] bgra, uint capacity, out int error);
    [DllImport(Library, EntryPoint = "kn_text_renderer_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Info(TextRendererHandle handle, out NativeBufferHandle buffer);
    [DllImport(Library, EntryPoint = "kn_text_convert", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Convert(byte[] bgra, byte[] rgba, byte[] rgb, byte[] mask, uint pixels);
    [DllImport(Library, EntryPoint = "kn_text_apply_mask", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus ApplyMask(byte[] rgbRgba, byte[] maskRgba, byte[] result, uint pixels);
}
