using System.Collections.Immutable;

namespace Kachinco.Core;

public static class ProjectValidator
{
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    public static bool ContainsId(Project project, Guid id)
    {
        if (project.Id == id || project.Assets.Any(x => x.Id == id) || project.Sequences.Any(x => x.Id == id)) return true;
        if (project.Sequences.Any(s => s.Clappers.Any(c => c.Id == id) || s.Recipes.Any(r => r.Id == id))) return true;
        return project.Sequences.SelectMany(x => x.Tracks).Any(track => track.Id == id ||
            track.Clips.Any(clip => clip.Id == id) || track.Captions.Any(caption => caption.Id == id));
    }

    public static ImmutableArray<Diagnostic> Validate(Project? project)
    {
        var errors = ImmutableArray.CreateBuilder<Diagnostic>();
        var ids = new HashSet<Guid>();
        void Error(string code, string message, Guid? id = null, string? path = null) =>
            errors.Add(Diagnostic.Error(code, message, id, path));
        void Identity(Guid id, string name, string path)
        {
            if (id == Guid.Empty || !ids.Add(id)) Error("INVALID_ID", "ID must be nonempty and globally unique.", id, path);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256) Error("INVALID_NAME", "Name must contain 1–256 characters.", id, path);
        }
        if (project is null) return [Diagnostic.Error("PROJECT_REQUIRED", "Project is required.")];
        Identity(project.Id, project.Name, "project");
        if (project.Assets.IsDefault || project.Sequences.IsDefault)
            return [Diagnostic.Error("INVALID_COLLECTION", "Assets and sequences must be initialized.", project.Id)];
        var assets = new Dictionary<Guid, MediaAsset>();
        foreach (var asset in project.Assets)
        {
            if (asset is null) { Error("INVALID_ASSET", "Null media asset."); continue; }
            Identity(asset.Id, asset.Name, "assets");
            assets.TryAdd(asset.Id, asset);
            if (!Enum.IsDefined(asset.Kind)) Error("INVALID_MEDIA_KIND", "Unknown media kind.", asset.Id);
            if (!TimelineTime.ValidRange(0, asset.DurationTicks)) Error("INVALID_MEDIA_DURATION", "Media duration must be positive.", asset.Id);
            if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.SourcePath.Length > 32768 ||
                asset.SourcePath.IndexOfAny(['\0', '\r', '\n']) >= 0 || asset.SourcePath.Contains("://", StringComparison.Ordinal) ||
                !MediaSourceFormats.Supports(asset.Kind, asset.SourcePath))
                Error("UNSUPPORTED_MEDIA_SOURCE", "Register a local MOV/MP4 video or WAV/MP3/M4A audio path matching its media kind.", asset.Id, "sourcePath");
            if (asset.SampleRate is <= 0 or > 384000 || asset.Channels is <= 0 or > 32)
                Error("INVALID_AUDIO_METADATA", "Invalid sample rate or channel count.", asset.Id);
        }
        foreach (var sequence in project.Sequences)
        {
            if (sequence is null) { Error("INVALID_SEQUENCE", "Null sequence."); continue; }
            Identity(sequence.Id, sequence.Name, "sequences");
            var settings = sequence.Settings;
            if (settings is null || !settings.FrameRate.IsValid ||
                !((settings.Width == 1920 && settings.Height == 1080) || (settings.Width == 1080 && settings.Height == 1920)))
                Error("INVALID_SEQUENCE_SETTINGS", "Use a landscape/portrait preset and reduced rational FPS between 1 and 240.", sequence.Id);
            if (!TimelineTime.ValidRange(0, sequence.DurationTicks)) Error("INVALID_SEQUENCE_DURATION", "Sequence duration must be positive.", sequence.Id);
            if (sequence.Tracks.IsDefault) { Error("INVALID_COLLECTION", "Tracks must be initialized.", sequence.Id); continue; }
            if (sequence.Clappers.IsDefault || sequence.Recipes.IsDefault) { Error("INVALID_AUTHORING", "Authoring arrays must be initialized."); continue; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clapper in sequence.Clappers)
            {
                if (clapper is null) { Error("INVALID_CLAPPER", "Clapper is null."); continue; }
                Identity(clapper.Id, clapper.Name, "clappers");
                if (!names.Add(clapper.Name)) Error("CLAPPER_NAME_CONFLICT", "Clapper names must be unique within the sequence.", clapper.Id);
                if (!TimelineTime.ValidRange(clapper.StartTicks, clapper.DurationTicks, sequence.DurationTicks)) Error("INVALID_CLAPPER_RANGE", "Clapper must fit the sequence.", clapper.Id);
                if (clapper.Notes is null || clapper.Notes.Length > 65536) Error("INVALID_CLAPPER_NOTES", "Invalid Clapper notes.", clapper.Id);
                if (clapper.TargetTrackId is { } target && !sequence.Tracks.Any(t => t.Id == target)) Error("CLAPPER_TRACK_MISSING", "Target track not found.", clapper.Id);
                if (clapper.SourceClipId is { } clip && !sequence.Tracks.Any(t => t.Clips.Any(c => c.Id == clip))) Error("CLAPPER_CLIP_MISSING", "Source clip not found.", clapper.Id);
                if (clapper.Geometry is { } g && (settings is null || !Enum.IsDefined(g.Kind) || !double.IsFinite(g.X) || !double.IsFinite(g.Y) ||
                    !double.IsFinite(g.Width) || !double.IsFinite(g.Height) || g.X < 0 || g.Y < 0 || g.X > settings.Width || g.Y > settings.Height ||
                    (g.Kind == ClapperGeometryKind.Point ? g.Width != 0 || g.Height != 0 : g.Width <= 0 || g.Height <= 0 || g.Width > settings.Width - g.X || g.Height > settings.Height - g.Y)))
                    Error("INVALID_CLAPPER_GEOMETRY", "Use point/rectangle in project pixels.", clapper.Id);
            }
            foreach (var recipe in sequence.Recipes)
            {
                if (recipe is null) { Error("INVALID_RECIPE", "Recipe is null."); continue; }
                Identity(recipe.Id, "recipe", "recipes");
                if (!sequence.Clappers.Any(c => c.Id == recipe.ClapperId)) Error("RECIPE_CLAPPER_MISSING", "Recipe Clapper not found.", recipe.Id);
                if (string.IsNullOrWhiteSpace(recipe.Source) || recipe.Source.Length > 65536 || recipe.Revision < 1 || recipe.ApiVersion != "1" || recipe.RendererVersion != "1")
                    Error("INVALID_RECIPE", "Recipe source/version is unsupported.", recipe.Id);
            }
            foreach (var track in sequence.Tracks)
            {
                if (track is null) { Error("INVALID_TRACK", "Null track."); continue; }
                Identity(track.Id, track.Name, "tracks");
                if (!Enum.IsDefined(track.Kind)) Error("INVALID_TRACK_KIND", "Unknown track kind.", track.Id);
                if (track.Clips.IsDefault || track.Captions.IsDefault) { Error("INVALID_COLLECTION", "Track items must be initialized.", track.Id); continue; }
                if ((track.Kind == TrackKind.Subtitle && track.Clips.Length > 0) ||
                    (track.Kind != TrackKind.Subtitle && track.Captions.Length > 0))
                    Error("TRACK_ITEM_MISMATCH", "Captions belong to subtitle tracks; media clips do not.", track.Id);
                foreach (var clip in track.Clips)
                {
                    if (clip is null) { Error("INVALID_CLIP", "Null clip."); continue; }
                    Identity(clip.Id, "clip", "clips");
                    if (!TimelineTime.ValidRange(clip.StartTicks, clip.DurationTicks, sequence.DurationTicks))
                        Error("INVALID_TIMELINE_RANGE", "Clip must fit inside the sequence.", clip.Id);
                    if (!assets.TryGetValue(clip.MediaAssetId, out var media))
                        Error("MEDIA_NOT_FOUND", "Clip references missing media.", clip.Id, "mediaAssetId");
                    else
                    {
                        if (!TimelineTime.ValidRange(clip.SourceInTicks, clip.DurationTicks, media.DurationTicks))
                            Error("INVALID_SOURCE_RANGE", "Source range must fit inside the registered media.", clip.Id);
                        if ((track.Kind == TrackKind.Video && media.Kind != MediaKind.Mov) ||
                            (track.Kind == TrackKind.Audio && media.Kind != MediaKind.Wav))
                            Error("TRACK_MEDIA_MISMATCH", "Video tracks accept video assets; audio tracks accept audio assets.", clip.Id);
                    }
                    var appearance = clip.Appearance;
                    var transform = appearance?.Transform;
                    if (appearance is null || transform is null || !Enum.IsDefined(appearance.Blend) ||
                        !double.IsFinite(appearance.Opacity) || appearance.Opacity is < 0 or > 1 ||
                        !double.IsFinite(transform.X) || !double.IsFinite(transform.Y) ||
                        !double.IsFinite(transform.RotationDegrees) || !double.IsFinite(transform.ScaleX) ||
                        !double.IsFinite(transform.ScaleY) || transform.ScaleX <= 0 || transform.ScaleY <= 0)
                        Error("INVALID_APPEARANCE", "Transform must be finite, scales positive and opacity within [0,1].", clip.Id);
                    if (clip.Audio is null || !double.IsFinite(clip.Audio.Gain) || clip.Audio.Gain is < 0 or > 16)
                        Error("INVALID_AUDIO_PROPERTIES", "Gain must be finite and within [0,16].", clip.Id);
                }
                foreach (var caption in track.Captions)
                {
                    if (caption is null) { Error("INVALID_CAPTION", "Null caption."); continue; }
                    Identity(caption.Id, "caption", "captions");
                    if (!TimelineTime.ValidRange(caption.StartTicks, caption.DurationTicks, sequence.DurationTicks))
                        Error("INVALID_CAPTION_RANGE", "Caption must fit inside the sequence.", caption.Id);
                    if (string.IsNullOrWhiteSpace(caption.Text) || caption.Text.Length > 65536)
                        Error("INVALID_CAPTION_TEXT", "Caption text must contain 1–65536 characters.", caption.Id);
                }
            }
        }
        foreach (var asset in project.Assets.Where(a => a?.Provenance is not null))
        {
            var provenance = asset.Provenance!;
            var recipe = project.Sequences.Where(s => s is not null && !s.Recipes.IsDefault).SelectMany(s => s.Recipes).FirstOrDefault(r => r is not null && r.Id == provenance.RecipeId);
            if (recipe is null || provenance.RecipeRevision < 1 || provenance.RecipeRevision > recipe.Revision ||
                !Hash(provenance.SourceSha256) || !Hash(provenance.OutputSha256)) Error("INVALID_PROVENANCE", "Generated provenance is invalid.", asset.Id);
        }
        return errors.ToImmutable();
    }
}
