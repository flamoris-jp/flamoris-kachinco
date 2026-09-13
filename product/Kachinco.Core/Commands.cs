using System.Collections.Immutable;

namespace Kachinco.Core;

// Typed contracts: adapters never receive mutable domain objects or a generic setter.
public abstract record EditCommand;
public sealed record CreateProject(Guid ProjectId, string Name) : EditCommand;
public sealed record CreateSequence(Guid SequenceId, string Name, SequenceSettings Settings, long DurationTicks) : EditCommand;
public sealed record RegisterMedia(MediaAsset Asset) : EditCommand;
public sealed record RelinkMedia(Guid MediaAssetId, string SourcePath, long DurationTicks,
    int? SampleRate = null, int? Channels = null) : EditCommand;
public sealed record AddTrack(Guid SequenceId, Guid TrackId, string Name, TrackKind Kind) : EditCommand;
public sealed record InsertClip(Guid SequenceId, Guid TrackId, Clip Clip) : EditCommand;
public sealed record MoveClip(Guid SequenceId, Guid ClipId, Guid TargetTrackId, long StartTicks) : EditCommand;
public sealed record TrimClip(Guid SequenceId, Guid ClipId, long StartTicks, long SourceInTicks, long DurationTicks) : EditCommand;
public sealed record SplitClip(Guid SequenceId, Guid ClipId, long SplitTicks, Guid RightClipId) : EditCommand;
public sealed record DeleteClip(Guid SequenceId, Guid ClipId) : EditCommand;
public sealed record SetClipProperties(Guid SequenceId, Guid ClipId, bool Enabled, ClipAppearance Appearance, AudioProperties Audio) : EditCommand;
public sealed record SetTrackEnabled(Guid SequenceId, Guid TrackId, bool Enabled) : EditCommand;
public sealed record ReorderTrack(Guid SequenceId, Guid TrackId, int NewIndex) : EditCommand;
public sealed record AddCaption(Guid SequenceId, Guid TrackId, Caption Caption) : EditCommand;
public sealed record DeleteCaption(Guid SequenceId, Guid CaptionId) : EditCommand;

public sealed record EditBatch(ImmutableArray<EditCommand> Commands, long? ExpectedRevision = null, bool DryRun = false);
public sealed record EditResult(bool Success, long Revision, ImmutableArray<Diagnostic> Diagnostics);
public sealed record ProjectSnapshot(long Revision, Project? Project, bool CanUndo, bool CanRedo);

internal sealed class EditRejectedException(Diagnostic diagnostic) : Exception(diagnostic.Message)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

internal static class CommandApplier
{
    private static EditRejectedException Reject(string code, string message, Guid? id = null) => new(Diagnostic.Error(code, message, id));

    public static Project Apply(Project? project, EditCommand command)
    {
        if (command is CreateProject create)
        {
            if (project is not null) throw Reject("PROJECT_EXISTS", "Use an explicit new session/project replacement first.", project.Id);
            return new(create.ProjectId, create.Name, [], []);
        }
        if (project is null) throw Reject("PROJECT_REQUIRED", "Create or open a project first.");
        if (command is RegisterMedia register) return project with { Assets = project.Assets.Add(register.Asset) };
        if (command is RelinkMedia relink)
        {
            var asset = project.Assets.FirstOrDefault(x => x.Id == relink.MediaAssetId) ??
                throw Reject("MEDIA_NOT_FOUND", "Media asset not found.", relink.MediaAssetId);
            var replacement = asset with
            {
                SourcePath = relink.SourcePath,
                DurationTicks = relink.DurationTicks,
                SampleRate = relink.SampleRate,
                Channels = relink.Channels
            };
            return project with { Assets = project.Assets.Replace(asset, replacement) };
        }
        if (command is CreateSequence sequence)
            return project with { Sequences = project.Sequences.Add(new(sequence.SequenceId, sequence.Name, sequence.Settings, sequence.DurationTicks, [])) };

        return command switch
        {
            AddTrack c => ChangeSequence(project, c.SequenceId, s => s with { Tracks = s.Tracks.Add(new(c.TrackId, c.Name, c.Kind, true, [], [])) }),
            InsertClip c => ChangeSequence(project, c.SequenceId, s => ChangeTrack(s, c.TrackId, t => t with { Clips = t.Clips.Add(c.Clip) })),
            MoveClip c => ChangeSequence(project, c.SequenceId, s =>
            {
                var (track, clip) = FindClip(s, c.ClipId);
                var removed = ChangeTrack(s, track.Id, t => t with { Clips = t.Clips.Remove(clip) });
                return ChangeTrack(removed, c.TargetTrackId, t => t with { Clips = t.Clips.Add(clip with { StartTicks = c.StartTicks }) });
            }),
            TrimClip c => ChangeSequence(project, c.SequenceId, s => ChangeClip(s, c.ClipId, clip =>
                clip with { StartTicks = c.StartTicks, SourceInTicks = c.SourceInTicks, DurationTicks = c.DurationTicks })),
            SplitClip c => ChangeSequence(project, c.SequenceId, s =>
            {
                var (track, clip) = FindClip(s, c.ClipId);
                if (c.SplitTicks <= clip.StartTicks || c.SplitTicks >= clip.EndTicks)
                    throw Reject("INVALID_SPLIT", "Split must be strictly inside the clip.", c.ClipId);
                long leftDuration = c.SplitTicks - clip.StartTicks;
                var left = clip with { DurationTicks = leftDuration };
                var right = clip with { Id = c.RightClipId, StartTicks = c.SplitTicks,
                    SourceInTicks = checked(clip.SourceInTicks + leftDuration), DurationTicks = clip.DurationTicks - leftDuration };
                return ChangeTrack(s, track.Id, t => t with { Clips = t.Clips.Replace(clip, left).Add(right) });
            }),
            DeleteClip c => ChangeSequence(project, c.SequenceId, s =>
            {
                var (track, clip) = FindClip(s, c.ClipId);
                return ChangeTrack(s, track.Id, t => t with { Clips = t.Clips.Remove(clip) });
            }),
            SetClipProperties c => ChangeSequence(project, c.SequenceId, s => ChangeClip(s, c.ClipId, clip =>
                clip with { Enabled = c.Enabled, Appearance = c.Appearance, Audio = c.Audio })),
            SetTrackEnabled c => ChangeSequence(project, c.SequenceId, s => ChangeTrack(s, c.TrackId, t => t with { Enabled = c.Enabled })),
            ReorderTrack c => ChangeSequence(project, c.SequenceId, s =>
            {
                var track = s.Tracks.FirstOrDefault(t => t.Id == c.TrackId) ?? throw Reject("TRACK_NOT_FOUND", "Track not found.", c.TrackId);
                if (c.NewIndex < 0 || c.NewIndex >= s.Tracks.Length) throw Reject("INVALID_TRACK_ORDER", "Track index is outside the sequence.", c.TrackId);
                return s with { Tracks = s.Tracks.Remove(track).Insert(c.NewIndex, track) };
            }),
            AddCaption c => ChangeSequence(project, c.SequenceId, s => ChangeTrack(s, c.TrackId, t => t with { Captions = t.Captions.Add(c.Caption) })),
            DeleteCaption c => ChangeSequence(project, c.SequenceId, s =>
            {
                var track = s.Tracks.FirstOrDefault(t => t.Captions.Any(x => x.Id == c.CaptionId)) ?? throw Reject("CAPTION_NOT_FOUND", "Caption not found.", c.CaptionId);
                return ChangeTrack(s, track.Id, t => t with { Captions = t.Captions.RemoveAll(x => x.Id == c.CaptionId) });
            }),
            _ => throw Reject("UNSUPPORTED_COMMAND", "Unknown editing command.")
        };
    }

    private static Project ChangeSequence(Project p, Guid id, Func<Sequence, Sequence> edit)
    {
        var s = p.Sequences.FirstOrDefault(x => x.Id == id) ?? throw Reject("SEQUENCE_NOT_FOUND", "Sequence not found.", id);
        return p with { Sequences = p.Sequences.Replace(s, edit(s)) };
    }
    private static Sequence ChangeTrack(Sequence s, Guid id, Func<Track, Track> edit)
    {
        var t = s.Tracks.FirstOrDefault(x => x.Id == id) ?? throw Reject("TRACK_NOT_FOUND", "Track not found.", id);
        return s with { Tracks = s.Tracks.Replace(t, edit(t)) };
    }
    private static (Track Track, Clip Clip) FindClip(Sequence s, Guid id)
    {
        foreach (var t in s.Tracks)
            if (t.Clips.FirstOrDefault(x => x.Id == id) is { } c) return (t, c);
        throw Reject("CLIP_NOT_FOUND", "Clip not found.", id);
    }
    private static Sequence ChangeClip(Sequence s, Guid id, Func<Clip, Clip> edit)
    {
        var (track, clip) = FindClip(s, id);
        return ChangeTrack(s, track.Id, t => t with { Clips = t.Clips.Replace(clip, edit(clip)) });
    }
}
