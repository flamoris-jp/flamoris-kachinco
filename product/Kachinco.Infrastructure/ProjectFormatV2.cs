using Kachinco.Core;

namespace Kachinco.Infrastructure;

internal sealed record EnvelopeV2(string Format, int SchemaVersion, long Timebase, ProjectV1 Project,
    SequenceAuthoringV2[] Authoring, AssetProvenanceV2[] GeneratedAssets);
internal sealed record SequenceAuthoringV2(Guid SequenceId, ClapperV2[] Clappers, RecipeV2[] Recipes);
internal sealed record ClapperV2(Guid Id, string Name, long StartTicks, long DurationTicks, GeometryV2? Geometry, Guid? TargetTrackId, Guid? SourceClipId, string Notes);
internal sealed record GeometryV2(ClapperGeometryKind Kind, double X, double Y, double Width, double Height);
internal sealed record RecipeV2(Guid Id, Guid ClapperId, string Source, int Revision, int Seed, string ApiVersion, string RendererVersion);
internal sealed record AssetProvenanceV2(Guid MediaAssetId, Guid RecipeId, int RecipeRevision, string SourceSha256, string OutputSha256);

internal static class FormatV2
{
    internal static EnvelopeV2 Encode(Project p) => new(ProjectJson.Format, 2, TimelineTime.TicksPerSecond, FormatV1.Encode(p).Project,
        p.Sequences.OrderBy(s => s.Id).Select(s => new SequenceAuthoringV2(s.Id,
            s.Clappers.OrderBy(c => c.Id).Select(c => new ClapperV2(c.Id,c.Name,c.StartTicks,c.DurationTicks,
                c.Geometry is { } g ? new(g.Kind,g.X,g.Y,g.Width,g.Height) : null,c.TargetTrackId,c.SourceClipId,c.Notes)).ToArray(),
            s.Recipes.OrderBy(r => r.Id).Select(r => new RecipeV2(r.Id,r.ClapperId,r.Source,r.Revision,r.Seed,r.ApiVersion,r.RendererVersion)).ToArray())).ToArray(),
        p.Assets.Where(a => a.Provenance is not null).OrderBy(a => a.Id).Select(a => new AssetProvenanceV2(a.Id,
            a.Provenance!.RecipeId,a.Provenance.RecipeRevision,a.Provenance.SourceSha256,a.Provenance.OutputSha256)).ToArray());
    internal static Project Decode(EnvelopeV2 e)
    {
        if (e.Authoring is null || e.GeneratedAssets is null) throw new FormatException("Authoring fields required.");
        var project = FormatV1.Decode(new(e.Format, 1, e.Timebase, e.Project));
        if (e.Authoring.Any(a => a is null || a.Clappers is null || a.Recipes is null) || e.Authoring.Length != project.Sequences.Length ||
            e.Authoring.Select(a => a.SequenceId).Distinct().Count() != e.Authoring.Length || e.Authoring.Any(a => !project.Sequences.Any(s => s.Id == a.SequenceId)))
            throw new FormatException("Exactly one authoring entry per sequence is required.");
        if (e.GeneratedAssets.Any(a => a is null || !project.Assets.Any(x => x.Id == a.MediaAssetId)) || e.GeneratedAssets.Select(a => a.MediaAssetId).Distinct().Count() != e.GeneratedAssets.Length)
            throw new FormatException("Invalid generated-asset identities.");
        return project with
        {
            Sequences = [.. project.Sequences.Select(s =>
            {
                var a = e.Authoring.Single(a => a.SequenceId == s.Id);
                if (a.Clappers.Any(c => c is null) || a.Recipes.Any(r => r is null)) throw new FormatException("Null authoring item.");
                return s with { Clappers = [.. a.Clappers.Select(c => new Clapper(c.Id,c.Name,c.StartTicks,c.DurationTicks,
                    c.Geometry is { } g ? new(g.Kind,g.X,g.Y,g.Width,g.Height) : null,c.TargetTrackId,c.SourceClipId,c.Notes))],
                    Recipes = [.. a.Recipes.Select(r => new Recipe(r.Id,r.ClapperId,r.Source,r.Revision,r.Seed,r.ApiVersion,r.RendererVersion))] };
            })],
            Assets = [.. project.Assets.Select(a => e.GeneratedAssets.FirstOrDefault(g => g.MediaAssetId == a.Id) is { } g ? a with
                { Provenance = new(g.RecipeId,g.RecipeRevision,g.SourceSha256,g.OutputSha256) } : a)]
        };
    }
}
