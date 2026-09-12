using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

internal sealed class Fixture
{
    public const long T = TimelineTime.TicksPerSecond;
    public static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
    public Guid ProjectId => Id(1);
    public Guid SequenceId => Id(2);
    public Guid MovId => Id(3);
    public Guid WavId => Id(4);
    public Guid VideoTrackId => Id(5);
    public Guid AudioTrackId => Id(6);
    public Guid SubtitleTrackId => Id(7);
    public Guid ClipId => Id(8);
    public Guid AudioClipId => Id(9);
    public Guid CaptionId => Id(10);
    public EditorSession Session { get; } = new();
    public Project Project => Session.GetProject().Project!;
    public Fixture()
    {
        var result = Session.Execute(new([
            new CreateProject(ProjectId, "FLAMORIS proof"),
            new CreateSequence(SequenceId, "Landscape", SequenceSettings.Landscape, 8*T),
            new RegisterMedia(new(MovId, "MOV", "input.mov", MediaKind.Mov, 10*T)),
            new RegisterMedia(new(WavId, "WAV", "input.wav", MediaKind.Wav, 10*T, 48000, 2)),
            new AddTrack(SequenceId, VideoTrackId, "V1", TrackKind.Video),
            new AddTrack(SequenceId, AudioTrackId, "A1", TrackKind.Audio),
            new AddTrack(SequenceId, SubtitleTrackId, "S1", TrackKind.Subtitle),
            new InsertClip(SequenceId, VideoTrackId, Clip(ClipId, MovId, 0, T, 8*T)),
            new InsertClip(SequenceId, AudioTrackId, Clip(AudioClipId, WavId, T, 2*T, 6*T)),
            new AddCaption(SequenceId, SubtitleTrackId, new(CaptionId, T, 2*T, "存在薄明\nBefore the first"))
        ]));
        Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics.Select(d => d.Message)));
    }
    public static Clip Clip(Guid id, Guid asset, long start, long source, long duration) =>
        new(id, asset, start, source, duration, true, ClipAppearance.Default, AudioProperties.Default);
    public Clip VideoClip => Project.Sequences[0].Tracks[0].Clips[0];
    public EditResult Edit(params EditCommand[] commands) => Session.Execute(new([.. commands]));
}
