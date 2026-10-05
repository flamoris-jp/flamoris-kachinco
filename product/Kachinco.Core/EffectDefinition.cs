using System.Collections.Immutable;

namespace Kachinco.Core;

// Library assets are compositions, never sessions, evaluators or rendered media.
public sealed record EffectSample(double Time, double Value);
public sealed record EffectCurve(VisualProperty Property, ImmutableArray<EffectSample> Samples);
public sealed record EffectParameters(double Intensity = 1, double DurationScale = 1);
public sealed record EffectDefinition(int SchemaVersion, Guid Id, string Name, string Description,
    int Version, string ApiVersion, ImmutableArray<EffectCurve> Curves, EffectParameters Defaults, string? RecipeSource = null, int Seed = 0);

public static class EffectComposition
{
    public static ImmutableArray<Diagnostic> Validate(EffectDefinition? effect)
    {
        if (effect is null || effect.SchemaVersion != 1 || effect.ApiVersion != "1" || effect.Id == Guid.Empty || effect.Version < 1 ||
            string.IsNullOrWhiteSpace(effect.Name) || effect.Name.Length > 256 || effect.Description is null || effect.Description.Length > 4096 ||
            effect.Defaults is null || !ValidParameters(effect.Defaults) || effect.Curves.IsDefault || effect.Curves.Length > 6 ||
            (effect.Curves.IsEmpty == string.IsNullOrWhiteSpace(effect.RecipeSource)) || effect.RecipeSource?.Length > 65536 || effect.RecipeSource is not null && string.IsNullOrWhiteSpace(effect.RecipeSource))
            return [Diagnostic.Error("INVALID_EFFECT", "Use a v1 effect with either visual automation or restricted Recipe source.")];
        var properties = new HashSet<VisualProperty>();
        foreach (var curve in effect.Curves)
        {
            if (curve is null || !Enum.IsDefined(curve.Property) || !properties.Add(curve.Property) ||
                curve.Samples.IsDefaultOrEmpty || curve.Samples.Length > 4096)
                return [Diagnostic.Error("INVALID_EFFECT_CURVE", "Use distinct supported properties with 1–4096 samples.")];
            double previous = -1;
            foreach (var sample in curve.Samples)
            {
                if (sample is null || !double.IsFinite(sample.Time) || sample.Time < 0 || sample.Time > 1 || sample.Time <= previous ||
                    !double.IsFinite(sample.Value) || curve.Property is VisualProperty.ScaleX or VisualProperty.ScaleY && sample.Value <= 0 ||
                    curve.Property == VisualProperty.Opacity && (sample.Value < 0 || sample.Value > 1))
                    return [Diagnostic.Error("INVALID_EFFECT_SAMPLE", "Use ordered time fractions [0,1], finite values, positive scales and opacity [0,1].")];
                previous = sample.Time;
            }
        }
        return [];
    }

    public static bool ValidParameters(EffectParameters parameters) => double.IsFinite(parameters.Intensity) &&
        parameters.Intensity is >= 0 and <= 1 && double.IsFinite(parameters.DurationScale) && parameters.DurationScale is > 0 and <= 100;

    public static double Constant(ClipAppearance appearance, VisualProperty property) => property switch
    {
        VisualProperty.X => appearance.Transform.X, VisualProperty.Y => appearance.Transform.Y,
        VisualProperty.ScaleX => appearance.Transform.ScaleX, VisualProperty.ScaleY => appearance.Transform.ScaleY,
        VisualProperty.RotationDegrees => appearance.Transform.RotationDegrees, VisualProperty.Opacity => appearance.Opacity,
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };

    // Capture the visible interval via the native evaluator, including trim/split guard points.
    public static Result<EffectDefinition> Capture(ProjectSnapshot snapshot, Guid sequenceId, Guid clipId, string name)
    {
        var track = snapshot.Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId)?.Tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == clipId));
        var clip = track?.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip is null || track!.Kind != TrackKind.Video) return Fail<EffectDefinition>("EFFECT_TARGET_REQUIRED", "Select a visual clip.");
        var created = TimelineEvaluator.Create(snapshot.Project!, sequenceId);
        if (!created.Success) return new(null, created.Diagnostics);
        using var evaluator = created.Value!;
        var first = evaluator.Evaluate(clip.StartTicks).Value!.VideoLayers.FirstOrDefault(l => l.ClipId == clipId);
        var last = evaluator.Evaluate(clip.EndTicks - 1).Value!.VideoLayers.FirstOrDefault(l => l.ClipId == clipId);
        if (first is null || last is null) return Fail<EffectDefinition>("EFFECT_TARGET_DISABLED", "Enable the visual clip and track before capture.");
        var curves = ImmutableArray.CreateBuilder<EffectCurve>();
        var properties = clip.Appearance.Automation.IsEmpty ? Enum.GetValues<VisualProperty>() : clip.Appearance.Automation.Select(c => c.Property).ToArray();
        foreach (var property in properties)
        {
            var samples = new List<EffectSample> { new(0, Constant(first.Appearance, property)) };
            var curve = clip.Appearance.Automation.FirstOrDefault(c => c.Property == property);
            if (curve is not null) samples.AddRange(curve.Points.Where(p => p.Tick > 0 && p.Tick < clip.DurationTicks)
                .Select(p => new EffectSample((double)p.Tick / clip.DurationTicks, p.Value)));
            samples.Add(new(1, Constant(last.Appearance, property)));
            curves.Add(new(property, [.. samples]));
        }
        var effect = new EffectDefinition(1, Guid.NewGuid(), name, "", 1, "1", curves.ToImmutable(), new());
        var errors = Validate(effect);
        return errors.IsEmpty ? Result<EffectDefinition>.Ok(effect) : new(null, errors);
    }

    public static Result<EditBatch> Plan(ProjectSnapshot snapshot, Guid sequenceId, Guid clipId, EffectDefinition effect,
        EffectParameters? parameters = null)
    {
        var errors = Validate(effect);
        if (!errors.IsEmpty) return new(null, errors);
        parameters ??= effect.Defaults;
        if (!ValidParameters(parameters)) return Fail<EditBatch>("INVALID_EFFECT_PARAMETERS", "Intensity must be [0,1] and duration scale (0,100].");
        if (effect.RecipeSource is not null) return Fail<EditBatch>("EFFECT_RECIPE_TARGET_REQUIRED", "Bind Recipe effects to an explicit Clapper, then render through ordinary Recipe generation.");
        var track = snapshot.Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId)?.Tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == clipId));
        var clip = track?.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip is null || track!.Kind != TrackKind.Video) return Fail<EditBatch>("EFFECT_TARGET_REQUIRED", "Choose a compatible visual clip.");
        try
        {
            var commands = ImmutableArray.CreateBuilder<EditCommand>();
            foreach (var curve in effect.Curves)
            {
                var points = new SortedDictionary<long, double>();
                double baseline = Constant(clip.Appearance, curve.Property);
                foreach (var sample in curve.Samples)
                {
                    long tick = checked((long)decimal.Round((decimal)sample.Time * clip.DurationTicks * (decimal)parameters.DurationScale));
                    // Blend without subtraction overflow. Collapsed ticks use the last ordered sample.
                    points[tick] = baseline * (1 - parameters.Intensity) + sample.Value * parameters.Intensity;
                }
                commands.Add(new SetClipPropertyCurve(sequenceId, clipId, new(curve.Property, [.. points.Select(p => new PropertyPoint(Guid.NewGuid(), p.Key, p.Value))])));
            }
            var batch = new EditBatch(commands.ToImmutable(), snapshot.Revision);
            var projected = NativeProjectCodec.ProjectCommands(snapshot.Project!, batch.Commands.ToArray());
            return projected.Success ? Result<EditBatch>.Ok(batch) : new(null, projected.Diagnostics);
        }
        catch (OverflowException) { return Fail<EditBatch>("EFFECT_TIME_OVERFLOW", "Effect duration exceeds supported ticks."); }
    }

    public static Result<EditBatch> PlanRecipe(ProjectSnapshot snapshot, Guid sequenceId, Guid clapperId, EffectDefinition effect)
    {
        var errors = Validate(effect);
        if (!errors.IsEmpty) return new(null, errors);
        if (effect.RecipeSource is null) return Fail<EditBatch>("EFFECT_RECIPE_REQUIRED", "Choose a Recipe library item.");
        if (snapshot.Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId)?.Clappers.Any(c => c.Id == clapperId) != true)
            return Fail<EditBatch>("CLAPPER_NOT_FOUND", "Choose an existing Clapper.");
        var command = new AddRecipe(sequenceId, new(Guid.NewGuid(), clapperId, effect.RecipeSource, 1, effect.Seed, "1", "1"));
        var projected = NativeProjectCodec.ProjectCommands(snapshot.Project!, command);
        return projected.Success ? Result<EditBatch>.Ok(new([command], snapshot.Revision)) : new(null, projected.Diagnostics);
    }
    private static Result<T> Fail<T>(string code, string message) => Result<T>.Fail(Diagnostic.Error(code, message));
}
