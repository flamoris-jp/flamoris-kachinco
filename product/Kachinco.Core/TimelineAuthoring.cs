using System.Collections.Immutable;
using System.Numerics;

namespace Kachinco.Core;

public readonly record struct TimelineViewport(decimal PixelsPerSecond)
{
    public const decimal MinimumPixelsPerSecond = 0.000000000001m;
    public const decimal MinimumInteractivePixelsPerSecond = 4m;
    public const decimal MaximumPixelsPerSecond = 1600m;

    public bool IsValid => PixelsPerSecond is >= MinimumPixelsPerSecond and <= MaximumPixelsPerSecond;

    public static TimelineViewport Fit(long durationTicks, decimal availablePixels)
    {
        if (durationTicks <= 0) throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (availablePixels <= 0) throw new ArgumentOutOfRangeException(nameof(availablePixels));
        decimal value = checked(availablePixels * TimelineTime.TicksPerSecond / durationTicks);
        return new(Math.Clamp(value, MinimumPixelsPerSecond, MaximumPixelsPerSecond));
    }

    public TimelineViewport ZoomBy(decimal factor)
    {
        if (!IsValid) throw new InvalidOperationException("Timeline viewport is invalid.");
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        decimal lowerBound = PixelsPerSecond < MinimumInteractivePixelsPerSecond
            ? MinimumPixelsPerSecond
            : MinimumInteractivePixelsPerSecond;
        return new(Math.Clamp(checked(PixelsPerSecond * factor), lowerBound, MaximumPixelsPerSecond));
    }

    public decimal TicksToPixels(long ticks)
    {
        if (!IsValid || ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        return checked(ticks * PixelsPerSecond / TimelineTime.TicksPerSecond);
    }

    public long PixelsToTicks(decimal pixels)
    {
        if (!IsValid || pixels < 0) throw new ArgumentOutOfRangeException(nameof(pixels));
        return checked((long)decimal.Round(
            checked(pixels * TimelineTime.TicksPerSecond / PixelsPerSecond),
            0, MidpointRounding.AwayFromZero));
    }

    public long DeltaPixelsToTicks(decimal deltaPixels)
    {
        if (!IsValid) throw new InvalidOperationException("Timeline viewport is invalid.");
        return checked((long)decimal.Round(
            checked(deltaPixels * TimelineTime.TicksPerSecond / PixelsPerSecond),
            0, MidpointRounding.AwayFromZero));
    }
}

public readonly record struct TimelineSnapResult(long Ticks, bool Snapped, long? TargetTicks);

public static class TimelineSnapping
{
    public static TimelineSnapResult Snap(long candidateTicks, int thresholdPixels,
        TimelineViewport viewport, IEnumerable<long> targets)
    {
        if (candidateTicks < 0) throw new ArgumentOutOfRangeException(nameof(candidateTicks));
        if (thresholdPixels < 0) throw new ArgumentOutOfRangeException(nameof(thresholdPixels));
        long thresholdTicks = viewport.PixelsToTicks(thresholdPixels);
        long? best = null;
        BigInteger bestDistance = (BigInteger)thresholdTicks + 1;
        foreach (var target in targets.Where(x => x >= 0).Distinct().Order())
        {
            var distance = BigInteger.Abs((BigInteger)target - candidateTicks);
            if (distance <= thresholdTicks && (distance < bestDistance ||
                distance == bestDistance && (best is null || target < best.Value)))
            {
                best = target;
                bestDistance = distance;
            }
        }
        return best is { } value ? new(value, true, value) : new(candidateTicks, false, null);
    }

    public static long QuantizeToFrame(long ticks, FrameRate frameRate)
    {
        if (ticks < 0 || !frameRate.IsValid) throw new ArgumentOutOfRangeException(nameof(ticks));
        long frameIndex = TimelineTime.RoundHalfUp((BigInteger)ticks * frameRate.Numerator,
            (BigInteger)TimelineTime.TicksPerSecond * frameRate.Denominator);
        return TimelineTime.FrameToTicks(frameIndex, frameRate);
    }
}

public enum TrimEdge { Start, End }

public static class TimelineEditPlanner
{
    // Placement extends the sequence before insertion in one undoable transaction.
    public static Result<EditBatch> Place(Project project, Guid sequenceId, Guid mediaId,
        Guid trackId, Guid clipId, long startTicks, long? revision = null)
    {
        var sequence = project.Sequences.FirstOrDefault(x => x.Id == sequenceId);
        var asset = project.Assets.FirstOrDefault(x => x.Id == mediaId);
        var track = sequence?.Tracks.FirstOrDefault(x => x.Id == trackId);
        if (sequence is null || asset is null || track is null)
            return Result<EditBatch>.Fail(Diagnostic.Error("PLACEMENT_TARGET_NOT_FOUND", "Choose an existing asset, sequence and track."));
        if (!Compatible(track.Kind, asset.Kind))
            return Result<EditBatch>.Fail(Diagnostic.Error("TRACK_MEDIA_MISMATCH", "Choose a compatible video or audio track.", trackId));
        if (startTicks < 0 || clipId == Guid.Empty || ProjectValidator.ContainsId(project, clipId))
            return Result<EditBatch>.Fail(Diagnostic.Error("INVALID_PLACEMENT", "Invalid clip identity or start time.", clipId));
        try
        {
            long end = checked(startTicks + asset.DurationTicks);
            if (Overlaps(track, startTicks, asset.DurationTicks)) return Result<EditBatch>.Fail(Overlap(track.Id));
            var commands = ImmutableArray.CreateBuilder<EditCommand>();
            if (end > sequence.DurationTicks) commands.Add(new SetSequenceDuration(sequenceId, end));
            commands.Add(new InsertClip(sequenceId, trackId,
                new(clipId, mediaId, startTicks, 0, asset.DurationTicks, true, ClipAppearance.Default, AudioProperties.Default)));
            return Result<EditBatch>.Ok(new(commands.ToImmutable(), revision));
        }
        catch (OverflowException)
        {
            return Result<EditBatch>.Fail(Diagnostic.Error("TIME_OVERFLOW", "Placement exceeds supported time."));
        }
    }

    // New lane plus placement, including duration extension, is one revision and Undo unit.
    public static Result<EditBatch> PlaceOnNewTrack(Project project, Guid sequenceId, Guid mediaId,
        Guid newTrackId, Guid clipId, long startTicks, long? revision = null)
    {
        var sequence = project.Sequences.FirstOrDefault(s => s.Id == sequenceId);
        var asset = project.Assets.FirstOrDefault(a => a.Id == mediaId);
        if (sequence is null || asset is null || newTrackId == Guid.Empty || ProjectValidator.ContainsId(project, newTrackId) || newTrackId == clipId)
            return Result<EditBatch>.Fail(Diagnostic.Error("INVALID_PLACEMENT", "Choose a sequence, media and unique track identity."));
        var kind = asset.Kind == MediaKind.Mov ? TrackKind.Video : TrackKind.Audio;
        int index = Array.FindIndex(sequence.Tracks.ToArray(), t => t.Kind == kind);
        if (index < 0) index = 0;
        string name = (kind == TrackKind.Video ? "V" : "A") + (sequence.Tracks.Count(t => t.Kind == kind) + 1);
        var add = new AddTrack(sequenceId, newTrackId, name, kind);
        var reorder = new ReorderTrack(sequenceId, newTrackId, index);
        // Pure planning uses the same command semantics as the eventual atomic session transaction.
        var projected = CommandApplier.Apply(CommandApplier.Apply(project, add), reorder);
        var placement = Place(projected, sequenceId, mediaId, newTrackId, clipId, startTicks, revision);
        return placement.Success ? Result<EditBatch>.Ok(new([add, reorder, .. placement.Value!.Commands], revision)) : placement;
    }
    public static bool Overlaps(Track track, long start, long duration, Guid? excluded = null) =>
        track.Clips.Any(c => c.Id != excluded && (System.Numerics.BigInteger)c.StartTicks < (System.Numerics.BigInteger)start + duration && c.EndTicks > start);
    private static Diagnostic Overlap(Guid id) => Diagnostic.Error("CLIP_OVERLAP", "This placement overlaps a clip. Choose free space or the + track row.", id);

    public static Result<MoveClip> Move(Project project, Guid sequenceId, Guid clipId,
        Guid targetTrackId, long startTicks)
    {
        var found = Find(project, sequenceId, clipId);
        if (!found.Success) return new(null, found.Diagnostics);
        var (sequence, _, clip) = found.Value;
        var target = sequence.Tracks.FirstOrDefault(x => x.Id == targetTrackId);
        if (target is null) return Result<MoveClip>.Fail(Diagnostic.Error("TRACK_NOT_FOUND", "Track not found.", targetTrackId));
        var asset = project.Assets.First(x => x.Id == clip.MediaAssetId);
        if (!Compatible(target.Kind, asset.Kind))
            return Result<MoveClip>.Fail(Diagnostic.Error("TRACK_MEDIA_MISMATCH", "Choose a compatible video or audio track.", clipId));
        if (!TimelineTime.ValidRange(startTicks, clip.DurationTicks, sequence.DurationTicks))
            return Result<MoveClip>.Fail(Diagnostic.Error("INVALID_TIMELINE_RANGE", "Moved clip must fit inside the sequence.", clipId));
        if (Overlaps(target, startTicks, clip.DurationTicks, clipId)) return Result<MoveClip>.Fail(Overlap(targetTrackId));
        return Result<MoveClip>.Ok(new(sequenceId, clipId, targetTrackId, startTicks));
    }

    public static Result<TrimClip> Trim(Project project, Guid sequenceId, Guid clipId,
        TrimEdge edge, long edgeTicks)
    {
        var found = Find(project, sequenceId, clipId);
        if (!found.Success) return new(null, found.Diagnostics);
        var (sequence, track, clip) = found.Value;
        TrimClip command;
        try
        {
            command = edge switch
            {
                TrimEdge.Start when edgeTicks > clip.StartTicks && edgeTicks < clip.EndTicks =>
                    new(sequenceId, clipId, edgeTicks,
                        checked(clip.SourceInTicks + edgeTicks - clip.StartTicks), clip.EndTicks - edgeTicks),
                TrimEdge.Start when edgeTicks < clip.StartTicks =>
                    new(sequenceId, clipId, edgeTicks,
                        checked(clip.SourceInTicks - (clip.StartTicks - edgeTicks)), clip.EndTicks - edgeTicks),
                TrimEdge.End when edgeTicks > clip.StartTicks =>
                    new(sequenceId, clipId, clip.StartTicks, clip.SourceInTicks, edgeTicks - clip.StartTicks),
                _ => throw new ArgumentOutOfRangeException(nameof(edgeTicks))
            };
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException)
        {
            return Result<TrimClip>.Fail(Diagnostic.Error("INVALID_TRIM", "Trim edge is outside the available timeline/source range.", clipId));
        }

        var asset = project.Assets.First(x => x.Id == clip.MediaAssetId);
        if (!TimelineTime.ValidRange(command.StartTicks, command.DurationTicks, sequence.DurationTicks) ||
            !TimelineTime.ValidRange(command.SourceInTicks, command.DurationTicks, asset.DurationTicks))
            return Result<TrimClip>.Fail(Diagnostic.Error("INVALID_TRIM", "Trim edge is outside the available timeline/source range.", clipId));
        if (Overlaps(track, command.StartTicks, command.DurationTicks, clipId)) return Result<TrimClip>.Fail(Overlap(track.Id));
        return Result<TrimClip>.Ok(command);
    }

    public static Result<SplitClip> Split(Project project, Guid sequenceId, Guid clipId,
        long splitTicks, Guid rightClipId)
    {
        var found = Find(project, sequenceId, clipId);
        if (!found.Success) return new(null, found.Diagnostics);
        var (_, _, clip) = found.Value;
        if (rightClipId == Guid.Empty || ProjectValidator.ContainsId(project, rightClipId))
            return Result<SplitClip>.Fail(Diagnostic.Error("INVALID_ID", "The new right clip ID must be unique and nonempty.", rightClipId));
        if (splitTicks <= clip.StartTicks || splitTicks >= clip.EndTicks)
            return Result<SplitClip>.Fail(Diagnostic.Error("INVALID_SPLIT", "Split must be strictly inside the clip.", clipId));
        return Result<SplitClip>.Ok(new(sequenceId, clipId, splitTicks, rightClipId));
    }

    public static ImmutableArray<long> SnapTargets(Sequence sequence, Guid? excludedClipId, long playheadTicks)
    {
        var targets = ImmutableArray.CreateBuilder<long>();
        targets.Add(0);
        if (playheadTicks >= 0 && playheadTicks <= sequence.DurationTicks) targets.Add(playheadTicks);
        foreach (var track in sequence.Tracks)
            foreach (var clip in track.Clips)
                if (clip.Id != excludedClipId)
                {
                    targets.Add(clip.StartTicks);
                    targets.Add(clip.EndTicks);
                }
        return [.. targets.Distinct().Order()];
    }

    private static Result<(Sequence Sequence, Track Track, Clip Clip)> Find(Project project, Guid sequenceId, Guid clipId)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sequence = project.Sequences.FirstOrDefault(x => x.Id == sequenceId);
        if (sequence is null) return Result<(Sequence, Track, Clip)>.Fail(Diagnostic.Error("SEQUENCE_NOT_FOUND", "Sequence not found.", sequenceId));
        foreach (var track in sequence.Tracks)
            if (track.Clips.FirstOrDefault(x => x.Id == clipId) is { } clip)
                return Result<(Sequence, Track, Clip)>.Ok((sequence, track, clip));
        return Result<(Sequence, Track, Clip)>.Fail(Diagnostic.Error("CLIP_NOT_FOUND", "Clip not found.", clipId));
    }

    private static bool Compatible(TrackKind track, MediaKind media) =>
        track == TrackKind.Video && media == MediaKind.Mov || track == TrackKind.Audio && media == MediaKind.Wav;
}
