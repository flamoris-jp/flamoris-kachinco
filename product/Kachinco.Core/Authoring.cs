using System.Collections.Immutable;

namespace Kachinco.Core;

public enum ClapperGeometryKind { Point, Rectangle }
public sealed record ClapperGeometry(ClapperGeometryKind Kind, double X, double Y, double Width, double Height);
public sealed record Clapper(Guid Id, string Name, long StartTicks, long DurationTicks,
    ClapperGeometry? Geometry, Guid? TargetTrackId, Guid? SourceClipId, string Notes);
public sealed record Recipe(Guid Id, Guid ClapperId, string Source, int Revision, int Seed, string ApiVersion, string RendererVersion);
public sealed record GeneratedProvenance(Guid RecipeId, int RecipeRevision, string SourceSha256, string OutputSha256);
public sealed record AddClapper(Guid SequenceId, Clapper Clapper) : EditCommand;
public sealed record UpdateClapper(Guid SequenceId, Clapper Clapper) : EditCommand;
public sealed record DeleteClapper(Guid SequenceId, Guid ClapperId) : EditCommand;
public sealed record AddRecipe(Guid SequenceId, Recipe Recipe) : EditCommand;
public sealed record UpdateRecipe(Guid SequenceId, Recipe Recipe) : EditCommand;
public sealed record SetGeneratedProvenance(Guid MediaAssetId, GeneratedProvenance Provenance) : EditCommand;

public static class ClapperQueries
{
    public static Result<Clapper> Resolve(Project project, Guid sequenceId, string name)
    {
        var sequence = project.Sequences.FirstOrDefault(s => s.Id == sequenceId);
        var found = sequence?.Clappers.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        return found is null ? Result<Clapper>.Fail(Diagnostic.Error("CLAPPER_NOT_FOUND", "Named Clapper not found.")) : Result<Clapper>.Ok(found);
    }
}
