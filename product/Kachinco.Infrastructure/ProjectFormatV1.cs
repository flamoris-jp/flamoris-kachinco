using System.Collections.Immutable;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// These DTOs are the v1 disk contract, independent of domain record evolution.
internal sealed record EnvelopeV1(string Format, int SchemaVersion, long Timebase, ProjectV1 Project);
internal sealed record ProjectV1(Guid Id, string Name, AssetV1[] Assets, SequenceV1[] Sequences);
internal sealed record AssetV1(Guid Id, string Name, string SourcePath, MediaKind Kind, long DurationTicks, int? SampleRate, int? Channels);
internal sealed record SettingsV1(int Width, int Height, int FpsNumerator, int FpsDenominator);
internal sealed record SequenceV1(Guid Id, string Name, SettingsV1 Settings, long DurationTicks, TrackV1[] Tracks);
internal sealed record TrackV1(Guid Id, string Name, TrackKind Kind, bool Enabled, ClipV1[] Clips, CaptionV1[] Captions);
internal sealed record TransformV1(double X, double Y, double ScaleX, double ScaleY, double RotationDegrees);
internal sealed record AppearanceV1(TransformV1 Transform, double Opacity, BlendMode Blend);
internal sealed record AudioV1(double Gain, bool Muted);
internal sealed record ClipV1(Guid Id, Guid MediaAssetId, long StartTicks, long SourceInTicks, long DurationTicks, bool Enabled, AppearanceV1 Appearance, AudioV1 Audio);
internal sealed record CaptionV1(Guid Id, long StartTicks, long DurationTicks, string Text, bool Enabled);

internal static class FormatV1
{
    internal static EnvelopeV1 Encode(Project p) => new(ProjectJson.Format, 1, TimelineTime.TicksPerSecond,
        new(p.Id, p.Name,
            TimelineQueries.ListMediaAssets(p).Select(a => new AssetV1(a.Id, a.Name, a.SourcePath, a.Kind, a.DurationTicks, a.SampleRate, a.Channels)).ToArray(),
            TimelineQueries.ListSequences(p).Select(s => new SequenceV1(s.Id, s.Name,
                new(s.Settings.Width, s.Settings.Height, s.Settings.FrameRate.Numerator, s.Settings.FrameRate.Denominator), s.DurationTicks,
                s.Tracks.Select(t => new TrackV1(t.Id, t.Name, t.Kind, t.Enabled,
                    TimelineQueries.ListClips(t).Select(c => new ClipV1(c.Id, c.MediaAssetId, c.StartTicks, c.SourceInTicks, c.DurationTicks, c.Enabled,
                        new(new(c.Appearance.Transform.X, c.Appearance.Transform.Y, c.Appearance.Transform.ScaleX, c.Appearance.Transform.ScaleY, c.Appearance.Transform.RotationDegrees), c.Appearance.Opacity, c.Appearance.Blend),
                        new(c.Audio.Gain, c.Audio.Muted))).ToArray(),
                    TimelineQueries.ListCaptions(t).Select(c => new CaptionV1(c.Id, c.StartTicks, c.DurationTicks, c.Text, c.Enabled)).ToArray())).ToArray())).ToArray()));

    internal static Project Decode(EnvelopeV1 envelope)
    {
        var p = Need(envelope.Project);
        return new(p.Id, Need(p.Name),
            [.. Items(p.Assets).Select(a => new MediaAsset(a.Id, Need(a.Name), Need(a.SourcePath), a.Kind, a.DurationTicks, a.SampleRate, a.Channels))],
            [.. Items(p.Sequences).Select(s =>
            {
                var settings = Need(s.Settings);
                return new Sequence(s.Id, Need(s.Name), new(settings.Width, settings.Height, new(settings.FpsNumerator, settings.FpsDenominator)), s.DurationTicks,
                    [.. Items(s.Tracks).Select(t => new Track(t.Id, Need(t.Name), t.Kind, t.Enabled,
                        [.. Items(t.Clips).Select(c =>
                        {
                            var a = Need(c.Appearance); var transform = Need(a.Transform); var audio = Need(c.Audio);
                            return new Clip(c.Id, c.MediaAssetId, c.StartTicks, c.SourceInTicks, c.DurationTicks, c.Enabled,
                                new(new(transform.X, transform.Y, transform.ScaleX, transform.ScaleY, transform.RotationDegrees), a.Opacity, a.Blend), new(audio.Gain, audio.Muted));
                        })],
                        [.. Items(t.Captions).Select(c => new Caption(c.Id, c.StartTicks, c.DurationTicks, Need(c.Text), c.Enabled))]))]);
            })]);
    }
    private static T Need<T>(T? value) where T : class => value ?? throw new FormatException("A required field is null.");
    private static IEnumerable<T> Items<T>(T[]? values) where T : class => Need(values).Select(v => Need(v));
}
