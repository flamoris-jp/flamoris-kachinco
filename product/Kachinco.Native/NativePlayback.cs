using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Kachinco.Native;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativePlaybackTicket(long Generation, long Position, long RenderTick, long StartSample, long TotalSamples);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeVideoStep(long Position, long VideoTick, long Dropped, int Ended, int Present);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeAudioStep(long FirstSample, int Count, int Underrun, int Ready, int Resume);

/// <summary>Serialized host adapter for native playback decisions; no clock of its own.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeDecoderCandidate(long Id,long Used,long Start,long LastRequest,int Consumed,int Eligible);

public sealed class NativePlayback : IDisposable
{
    private readonly PlaybackHandle handle;
    public NativePlayback() => NativeMediaProcess.Check(Methods.Create(out handle));
    public NativePlaybackTicket Request(long duration, int numerator, int denominator, long tick, bool play)
    {
        NativeMediaProcess.Check(Methods.Request(handle,duration,numerator,denominator,tick,play?1:0,out var value)); return value;
    }
    public long Cancel() { NativeMediaProcess.Check(Methods.Cancel(handle,out long value)); return value; }
    public bool Accept(long generation) => Methods.Accept(handle,generation) == NativeStatus.Ok;
    public long Clock(long generation,long played)
    { NativeMediaProcess.Check(Methods.Clock(handle,generation,played,out long value)); return value; }
    public NativeVideoStep Video(long generation,long played,int readyCount,long readyTick)
    { NativeMediaProcess.Check(Methods.Video(handle,generation,played,readyCount,readyTick,out var value)); return value; }
    public NativeAudioStep Audio(long generation,long queued)
    { NativeMediaProcess.Check(Methods.Audio(handle,generation,queued,out var value)); return value; }
    public NativeVideoStep Presentation(long generation, long played, long readyTick, long nextReadyTick)
    { NativeMediaProcess.Check(Methods.Presentation(handle, generation, played, readyTick, nextReadyTick, out var value)); return value; }
    public static long FirstSample(long tick,int rate=48000)
    { NativeMediaProcess.Check(Methods.FirstSample(tick,rate,out long value)); return value; }
    public static (long Selected,long Oldest) SelectDecoder(NativeDecoderCandidate[] candidates,bool video,long tick,int samples=0)
    {
        NativeMediaProcess.Check(Methods.SelectDecoder(candidates,(uint)candidates.Length,video?1:0,tick,samples,out long selected,out long oldest));
        return (selected,oldest);
    }
    public void Dispose() => handle.Dispose();
    private sealed class PlaybackHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PlaybackHandle() : base(true) { }
        protected override bool ReleaseHandle() { Methods.Destroy(handle); return true; }
    }
    private static class Methods
    {
        [DllImport("Kachinco.Native.Runtime", EntryPoint="kn_decoder_select", CallingConvention=CallingConvention.Cdecl)]
        internal static extern NativeStatus SelectDecoder([In] NativeDecoderCandidate[] candidates,uint count,int video,long tick,int samples,out long selected,out long oldest);
        private const string Library="Kachinco.Native.Runtime";
        [DllImport(Library, EntryPoint="kn_playback_create", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Create(out PlaybackHandle handle);
        [DllImport(Library, EntryPoint="kn_playback_destroy", CallingConvention=CallingConvention.Cdecl)] internal static extern void Destroy(IntPtr handle);
        [DllImport(Library, EntryPoint="kn_playback_request", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Request(PlaybackHandle handle,long duration,int numerator,int denominator,long tick,int play,out NativePlaybackTicket value);
        [DllImport(Library, EntryPoint="kn_playback_cancel", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Cancel(PlaybackHandle handle,out long generation);
        [DllImport(Library, EntryPoint="kn_playback_accept", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Accept(PlaybackHandle handle,long generation);
        [DllImport(Library, EntryPoint="kn_playback_clock", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Clock(PlaybackHandle handle,long generation,long played,out long position);
        [DllImport(Library, EntryPoint="kn_playback_video", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Video(PlaybackHandle handle,long generation,long played,int count,long tick,out NativeVideoStep value);
        [DllImport(Library, EntryPoint="kn_playback_audio", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Audio(PlaybackHandle handle,long generation,long queued,out NativeAudioStep value);
        [DllImport(Library, EntryPoint="kn_playback_present", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus Presentation(PlaybackHandle handle,long generation,long played,long readyTick,long nextReadyTick,out NativeVideoStep value);
        [DllImport(Library, EntryPoint="kn_first_sample", CallingConvention=CallingConvention.Cdecl)] internal static extern NativeStatus FirstSample(long tick,int rate,out long value);
    }
}
