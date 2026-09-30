using System.Collections.Immutable;

using Kachinco.Core;

namespace Kachinco.Tests.Oracles;

internal sealed class ManagedEditRejectedException(Diagnostic diagnostic) : Exception(diagnostic.Message)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

internal static class ManagedCommandOracle
{
    private static ManagedEditRejectedException Reject(string code, string message, Guid? id = null) => new(Diagnostic.Error(code, message, id));

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
        if (command is SetGeneratedProvenance provenance)
        {
            var asset = project.Assets.FirstOrDefault(a => a.Id == provenance.MediaAssetId) ?? throw Reject("MEDIA_NOT_FOUND", "Media not found.");
            return project with { Assets = project.Assets.Replace(asset, asset with { Provenance = provenance.Provenance ?? throw Reject("INVALID_PROVENANCE", "Provenance required.") }) };
        }
        if (command is CreateSequence sequence)
            return project with { Sequences = project.Sequences.Add(new(sequence.SequenceId, sequence.Name, sequence.Settings, sequence.DurationTicks, [])) };

        return command switch
        {
            AddClapper c => ChangeSequence(project, c.SequenceId, s => s with { Clappers = s.Clappers.Add(c.Clapper) }),
            UpdateClapper c => ChangeSequence(project, c.SequenceId, s => s with { Clappers = s.Clappers.Replace(
                s.Clappers.FirstOrDefault(x => x.Id == (c.Clapper ?? throw Reject("INVALID_CLAPPER", "Clapper required.")).Id) ?? throw Reject("CLAPPER_NOT_FOUND", "Clapper not found."), c.Clapper) }),
            DeleteClapper c => ChangeSequence(project, c.SequenceId, s =>
            {
                var clapper = s.Clappers.FirstOrDefault(x => x.Id == c.ClapperId) ?? throw Reject("CLAPPER_NOT_FOUND", "Clapper not found.");
                return s with { Clappers = s.Clappers.Remove(clapper) };
            }),
            AddRecipe c => ChangeSequence(project, c.SequenceId, s => s with { Recipes = s.Recipes.Add(c.Recipe) }),
            UpdateRecipe c => ChangeSequence(project, c.SequenceId, s =>
            {
                var previous = s.Recipes.FirstOrDefault(x => x.Id == (c.Recipe ?? throw Reject("INVALID_RECIPE", "Recipe required.")).Id) ?? throw Reject("RECIPE_NOT_FOUND", "Recipe not found.");
                if (c.Recipe.Revision != previous.Revision + 1) throw Reject("RECIPE_REVISION_CONFLICT", "Recipe revision must advance once.");
                return s with { Recipes = s.Recipes.Replace(previous, c.Recipe) };
            }),
            SetSequenceDuration c => ChangeSequence(project, c.SequenceId, s => s with { DurationTicks = c.DurationTicks }),
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
            UpdateCaption c => ChangeSequence(project, c.SequenceId, s =>
            {
                var track = s.Tracks.FirstOrDefault(t => t.Captions.Any(x => x.Id == c.CaptionId)) ?? throw Reject("CAPTION_NOT_FOUND", "Caption not found.", c.CaptionId);
                var caption = track.Captions.First(x => x.Id == c.CaptionId);
                return ChangeTrack(s, track.Id, t => t with { Captions = t.Captions.Replace(caption,
                    caption with { StartTicks = c.StartTicks, DurationTicks = c.DurationTicks, Text = c.Text, Enabled = c.Enabled }) });
            }),
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
