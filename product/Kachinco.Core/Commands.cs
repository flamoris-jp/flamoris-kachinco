using System.Collections.Immutable;

namespace Kachinco.Core;

// Typed contracts: adapters never receive mutable domain objects or a generic setter.
public abstract record EditCommand;
public sealed record CreateProject(Guid ProjectId, string Name) : EditCommand;
public sealed record CreateSequence(Guid SequenceId, string Name, SequenceSettings Settings, long DurationTicks) : EditCommand;
public sealed record SetSequenceDuration(Guid SequenceId, long DurationTicks) : EditCommand;
public sealed record RegisterMedia(MediaAsset Asset) : EditCommand;
public sealed record RelinkMedia(Guid MediaAssetId, string SourcePath, long DurationTicks,
    int? SampleRate = null, int? Channels = null) : EditCommand;
public sealed record AddTrack(Guid SequenceId, Guid TrackId, string Name, TrackKind Kind) : EditCommand;
public sealed record InsertClip(Guid SequenceId, Guid TrackId, Clip Clip) : EditCommand;
public sealed record MoveClip(Guid SequenceId, Guid ClipId, Guid TargetTrackId, long StartTicks) : EditCommand;
public sealed record RippleReorderClip(Guid SequenceId, Guid ClipId, Guid? BeforeClipId) : EditCommand;
public sealed record TrimClip(Guid SequenceId, Guid ClipId, long StartTicks, long SourceInTicks, long DurationTicks) : EditCommand;
public sealed record SplitClip(Guid SequenceId, Guid ClipId, long SplitTicks, Guid RightClipId) : EditCommand;
public sealed record DeleteClip(Guid SequenceId, Guid ClipId) : EditCommand;
public sealed record SetClipProperties(Guid SequenceId, Guid ClipId, bool Enabled, ClipAppearance Appearance, AudioProperties Audio) : EditCommand;
public sealed record SetTrackEnabled(Guid SequenceId, Guid TrackId, bool Enabled) : EditCommand;
public sealed record ReorderTrack(Guid SequenceId, Guid TrackId, int NewIndex) : EditCommand;
public sealed record AddCaption(Guid SequenceId, Guid TrackId, Caption Caption) : EditCommand;
public sealed record UpdateCaption(Guid SequenceId, Guid CaptionId, long StartTicks, long DurationTicks, string Text, bool Enabled) : EditCommand;
public sealed record DeleteCaption(Guid SequenceId, Guid CaptionId) : EditCommand;

public sealed record EditBatch(ImmutableArray<EditCommand> Commands, long? ExpectedRevision = null, bool DryRun = false);
public sealed record EditResult(bool Success, long Revision, ImmutableArray<Diagnostic> Diagnostics);
public sealed record ProjectSnapshot(long Revision, Project? Project, bool CanUndo, bool CanRedo);
