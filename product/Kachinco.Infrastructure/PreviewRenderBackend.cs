using Kachinco.Native;

namespace Kachinco.Infrastructure;

public enum PreviewBackendPreference { Auto, Cpu, D3D11 }
public sealed record PreviewBackendDiagnostics(PreviewBackendPreference Requested, string Active,
    ulong AllocatedBytes, string? FallbackReason, bool RequiresCpuTransfer = true);

// Host boundary only. Transform, opacity and blend semantics remain native authority.
public interface IPreviewFrameCompositor : IDisposable
{
    ulong AllocatedBytes { get; }
    void Begin(int width, int height);
    void Composite(ReadOnlySpan<byte> pixels, NativeAppearance appearance);
    byte[] Read();
    void Reset();
}
public delegate bool PreviewCompositorFactory(out IPreviewFrameCompositor? compositor, out string reason);

public sealed class PreviewRenderBackend : IDisposable
{
    public const ulong DefaultGpuBudgetBytes = 256UL * 1024 * 1024;
    // Retaining decoded CPU inputs permits exact same-frame fallback without another
    // evaluation or decode. Above this bound the frame uses streaming native CPU.
    public const long MaximumRetainedInputBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim frameGate = new(1, 1);
    private readonly object diagnosticsGate = new();
    private readonly PreviewCompositorFactory factory;
    private IPreviewFrameCompositor? compositor;
    private bool disposed, disabled;
    private PreviewBackendDiagnostics diagnostics;

    public PreviewRenderBackend(PreviewBackendPreference preference = PreviewBackendPreference.Auto,
        PreviewCompositorFactory? factory = null)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        this.factory = factory ?? CreateNative;
        diagnostics = new(preference, "CPU", 0, null);
    }
    public PreviewBackendDiagnostics Diagnostics { get { lock (diagnosticsGate) return diagnostics; } }
    internal async ValueTask<IDisposable> EnterAsync(CancellationToken token)
    {
        await frameGate.WaitAsync(token).ConfigureAwait(false);
        if (disposed) { frameGate.Release(); throw new ObjectDisposedException(nameof(PreviewRenderBackend)); }
        return new Lease(frameGate);
    }
    // All mutations are protected by a frame lease, including complete async decode.
    internal bool TryBegin(int width, int height, int inputCount, bool identityFastPath)
    {
        if (Diagnostics.Requested == PreviewBackendPreference.Cpu || disabled) return false;
        if (Diagnostics.Requested == PreviewBackendPreference.Auto && identityFastPath)
        { Update("CPU", "Identity frame uses the native CPU copy fast path."); return false; }
        long pixels = checked((long)width * height * 4);
        if (inputCount > 0 && pixels > MaximumRetainedInputBytes / inputCount)
        { Update("CPU", "Decoded inputs exceed the bounded GPU fallback buffer."); return false; }
        try
        {
            if (compositor is null)
            {
                if (!factory(out var created, out var reason))
                { created?.Dispose(); disabled = true; Update("CPU", reason); return false; }
                compositor = created ?? throw new InvalidOperationException("GPU factory returned no compositor.");
            }
            compositor.Begin(width, height); Update("D3D11", null); return true;
        }
        catch (Exception e) when (IsGpuFailure(e)) { FallBack(e.Message); return false; }
    }
    internal void Composite(ReadOnlySpan<byte> pixels, NativeAppearance appearance) => compositor!.Composite(pixels, appearance);
    internal byte[] Read() { var pixels = compositor!.Read(); Update("D3D11", null); return pixels; }
    internal void AbandonFrame()
    {
        try { compositor?.Reset(); Update(Diagnostics.Active, Diagnostics.FallbackReason); }
        catch (Exception e) when (IsGpuFailure(e)) { FallBack(e.Message); }
    }
    internal void FallBack(string reason)
    {
        disabled = true;
        ReleaseCompositor(); Update("CPU", reason);
    }
    public async ValueTask ResetAsync(CancellationToken token = default)
    {
        using var lease = await EnterAsync(token).ConfigureAwait(false);
        AbandonFrame();
    }
    public async ValueTask SelectAsync(PreviewBackendPreference preference, CancellationToken token = default)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        using var lease = await EnterAsync(token).ConfigureAwait(false);
        ReleaseCompositor(); disabled = false;
        lock (diagnosticsGate) diagnostics = new(preference, "CPU", 0, null);
    }
    private void Update(string active, string? reason)
    {
        lock (diagnosticsGate) diagnostics = diagnostics with
        { Active = active, AllocatedBytes = compositor?.AllocatedBytes ?? 0, FallbackReason = reason };
    }
    private void ReleaseCompositor()
    {
        var old = compositor; compositor = null;
        try { old?.Dispose(); } catch (Exception e) when (IsGpuFailure(e)) { /* CPU recovery must remain available. */ }
    }
    internal static bool IsGpuFailure(Exception e) => e is InvalidOperationException or IOException or InvalidDataException or
        NotSupportedException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException;
    private static bool CreateNative(out IPreviewFrameCompositor? compositor, out string reason)
    {
        bool ready = NativeGpuCompositor.TryCreate(out var native, out reason, DefaultGpuBudgetBytes);
        compositor = ready ? new NativeAdapter(native!) : null; return ready;
    }
    public void Dispose()
    {
        frameGate.Wait();
        try
        {
            if (disposed) return;
            disposed = true; ReleaseCompositor(); Update("CPU", Diagnostics.FallbackReason);
        }
        finally { frameGate.Release(); }
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? owned = gate;
        public void Dispose() => Interlocked.Exchange(ref owned, null)?.Release();
    }
    private sealed class NativeAdapter(NativeGpuCompositor native) : IPreviewFrameCompositor
    {
        public ulong AllocatedBytes => native.AllocatedBytes;
        public void Begin(int width, int height) => native.Begin(width, height);
        public void Composite(ReadOnlySpan<byte> pixels, NativeAppearance appearance) => native.Composite(pixels, appearance);
        public byte[] Read() => native.Read();
        public void Reset() => native.Reset();
        public void Dispose() => native.Dispose();
    }
}
