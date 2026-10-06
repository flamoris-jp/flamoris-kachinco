using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kachinco.Tests")]

namespace Kachinco.Infrastructure;

public enum PreviewDecodePreference { Software, D3D11 }

// Describes the most recently used forward video stream, not codec availability in general.
// The CLI adapter always returns host RGBA; D3D11 decode requires an explicit download.
public sealed record PreviewDecodeDiagnostics(
    PreviewDecodePreference RequestedBackend,
    PreviewDecodePreference ActiveBackend,
    bool HardwareRequested,
    bool HardwareConfirmed,
    bool RequiresCpuTransfer,
    string? FallbackReason);

internal static class FfmpegVideoDecodeArguments
{
    internal static string[] Build(string path, long tick, int width, int height, bool hardware)
    {
        List<string> args = ["-v", "info", "-nostdin", "-threads", "1", "-filter_threads", "1", "-ss", FfmpegMediaDecoder.Seconds(tick)];
        if (hardware) args.AddRange(["-hwaccel", "d3d11va", "-hwaccel_output_format", "d3d11", "-extra_hw_frames", "0"]);
        args.AddRange(["-i", Path.GetFullPath(path), "-map", "0:v:0", "-an"]);
        // hwdownload must receive hardware frames. If FFmpeg silently chooses a software
        // codec, this filter fails and the caller restarts a clean software forward stream.
        // This first CLI hardware slice downloads 8-bit NV12 surfaces. Other surface
        // formats (including 10-bit P010) safely use the software forward decoder.
        string download = hardware ? "hwdownload,format=nv12," : "";
        args.AddRange(["-vf", download + $"scale={width}:{height}:force_original_aspect_ratio=decrease,format=rgba,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black@0,showinfo=checksum=0",
            "-fps_mode", "passthrough", "-threads", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"]);
        return args.ToArray();
    }
}
