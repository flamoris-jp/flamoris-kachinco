using System.Collections.Immutable;

namespace Kachinco.Core;

// Reference policy and UI hints only. Actual files must pass IMediaProbe before import.
public static class MediaSourceFormats
{
    public static ImmutableArray<string> VideoExtensions { get; } = [".mov", ".mp4"];
    public static ImmutableArray<string> AudioExtensions { get; } = [".wav", ".mp3", ".m4a"];

    public static bool TryGetKind(string? path, out MediaKind kind)
    {
        var extension = Path.GetExtension(path);
        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) { kind = MediaKind.Mov; return true; }
        if (AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) { kind = MediaKind.Wav; return true; }
        kind = default;
        return false;
    }

    public static bool Supports(MediaKind kind, string? path) => TryGetKind(path, out var expected) && kind == expected;
}
