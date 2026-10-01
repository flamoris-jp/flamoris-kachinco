using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kachinco.Native;

/// <summary>Native owns the child and pipes; readers are borrowed managed stream projections.</summary>
public sealed class NativeMediaProcess : IDisposable
{
    private readonly NativeProcessHandle handle;
    private int? exitCode;
    private int disposed;
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    private NativeMediaProcess(NativeProcessHandle handle)
    {
        this.handle = handle;
        StandardOutput = new(new NativePipeStream(handle, 0), Encoding.UTF8);
        StandardError = new(new NativePipeStream(handle, 1), Encoding.UTF8);
    }
    public static NativeMediaProcess Start(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Contains('\0')) throw new ArgumentException("Executable contains NUL.", nameof(executable));
        var args = arguments.ToArray();
        if (args.Length > 256 || args.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("Invalid process arguments.", nameof(arguments));
        var pointers = new IntPtr[args.Length];
        try
        {
            for (int i = 0; i < args.Length; ++i) pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            var status = NativeProcessMethods.Start(executable, pointers, (uint)pointers.Length, out var handle, out int osError);
            if (status != NativeStatus.Ok)
            {
                handle.Dispose();
                if (status == NativeStatus.IoError) throw new Win32Exception(osError, "Media executable could not be started.");
                Check(status);
            }
            try { return new(handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { foreach (var pointer in pointers) if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }
    public bool HasExited
    {
        get
        {
            var status = NativeProcessMethods.Wait(handle, 0, out int code);
            if (status is NativeStatus.Timeout or NativeStatus.Cancelled) return false;
            Check(status); exitCode = code; return true;
        }
    }
    public int ExitCode => exitCode ?? (HasExited ? exitCode!.Value : throw new InvalidOperationException("Process has not exited."));
    public async Task WaitForExitAsync(CancellationToken token = default)
    {
        while (!HasExited) { token.ThrowIfCancellationRequested(); await Task.Delay(2, token).ConfigureAwait(false); }
    }
    public void Kill() => NativeProcessMethods.Cancel(handle);
    // One worker per bounded frame/PCM block. No timer/Task roundtrip for each pipe packet.
    // This is a sole-reader operation; do not mix with StandardOutput reads concurrently.
    public async Task<int> ReadOutputBlockAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.Length > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(buffer));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancellation = deadline.Token.Register(Kill);
        return await Task.Run(() =>
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var pinned = buffer[offset..].Pin();
                unsafe
                {
                    Check(NativeProcessMethods.ReadWait(handle, 0, (IntPtr)pinned.Pointer,
                        (uint)Math.Min(buffer.Length - offset, 64 * 1024 * 1024), out uint read));
                    deadline.Token.ThrowIfCancellationRequested();
                    if (read == 0) break;
                    offset += checked((int)read);
                }
            }
            return offset;
        }, deadline.Token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Kill();
        StandardOutput.Dispose(); StandardError.Dispose(); handle.Dispose();
    }
    internal static void Check(NativeStatus status)
    {
        if (status == NativeStatus.Cancelled) throw new OperationCanceledException("Native media operation cancelled.");
        if (status != NativeStatus.Ok) throw new IOException(NativeRuntime.StatusMessage(status));
    }

    private sealed class NativePipeStream(NativeProcessHandle handle, uint channel) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private NativeStatus ReadOnce(Memory<byte> buffer, uint timeout, out int read)
        {
            using var pinned = buffer.Pin();
            // MemoryHandle exposes a pointer only for the lifetime of its pin.
            unsafe
            {
                var status = NativeProcessMethods.Read(handle, channel, (IntPtr)pinned.Pointer,
                    (uint)Math.Min(buffer.Length, 64 * 1024 * 1024), timeout, out uint count);
                read = checked((int)count); return status;
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var memory = buffer.AsMemory(offset, count);
            if (memory.IsEmpty) return 0;
            for (;;)
            {
                var status = ReadOnce(memory, 60000, out int read);
                if (status == NativeStatus.Timeout) continue;
                Check(status); return read;
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            for (;;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = ReadOnce(buffer, 0, out int read);
                if (status != NativeStatus.Timeout) { Check(status); return read; }
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

internal sealed class NativeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeProcessHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeProcessMethods.Destroy(handle); return true; }
}
internal static class NativeProcessMethods
{
    private const string Library = "Kachinco.Native.Runtime";
    [DllImport(Library, EntryPoint = "kn_process_start", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Start([MarshalAs(UnmanagedType.LPUTF8Str)] string executable,
        [In] IntPtr[] arguments, uint count, out NativeProcessHandle output, out int osError);
    [DllImport(Library, EntryPoint = "kn_process_read", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Read(NativeProcessHandle handle, uint channel, IntPtr buffer, uint capacity, uint timeout, out uint read);
    [DllImport(Library, EntryPoint = "kn_process_read_wait", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus ReadWait(NativeProcessHandle handle, uint channel, IntPtr buffer, uint capacity, out uint read);
    [DllImport(Library, EntryPoint = "kn_process_wait", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus Wait(NativeProcessHandle handle, uint timeout, out int exitCode);
    [DllImport(Library, EntryPoint = "kn_process_cancel", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Cancel(NativeProcessHandle handle);
    [DllImport(Library, EntryPoint = "kn_process_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
}
