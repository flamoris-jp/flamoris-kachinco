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

// Validates once, then keeps an immutable snapshot. Preview/export/audio share this map.
public sealed class TimelineEvaluator
{
    public Project Project { get; }
    public Sequence Sequence { get; }
    private readonly ImmutableArray<Track> orderedTracks;
    private TimelineEvaluator(Project project, Sequence sequence)
    {
        Project = project; Sequence = sequence;
        orderedTracks = [.. sequence.Tracks.Select(t => t with
        { Clips = TimelineQueries.ListClips(t), Captions = TimelineQueries.ListCaptions(t) })];
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
        foreach (var track in orderedTracks)
        {
            if (!track.Enabled) continue;
            foreach (var clip in track.Clips)
            {
                if (!clip.Enabled || !TimelineTime.Contains(clip.StartTicks, clip.DurationTicks, tick)) continue;
                long sourceTicks = SourceTick(clip, tick);
                if (track.Kind == TrackKind.Video)
                    video.Add(new(track.Id, clip.Id, clip.MediaAssetId, sourceTicks, clip.Appearance));
                else if (track.Kind == TrackKind.Audio && !clip.Audio.Muted && clip.Audio.Gain > 0)
                    audio.Add(new(track.Id, clip.Id, clip.MediaAssetId, sourceTicks, clip.Audio.Gain));
            }
            foreach (var caption in track.Captions)
                if (caption.Enabled && TimelineTime.Contains(caption.StartTicks, caption.DurationTicks, tick))
                    captions.Add(new(track.Id, caption.Id, caption.Text));
        }
        return Result<EvaluatedFrame>.Ok(new(Sequence.Id, tick, Sequence.Settings, video.ToImmutable(), audio.ToImmutable(), captions.ToImmutable()));
    }

    public Result<ImmutableArray<AudioRangeContribution>> EvaluateAudioRange(long startTicks, long durationTicks)
    {
        if (!TimelineTime.ValidRange(startTicks, durationTicks, Sequence.DurationTicks))
            return Result<ImmutableArray<AudioRangeContribution>>.Fail(Diagnostic.Error("INVALID_AUDIO_RANGE", "Audio range must fit the sequence.", Sequence.Id));
        long endTicks = startTicks + durationTicks;
        var result = ImmutableArray.CreateBuilder<AudioRangeContribution>();
        foreach (var track in orderedTracks.Where(t => t.Enabled && t.Kind == TrackKind.Audio))
            foreach (var clip in track.Clips.Where(c => c.Enabled && !c.Audio.Muted && c.Audio.Gain > 0))
            {
                long start = Math.Max(startTicks, clip.StartTicks), end = Math.Min(endTicks, clip.EndTicks);
                if (start < end) result.Add(new(track.Id, clip.Id, clip.MediaAssetId, start, SourceTick(clip, start), end - start, clip.Audio.Gain));
            }
        return Result<ImmutableArray<AudioRangeContribution>>.Ok(result.ToImmutable());
    }

    private static long SourceTick(Clip clip, long timelineTick) => checked(clip.SourceInTicks + (timelineTick - clip.StartTicks));
}
