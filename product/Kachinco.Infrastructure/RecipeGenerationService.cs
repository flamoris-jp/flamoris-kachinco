using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public interface IRecipeRasterizer
{
    ValueTask<ImmutableArray<byte>> RenderAsync(RecipeIr ir, Recipe recipe, Clapper clapper, SequenceSettings settings, long localTicks, CancellationToken token);
}
public sealed record PreparedGeneration(string OutputPath, EditBatch Batch, Guid ClipId, Guid MediaAssetId);

public sealed class RecipeGenerationService(RecipeCompiler compiler, IRecipeRasterizer rasterizer, string executable = "ffmpeg")
{
    public async Task<Result<PreparedGeneration>> PrepareAsync(ProjectSnapshot snapshot, Guid sequenceId, Recipe recipe,
        string outputPath, Guid? replaceMediaId = null, CancellationToken token = default)
    {
        if (snapshot.Project is null) return Fail("PROJECT_REQUIRED", "Open a project first.");
        var sequence = snapshot.Project.Sequences.FirstOrDefault(s => s.Id == sequenceId);
        var clapper = sequence?.Clappers.FirstOrDefault(c => c.Id == recipe.ClapperId);
        if (sequence is null || clapper is null) return Fail("CLAPPER_NOT_FOUND", "Recipe needs an existing Clapper.");
        if (clapper.DurationTicks > 10 * TimelineTime.TicksPerSecond) return Fail("RECIPE_DURATION_LIMIT", "This proof supports clips up to 10 seconds.");
        var track = sequence.Tracks.FirstOrDefault(t => t.Id == clapper.TargetTrackId && t.Kind == TrackKind.Video) ??
            (clapper.TargetTrackId is null ? sequence.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video) : null);
        if (track is null) return Fail("RECIPE_TRACK_REQUIRED", "Choose a video target track.");
        var existing = replaceMediaId is { } mediaId ? snapshot.Project.Assets.FirstOrDefault(a => a.Id == mediaId) : null;
        var previous = sequence.Recipes.FirstOrDefault(r => r.Id == recipe.Id);
        if (replaceMediaId is not null && (existing?.Provenance?.RecipeId != recipe.Id || previous is null)) return Fail("RECIPE_LINEAGE_MISMATCH", "Replacement must follow the same Recipe lineage.");
        if (replaceMediaId is null && previous is not null) return Fail("RECIPE_EXISTS", "Use explicit regeneration for an existing Recipe.");
        var compiled = await compiler.CompileAsync(recipe.Source, token);
        if (!compiled.Success) return new(null, compiled.Diagnostics);
        string? temporary = null; bool published = false;
        try
        {
            string output = Path.GetFullPath(outputPath);
            if (!output.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) || File.Exists(output)) return Fail("RECIPE_OUTPUT_EXISTS", "Choose a new MOV filename; previous generations are retained for Undo.");
            // Dry-run all persistent semantics before expensive rendering. Hashes are filled after encoding.
            Guid assetId = existing?.Id ?? Guid.NewGuid(), clipId = existing is null ? Guid.NewGuid() : sequence.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.MediaAssetId == assetId)?.Id ?? Guid.Empty;
            string sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipe.Source))).ToLowerInvariant();
            EditBatch Build(string hash)
            {
                var provenance = new GeneratedProvenance(recipe.Id, recipe.Revision, sourceHash, hash);
                var commands = new List<EditCommand>();
                commands.Add(previous is null ? new AddRecipe(sequenceId, recipe) : new UpdateRecipe(sequenceId, recipe));
                if (existing is null)
                {
                    commands.Add(new RegisterMedia(new MediaAsset(assetId, Path.GetFileName(output), output, MediaKind.Mov, clapper.DurationTicks) { Provenance = provenance }));
                    commands.Add(new InsertClip(sequenceId, track.Id, new(clipId, assetId, clapper.StartTicks, 0, clapper.DurationTicks, true, ClipAppearance.Default, AudioProperties.Default)));
                }
                else
                {
                    commands.Add(new RelinkMedia(assetId, output, clapper.DurationTicks));
                    commands.Add(new SetGeneratedProvenance(assetId, provenance));
                }
                return new([.. commands], snapshot.Revision);
            }
            var validation = new EditorSession(); validation.ReplaceProject(snapshot.Project);
            var check = validation.Execute(Build(new string('0',64)) with { ExpectedRevision = validation.GetProject().Revision, DryRun = true });
            if (!check.Success) return new(null, check.Diagnostics);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, ".kachinco-recipe-" + Guid.NewGuid().ToString("N") + ".mov");
            var fps = sequence.Settings.FrameRate;
            var info = MediaProcess.StartInfo(executable, ["-v","error","-nostdin","-f","rawvideo","-pix_fmt","rgba","-s",$"{sequence.Settings.Width}x{sequence.Settings.Height}",
                "-r",$"{fps.Numerator}/{fps.Denominator}","-i","pipe:0","-an","-c:v","qtrle","-pix_fmt","argb",temporary]);
            info.RedirectStandardInput = true;
            using var process = new Process { StartInfo = info }; process.Start();
            using var cancel = token.Register(() => MediaProcess.Kill(process));
            var error = MediaProcess.DrainErrorAsync(process.StandardError, token);
            try
            {
                long frames = TimelineTime.FrameCount(clapper.DurationTicks, fps);
                for (long i = 0; i < frames; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var pixels = await rasterizer.RenderAsync(compiled.Value!, recipe, clapper, sequence.Settings, TimelineTime.FrameToTicks(i, fps), token);
                    if (pixels.Length != checked(sequence.Settings.Width * sequence.Settings.Height * 4)) throw new InvalidDataException("Invalid Recipe frame dimensions.");
                    await process.StandardInput.BaseStream.WriteAsync(pixels.ToArray(), token);
                }
                process.StandardInput.Close(); await process.WaitForExitAsync(token);
                if (process.ExitCode != 0) throw new InvalidDataException("Recipe encode failed: " + await error);
            }
            finally { MediaProcess.Kill(process); try { await error; } catch (OperationCanceledException) { } }
            await using var stream = File.OpenRead(temporary);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            await stream.DisposeAsync(); token.ThrowIfCancellationRequested();
            File.Move(temporary, output, false); published = true;
            return Result<PreparedGeneration>.Ok(new(output, Build(hash), clipId, assetId));
        }
        catch (OperationCanceledException) { return Fail("CANCELLED", "Recipe generation cancelled."); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { return Fail("RECIPE_GENERATION_FAILED", e.Message); }
        finally { if (!published && temporary is not null) try { File.Delete(temporary); } catch (IOException) { } }
    }
    private static Result<PreparedGeneration> Fail(string code, string message) => Result<PreparedGeneration>.Fail(Diagnostic.Error(code, message));
}
