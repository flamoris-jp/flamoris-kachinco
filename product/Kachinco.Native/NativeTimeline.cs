using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Kachinco.Native;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeEvaluationItem(long Start, long Duration, long Source, ulong IdHigh, ulong IdLow,
    int Track, int Index, int Kind, int Enabled, NativeAppearance Appearance, double Gain, int Muted, int TrackEnabled);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeEvaluationResult(int Index, int Kind, long TimelineStart, long SourceStart,
    long Duration, NativeAppearance Appearance, double Gain);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeParameterPoint(long Tick, double Value);
public sealed record NativeGainCurve(int Index, NativeParameterPoint[] Points);

public sealed class NativeTimeline : IDisposable
{
    private readonly TimelineHandle handle;
    private readonly int capacity;
    public NativeTimeline(long duration, NativeEvaluationItem[] items, NativeGainCurve[]? curves = null)
    {
        NativeMediaProcess.Check(Methods.Create(duration, items, (uint)items.Length, out handle));
        capacity = items.Length;
        try
        {
            foreach (var curve in curves ?? []) NativeMediaProcess.Check(Methods.SetGainCurve(handle, curve.Index, curve.Points, checked((uint)curve.Points.Length)));
            handle.Account(checked(items.LongLength * Marshal.SizeOf<NativeEvaluationItem>() + (curves?.Sum(c => c.Points.LongLength * 16) ?? 0) + 32));
        }
        catch { handle.Dispose(); throw; }
    }
    public NativeEvaluationResult[] Evaluate(long tick, long duration = 0)
    {
        var result = new NativeEvaluationResult[capacity];
        NativeMediaProcess.Check(Methods.Evaluate(handle, tick, duration, result, (uint)capacity, out uint count));
        Array.Resize(ref result, checked((int)count)); return result;
    }
    public static double Parameter(NativeParameterPoint[] points, long tick, double fallback)
    {
        NativeMediaProcess.Check(Methods.Parameter(points, (uint)points.Length, tick, fallback, out double value)); return value;
    }
    public unsafe void MixAudio(int index, Span<double> mix, ReadOnlySpan<float> source, int offset, long firstSample, int rate, int channels)
    {
        fixed (double* dst = mix)
        fixed (float* src = source)
            NativeMediaProcess.Check(Methods.MixAudio(handle, index, (IntPtr)dst, checked((uint)mix.Length), (IntPtr)src,
                checked((uint)source.Length), checked((uint)offset), firstSample, rate, channels));
    }
    public void Dispose() => handle.Dispose();
    private sealed class TimelineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private long bytes;
        public TimelineHandle() : base(true) { }
        public void Account(long size) { GC.AddMemoryPressure(size); bytes = size; }
        protected override bool ReleaseHandle()
        {
            Methods.Destroy(handle);
            if (bytes > 0) GC.RemoveMemoryPressure(bytes);
            return true;
        }
    }
    private static class Methods
    {
        private const string Library = "Kachinco.Native.Runtime";
        [DllImport(Library, EntryPoint = "kn_timeline_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Create(long duration, [In] NativeEvaluationItem[] items, uint count, out TimelineHandle handle);
        [DllImport(Library, EntryPoint = "kn_timeline_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);
        [DllImport(Library, EntryPoint = "kn_timeline_evaluate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Evaluate(TimelineHandle handle, long tick, long duration, [Out] NativeEvaluationResult[] output, uint capacity, out uint count);
        [DllImport(Library, EntryPoint = "kn_parameter_at", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus Parameter([In] NativeParameterPoint[] points, uint count, long tick, double fallback, out double output);
        [DllImport(Library, EntryPoint = "kn_timeline_set_gain_curve", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus SetGainCurve(TimelineHandle handle, int index, [In] NativeParameterPoint[] points, uint count);
        [DllImport(Library, EntryPoint = "kn_timeline_mix_audio", CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeStatus MixAudio(TimelineHandle handle, int index, IntPtr mix, uint mixCount, IntPtr source, uint sourceCount, uint offset,
            long firstSample, int rate, int channels);
    }
}
