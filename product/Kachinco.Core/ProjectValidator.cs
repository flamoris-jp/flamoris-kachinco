using System.Collections.Immutable;

namespace Kachinco.Core;

public static class ProjectValidator
{
    public static bool ContainsId(Project project, Guid id)
    {
        if (project.Id == id || project.Assets.Any(x => x.Id == id) || project.Sequences.Any(x => x.Id == id)) return true;
        if (project.Sequences.Any(s => s.Clappers.Any(c => c.Id == id) || s.Recipes.Any(r => r.Id == id))) return true;
        return project.Sequences.SelectMany(x => x.Tracks).Any(track => track.Id == id ||
            track.Clips.Any(clip => clip.Id == id) || track.Captions.Any(caption => caption.Id == id));
    }

    public static ImmutableArray<Diagnostic> Validate(Project? project) => NativeProjectCodec.Validate(project);
}
