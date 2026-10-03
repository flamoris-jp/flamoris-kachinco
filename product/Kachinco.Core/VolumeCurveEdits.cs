using System.Collections.Immutable;

namespace Kachinco.Core;

// Builds ordinary commands only. Native EditorSession remains the mutation authority.
public static class VolumeCurveEdits
{
    public static EditBatch Fade(ProjectSnapshot baseline, Guid sequenceId, Guid clipId, bool fadeIn)
    {
        var clip = baseline.Project?.Sequences.First(s => s.Id == sequenceId).Tracks
            .Where(t => t.Kind == TrackKind.Audio).SelectMany(t => t.Clips).First(c => c.Id == clipId)
            ?? throw new ArgumentException("Audio clip not found.");
        long length = Math.Min(TimelineTime.TicksPerSecond, clip.DurationTicks / 2);
        if (length == 0) throw new ArgumentException("Clip is too short for a fade.");
        long start = fadeIn ? 0 : clip.DurationTicks - length, end = fadeIn ? length : clip.DurationTicks;
        var commands = ImmutableArray.CreateBuilder<EditCommand>();
        foreach (var point in clip.Audio.VolumePoints.Where(p => p.Tick > start && p.Tick < end))
            commands.Add(new DeleteClipVolumePoint(sequenceId, clipId, point.Id));
        Upsert(start, fadeIn ? 0 : 1); Upsert(end, fadeIn ? 1 : 0);
        return new(commands.ToImmutable(), baseline.Revision);
        void Upsert(long tick, double multiplier)
        {
            var existing = clip.Audio.VolumePoints.FirstOrDefault(p => p.Tick == tick);
            commands.Add(existing is null ? new AddClipVolumePoint(sequenceId, clipId, new(Guid.NewGuid(), tick, multiplier))
                : new UpdateClipVolumePoint(sequenceId, clipId, existing with { Multiplier = multiplier }));
        }
    }
}

public sealed class VolumePointEdit(ProjectSnapshot baseline, Guid sequenceId, Guid clipId, VolumePoint point)
{
    public ProjectSnapshot Baseline { get; } = baseline;
    public Guid SequenceId { get; } = sequenceId;
    public EditBatch Batch(double multiplier) => new([Command(multiplier)], Baseline.Revision);
    private UpdateClipVolumePoint Command(double multiplier) => new(SequenceId, clipId, point with { Multiplier = multiplier });
    public bool IsUnchanged(double multiplier) => multiplier == point.Multiplier;
    public Result<ProjectSnapshot> Preview(double multiplier)
    {
        var result = NativeProjectCodec.ProjectCommands(Baseline.Project!, Command(multiplier));
        return result.Success ? Result<ProjectSnapshot>.Ok(Baseline with { Project = result.Value }) : new(null, result.Diagnostics);
    }
}
