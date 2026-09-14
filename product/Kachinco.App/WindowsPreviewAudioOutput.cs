using System.IO;
using System.Runtime.InteropServices;
using Kachinco.Infrastructure;

namespace Kachinco.App;

// Short PCM buffers go directly to a real Windows output device. There is no media file,
// independent stopwatch clock, or callback into UI from unmanaged code.
public sealed class WindowsPreviewAudioOutput : IPreviewAudioOutput
{
    private IntPtr handle;
    private readonly Queue<(IntPtr Header, IntPtr Data)> buffers = new();
    private long submitted, expected = -1, wrap;
    private uint previous;
    private const uint Samples = 2, Bytes = 4, Done = 1;
    public WindowsPreviewAudioOutput()
    {
        var format = new WaveFormat { Format = 1, Channels = 2, Rate = 48000, BytesPerSecond = 192000, BlockAlign = 4, Bits = 16 };
        Check(waveOutOpen(out handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Open audio output");
        try { Check(waveOutPause(handle), "Pause audio output"); }
        catch { waveOutClose(handle); handle = IntPtr.Zero; throw; }
    }
    public long PlayedFrames
    {
        get
        {
            EnsureOpen(); var time = new MultimediaTime { Type = Samples };
            Check(waveOutGetPosition(handle, ref time, (uint)Marshal.SizeOf<MultimediaTime>()), "Read audio clock");
            if (time.Type is not (Samples or Bytes)) throw new InvalidOperationException("Audio device does not expose a sample/byte position.");
            uint raw = time.Value;
            if (raw < previous && previous - raw > int.MaxValue) wrap += 1L << 32;
            previous = raw;
            return Math.Min(submitted, (wrap + raw) / (time.Type == Bytes ? 4 : 1));
        }
    }
    public long QueuedFrames { get { Reap(); return Math.Max(0, submitted - PlayedFrames); } }
    public void Enqueue(RenderedAudioBlock block)
    {
        EnsureOpen(); Reap();
        int frames = block.Samples.Length / 2;
        if (block.SampleRate != 48000 || block.Channels != 2 || block.Samples.Length % 2 != 0 || frames is < 1 or > InteractivePreview.AudioBlockFrames ||
            buffers.Count >= 6 || QueuedFrames + frames > InteractivePreview.MaximumQueuedFrames || expected >= 0 && block.FirstSample != expected)
            throw new InvalidDataException("Audio queue exceeds its contiguous 48 kHz stereo window.");
        var pcm = new short[block.Samples.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            if (!float.IsFinite(block.Samples[i])) throw new InvalidDataException("Non-finite preview PCM.");
            pcm[i] = (short)Math.Clamp((int)Math.Round(block.Samples[i] * 32768d, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
        }
        IntPtr data = Marshal.AllocHGlobal(pcm.Length * 2), header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        bool prepared = false;
        try
        {
            Marshal.Copy(pcm, 0, data, pcm.Length);
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)(pcm.Length * 2) }, header, false);
            Check(waveOutPrepareHeader(handle, header, (uint)Marshal.SizeOf<WaveHeader>()), "Prepare audio buffer"); prepared = true;
            Check(waveOutWrite(handle, header, (uint)Marshal.SizeOf<WaveHeader>()), "Queue audio buffer");
            buffers.Enqueue((header, data)); submitted += frames; expected = block.FirstSample + frames;
        }
        catch
        {
            if (prepared) waveOutUnprepareHeader(handle, header, (uint)Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); throw;
        }
    }
    public void Play() { EnsureOpen(); Check(waveOutRestart(handle), "Start audio output"); }
    public void Pause() { EnsureOpen(); Check(waveOutPause(handle), "Pause audio output"); }
    private void Reap()
    {
        while (buffers.TryPeek(out var b) && (Marshal.PtrToStructure<WaveHeader>(b.Header).Flags & Done) != 0)
        {
            Check(waveOutUnprepareHeader(handle, b.Header, (uint)Marshal.SizeOf<WaveHeader>()), "Release audio buffer");
            buffers.Dequeue(); Marshal.FreeHGlobal(b.Header); Marshal.FreeHGlobal(b.Data);
        }
    }
    private void EnsureOpen() { if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(WindowsPreviewAudioOutput)); }
    private static void Check(uint code, string operation) { if (code != 0) throw new InvalidOperationException($"{operation} failed (Windows audio {code}). Check the selected audio device."); }
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        // Reset returns all pending buffers before unprepare/free. Never free an in-use header.
        Check(waveOutReset(handle), "Reset audio output"); Reap();
        Check(waveOutClose(handle), "Close audio output"); handle = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public ushort Format, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MultimediaTime { [FieldOffset(0)] public uint Type; [FieldOffset(4)] public uint Value; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutGetPosition(IntPtr handle, ref MultimediaTime time, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
}
