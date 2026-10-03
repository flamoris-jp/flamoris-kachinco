namespace Kachinco.Core;

public enum ClipNumericProperty { Opacity, X, Y, ScaleX, ScaleY, Rotation, Gain }

// A transient authoring projection, not an editing session or history owner.
public sealed class ClipPropertyEdit
{
    public ProjectSnapshot Baseline { get; }
    public Guid SequenceId { get; }
    public Clip Clip { get; }
    public ClipNumericProperty Property { get; }
    public ClipPropertyEdit(ProjectSnapshot baseline, Guid sequenceId, Guid clipId, ClipNumericProperty property)
    {
        Baseline = baseline; SequenceId = sequenceId; Property = property;
        Clip = baseline.Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId)?.Tracks
            .SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId) ?? throw new ArgumentException("Clip not found.");
    }
    public SetClipProperties Command(double value)
    {
        var t = Clip.Appearance.Transform;
        var appearance = Property switch
        {
            ClipNumericProperty.Opacity => Clip.Appearance with { Opacity = value },
            ClipNumericProperty.X => Clip.Appearance with { Transform = t with { X = value } },
            ClipNumericProperty.Y => Clip.Appearance with { Transform = t with { Y = value } },
            ClipNumericProperty.ScaleX => Clip.Appearance with { Transform = t with { ScaleX = value } },
            ClipNumericProperty.ScaleY => Clip.Appearance with { Transform = t with { ScaleY = value } },
            ClipNumericProperty.Rotation => Clip.Appearance with { Transform = t with { RotationDegrees = value } },
            _ => Clip.Appearance
        };
        return new(SequenceId, Clip.Id, Clip.Enabled, appearance,
            Property == ClipNumericProperty.Gain ? Clip.Audio with { Gain = value } : Clip.Audio);
    }
    public bool IsUnchanged(double value)
    {
        var command = Command(value);
        return command.Appearance == Clip.Appearance && command.Audio == Clip.Audio;
    }
    public EditBatch Batch(double value) => new([Command(value)], Baseline.Revision);
    public Result<ProjectSnapshot> Preview(double value)
    {
        var result = NativeProjectCodec.ProjectCommands(Baseline.Project!, Command(value));
        return result.Success ? Result<ProjectSnapshot>.Ok(Baseline with { Project = result.Value }) : new(null, result.Diagnostics);
    }
}
