using System.Collections.Immutable;

namespace Kachinco.Core;

// Transient import planning; native commands remain the project authority.
public static class CaptionImport
{
    public static EditBatch Plan(Sequence sequence, ImmutableArray<Caption> captions,
        long offsetTicks, long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetTicks);
        if (captions.IsEmpty) return new([], revision);
        var shifted = captions.Select(c => c with { StartTicks = checked(c.StartTicks + offsetTicks) }).ToArray();
        long end = shifted.Max(c => checked(c.StartTicks + c.DurationTicks));
        var track = sequence.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Subtitle);
        var trackId = track?.Id ?? Guid.NewGuid();
        var commands = new List<EditCommand>();
        if (end > sequence.DurationTicks) commands.Add(new SetSequenceDuration(sequence.Id, end));
        if (track is null) commands.Add(new AddTrack(sequence.Id, trackId, "字幕", TrackKind.Subtitle));
        commands.AddRange(shifted.Select(c => new AddCaption(sequence.Id, trackId, c)));
        return new([.. commands], revision);
    }
}
