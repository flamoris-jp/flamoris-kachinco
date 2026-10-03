using System.Collections.Immutable;

namespace Kachinco.Core;

public sealed record EvaluatedVideoLayer(Guid TrackId, Guid ClipId, Guid MediaAssetId,
    long SourceTicks, ClipAppearance Appearance);
public sealed record EvaluatedAudio(Guid TrackId, Guid ClipId, Guid MediaAssetId, long SourceTicks, double Gain);
public sealed record EvaluatedCaption(Guid TrackId, Guid CaptionId, string Text);
public sealed record EvaluatedFrame(Guid SequenceId, long Tick, SequenceSettings Settings,
    ImmutableArray<EvaluatedVideoLayer> VideoLayers, ImmutableArray<EvaluatedAudio> Audio,
    ImmutableArray<EvaluatedCaption> Captions);
public sealed record AudioRangeContribution(Guid TrackId, Guid ClipId, Guid MediaAssetId,
    long TimelineStartTicks, long SourceStartTicks, long DurationTicks, double Gain);

// Immutable domain projection; native owns ordering, activity and source/parameter evaluation.
public sealed class TimelineEvaluator : IDisposable
{
    public Project Project { get; }
    public Sequence Sequence { get; }
    private readonly Kachinco.Native.NativeTimeline native;
    private readonly (Guid TrackId, Clip? Clip, Caption? Caption)[] identities;
    private readonly Dictionary<Guid, int> audioIndices = [];
    private TimelineEvaluator(Project project, Sequence sequence)
    {
        Project = project; Sequence = sequence;
        var items = new List<Kachinco.Native.NativeEvaluationItem>();
        var ids = new List<(Guid, Clip?, Caption?)>();
        var curves = new List<Kachinco.Native.NativeGainCurve>();
        for (int trackIndex = 0; trackIndex < sequence.Tracks.Length; ++trackIndex)
        {
            var track = sequence.Tracks[trackIndex];
            foreach (var clip in track.Clips)
            {
                var t = clip.Appearance.Transform; var id = clip.Id.ToString("N");
                items.Add(new(clip.StartTicks, clip.DurationTicks, clip.SourceInTicks,
                    Convert.ToUInt64(id[..16], 16), Convert.ToUInt64(id[16..], 16), trackIndex, ids.Count,
                    (int)track.Kind, clip.Enabled ? 1 : 0,
                    new(t.X,t.Y,t.ScaleX,t.ScaleY,t.RotationDegrees,clip.Appearance.Opacity,(int)clip.Appearance.Blend),
                    clip.Audio.Gain, clip.Audio.Muted ? 1 : 0, track.Enabled ? 1 : 0));
                ids.Add((track.Id, clip, null));
                if (track.Kind == TrackKind.Audio)
                {
                    int index = ids.Count - 1; audioIndices.Add(clip.Id, index);
                    if (!clip.Audio.VolumePoints.IsDefaultOrEmpty)
                        curves.Add(new(index, clip.Audio.VolumePoints.Select(p => new Kachinco.Native.NativeParameterPoint(p.Tick, p.Multiplier)).ToArray()));
                }
            }
            foreach (var caption in track.Captions)
            {
                var id = caption.Id.ToString("N");
                items.Add(new(caption.StartTicks, caption.DurationTicks, 0,
                    Convert.ToUInt64(id[..16],16),Convert.ToUInt64(id[16..],16), trackIndex, ids.Count, 2,
                    caption.Enabled ? 1 : 0, new(0,0,1,1,0,1,0), 1, 0, track.Enabled ? 1 : 0));
                ids.Add((track.Id, null, caption));
            }
        }
        identities = ids.ToArray(); native = new(sequence.DurationTicks, items.ToArray(), curves.ToArray());
    }
    public static Result<TimelineEvaluator> Create(Project project, Guid sequenceId)
    {
        var errors = ProjectValidator.Validate(project);
        if (!errors.IsEmpty) return new(null, errors);
        var sequence = project.Sequences.FirstOrDefault(s => s.Id == sequenceId);
        return sequence is null ? Result<TimelineEvaluator>.Fail(Diagnostic.Error("SEQUENCE_NOT_FOUND", "Sequence not found.", sequenceId)) :
            Result<TimelineEvaluator>.Ok(new(project, sequence));
    }
    public Result<EvaluatedFrame> Evaluate(long tick)
    {
        if (tick < 0 || tick >= Sequence.DurationTicks)
            return Result<EvaluatedFrame>.Fail(Diagnostic.Error("TICK_OUT_OF_RANGE", "Tick is outside the half-open sequence range.", Sequence.Id));
        var video = ImmutableArray.CreateBuilder<EvaluatedVideoLayer>();
        var audio = ImmutableArray.CreateBuilder<EvaluatedAudio>();
        var captions = ImmutableArray.CreateBuilder<EvaluatedCaption>();
        foreach (var value in native.Evaluate(tick))
        {
            var id = identities[value.Index];
            if (value.Kind == 0)
            {
                var a = value.Appearance;
                video.Add(new(id.TrackId,id.Clip!.Id,id.Clip.MediaAssetId,value.SourceStart,
                    new(new(a.X,a.Y,a.ScaleX,a.ScaleY,a.Rotation),a.Opacity,(BlendMode)a.Blend)));
            }
            else if (value.Kind == 1) audio.Add(new(id.TrackId,id.Clip!.Id,id.Clip.MediaAssetId,value.SourceStart,value.Gain));
            else captions.Add(new(id.TrackId,id.Caption!.Id,id.Caption.Text));
        }
        return Result<EvaluatedFrame>.Ok(new(Sequence.Id,tick,Sequence.Settings,video.ToImmutable(),audio.ToImmutable(),captions.ToImmutable()));
    }
    public Result<ImmutableArray<AudioRangeContribution>> EvaluateAudioRange(long startTicks, long durationTicks)
    {
        if (!TimelineTime.ValidRange(startTicks,durationTicks,Sequence.DurationTicks))
            return Result<ImmutableArray<AudioRangeContribution>>.Fail(Diagnostic.Error("INVALID_AUDIO_RANGE", "Audio range must fit the sequence.", Sequence.Id));
        return Result<ImmutableArray<AudioRangeContribution>>.Ok([.. native.Evaluate(startTicks,durationTicks).Select(value =>
        {
            var id = identities[value.Index];
            return new AudioRangeContribution(id.TrackId,id.Clip!.Id,id.Clip.MediaAssetId,value.TimelineStart,value.SourceStart,value.Duration,value.Gain);
        })]);
    }
    public void Dispose() => native.Dispose();
    public void MixAudio(Guid clipId, Span<double> mix, ReadOnlySpan<float> source, int offset, long firstSample, int rate, int channels) =>
        native.MixAudio(audioIndices[clipId], mix, source, offset, firstSample, rate, channels);
}
