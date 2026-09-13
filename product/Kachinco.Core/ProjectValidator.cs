using System.Collections.Immutable;

namespace Kachinco.Core;

public static class ProjectValidator
{
    public static bool ContainsId(Project project, Guid id)
    {
        if (project.Id == id || project.Assets.Any(x => x.Id == id) || project.Sequences.Any(x => x.Id == id)) return true;
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
            var extension = asset.Kind == MediaKind.Mov ? ".mov" : ".wav";
            if (string.IsNullOrWhiteSpace(asset.SourcePath) || asset.SourcePath.Length > 32768 ||
                asset.SourcePath.IndexOfAny(['\0', '\r', '\n']) >= 0 || asset.SourcePath.Contains("://", StringComparison.Ordinal) ||
                !asset.SourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                Error("UNSUPPORTED_MEDIA_SOURCE", "Register a local MOV/WAV path matching its media kind.", asset.Id, "sourcePath");
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
                            Error("TRACK_MEDIA_MISMATCH", "Video tracks accept MOV; audio tracks accept WAV.", clip.Id);
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
        return errors.ToImmutable();
    }
}
