using System.Collections.Immutable;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed record MediaAvailability(Guid MediaAssetId, string SourcePath, string? ResolvedPath,
    bool IsAvailable, ImmutableArray<Diagnostic> Diagnostics);

public static class MediaReferenceResolver
{
    public static ImmutableArray<MediaAvailability> Inspect(Project project, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? projectDirectory = null;
        if (!string.IsNullOrWhiteSpace(projectFilePath))
        {
            try { projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        var result = ImmutableArray.CreateBuilder<MediaAvailability>();
        foreach (var asset in TimelineQueries.ListMediaAssets(project))
        {
            string? resolved = null;
            Diagnostic? diagnostic = null;
            try
            {
                if (Path.IsPathFullyQualified(asset.SourcePath)) resolved = Path.GetFullPath(asset.SourcePath);
                else if (projectDirectory is not null) resolved = Path.GetFullPath(asset.SourcePath, projectDirectory);
                else diagnostic = Warning("MEDIA_REFERENCE_UNRESOLVED",
                    "Save the project before resolving project-relative media.", asset.Id, asset.SourcePath);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostic = Warning("INVALID_MEDIA_REFERENCE", "The stored media path is invalid.", asset.Id, asset.SourcePath);
            }

            bool available = resolved is not null && File.Exists(resolved);
            if (!available && diagnostic is null)
                diagnostic = Warning("MEDIA_MISSING", "The source media file is missing.", asset.Id, resolved ?? asset.SourcePath);
            result.Add(new(asset.Id, asset.SourcePath, resolved, available,
                diagnostic is null ? [] : [diagnostic]));
        }
        return result.ToImmutable();
    }

    private static Diagnostic Warning(string code, string message, Guid id, string path) =>
        new(code, DiagnosticSeverity.Warning, message, id, path);
}

public sealed class MediaRelinkService(IMediaProbe probe)
{
    public async Task<Result<RelinkMedia>> PrepareAsync(Project project, Guid mediaAssetId,
        string replacementPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var existing = project.Assets.FirstOrDefault(x => x.Id == mediaAssetId);
        if (existing is null)
            return Result<RelinkMedia>.Fail(Diagnostic.Error("MEDIA_NOT_FOUND", "Media asset not found.", mediaAssetId));

        var probed = await probe.ProbeAsync(replacementPath, cancellationToken);
        if (!probed.Success) return new(null, probed.Diagnostics);
        if (probed.Value!.Kind != existing.Kind)
            return Result<RelinkMedia>.Fail(Diagnostic.Error("INCOMPATIBLE_RELINK",
                "Replacement media must have the same video/audio kind.", mediaAssetId, replacementPath));

        var command = new RelinkMedia(mediaAssetId, probed.Value.SourcePath, probed.Value.DurationTicks,
            probed.Value.SampleRate, probed.Value.Channels);
        var replacement = existing with
        {
            SourcePath = command.SourcePath,
            DurationTicks = command.DurationTicks,
            SampleRate = command.SampleRate,
            Channels = command.Channels
        };
        var candidate = project with { Assets = project.Assets.Replace(existing, replacement) };
        var diagnostics = ProjectValidator.Validate(candidate);
        if (!diagnostics.IsEmpty)
        {
            var relinkDiagnostics = diagnostics.Select(d => d.Code is "INVALID_SOURCE_RANGE"
                ? d with { Code = "INCOMPATIBLE_RELINK", Message = "Replacement media is too short for an existing clip source range." }
                : d).ToImmutableArray();
            return new(null, relinkDiagnostics);
        }
        return Result<RelinkMedia>.Ok(command);
    }
}
