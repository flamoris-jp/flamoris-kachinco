using System.Collections.Immutable;

namespace Kachinco.Core;

public enum MediaKind { Mov, Wav }
public enum TrackKind { Video, Audio, Subtitle }
public enum BlendMode { Normal, Screen }

public sealed record Project(Guid Id, string Name, ImmutableArray<MediaAsset> Assets,
    ImmutableArray<Sequence> Sequences);
public sealed record MediaAsset(Guid Id, string Name, string SourcePath, MediaKind Kind,
    long DurationTicks, int? SampleRate = null, int? Channels = null);
public sealed record SequenceSettings(int Width, int Height, FrameRate FrameRate)
{
    public static SequenceSettings Landscape => new(1920, 1080, new(30, 1));
    public static SequenceSettings Portrait => new(1080, 1920, new(30, 1));
}
public sealed record Sequence(Guid Id, string Name, SequenceSettings Settings,
    long DurationTicks, ImmutableArray<Track> Tracks);
// Array order is bottom-to-top. Items are queried/evaluated by start then UUID.
public sealed record Track(Guid Id, string Name, TrackKind Kind, bool Enabled,
    ImmutableArray<Clip> Clips, ImmutableArray<Caption> Captions);
public sealed record Clip(Guid Id, Guid MediaAssetId, long StartTicks, long SourceInTicks,
    long DurationTicks, bool Enabled, ClipAppearance Appearance, AudioProperties Audio)
{
    public long EndTicks => checked(StartTicks + DurationTicks);
    public long SourceOutTicks => checked(SourceInTicks + DurationTicks);
}
public sealed record Caption(Guid Id, long StartTicks, long DurationTicks, string Text, bool Enabled = true);
public sealed record Transform2D(double X, double Y, double ScaleX, double ScaleY, double RotationDegrees)
{
    public static Transform2D Identity => new(0, 0, 1, 1, 0);
}
public sealed record ClipAppearance(Transform2D Transform, double Opacity, BlendMode Blend)
{
    public static ClipAppearance Default => new(Transform2D.Identity, 1, BlendMode.Normal);
}
public sealed record AudioProperties(double Gain, bool Muted)
{
    public static AudioProperties Default => new(1, false);
}

public enum DiagnosticSeverity { Error, Warning, Info }
public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message,
    Guid? EntityId = null, string? Path = null)
{
    public static Diagnostic Error(string code, string message, Guid? id = null, string? path = null) =>
        new(code, DiagnosticSeverity.Error, message, id, path);
}

public sealed record Result<T>(T? Value, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Success => !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public static Result<T> Ok(T value) => new(value, []);
    public static Result<T> Fail(params Diagnostic[] diagnostics) => new(default, [.. diagnostics]);
}
