using System.Collections.Immutable;

using Kachinco.Core;

namespace Kachinco.Tests.Oracles;

public sealed class ManagedSessionOracle
{
    private readonly object gate = new();
    private readonly List<Project?> undo = [];
    private readonly List<Project?> redo = [];
    private readonly int historyLimit;
    private Project? current;
    private long revision;
    private string documentToken = Guid.NewGuid().ToString("N");

    // Transient identity of an open document instance, never persisted or a revision.
    public string DocumentToken { get { lock (gate) return documentToken; } }
    public event Action? DocumentReplacing;
    private void InvalidateDocument()
    {
        foreach (Action observer in DocumentReplacing?.GetInvocationList() ?? [])
            try { observer(); } catch { /* observers cannot prevent host lifecycle */ }
        documentToken = Guid.NewGuid().ToString("N");
    }

    public ManagedSessionOracle(int historyLimit = 100)
    {
        if (historyLimit < 1) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        this.historyLimit = historyLimit;
    }

    public ProjectSnapshot GetProject()
    {
        lock (gate) return new(revision, current, undo.Count > 0, redo.Count > 0);
    }

    public EditResult Execute(EditBatch batch, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (batch is null) return Fail("INVALID_BATCH", "Batch is required.");
            if (batch.ExpectedRevision is { } expected && expected != revision) return Fail("REVISION_CONFLICT", "Query the latest project before editing.");
            if (batch.Commands.IsDefaultOrEmpty || batch.Commands.Length > 10000) return Fail("INVALID_BATCH", "Provide 1–10000 commands.");
            Project? candidate = current;
            try
            {
                foreach (var command in batch.Commands)
                {
                    if (command is null) return Fail("INVALID_COMMAND", "Command cannot be null.");
                    candidate = ManagedCommandOracle.Apply(candidate, command);
                    var errors = ManagedValidatorOracle.Validate(candidate);
                    if (!errors.IsEmpty) return new(false, revision, errors);
                }
            }
            catch (ManagedEditRejectedException e) { return new(false, revision, [e.Diagnostic]); }
            catch (OverflowException) { return Fail("TIME_OVERFLOW", "Time arithmetic exceeds the supported integer range."); }
            if (!batch.DryRun)
            {
                if (revision == long.MaxValue) return Fail("REVISION_OVERFLOW", "Start a new session.");
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate?.Id != current?.Id) InvalidateDocument();
                Push(undo, current);
                current = candidate;
                redo.Clear();
                revision++;
            }
            return new(true, revision, []);
        }
    }

    public EditResult Undo(long? expectedRevision = null, CancellationToken cancellationToken = default) => Travel(undo, redo, expectedRevision, cancellationToken);
    public EditResult Redo(long? expectedRevision = null, CancellationToken cancellationToken = default) => Travel(redo, undo, expectedRevision, cancellationToken);

    // Open/New are explicit session lifecycle actions; a failed load never calls this.
    public EditResult ReplaceProject(Project? project, long? expectedRevision = null)
    {
        lock (gate)
        {
            if (expectedRevision is { } expected && expected != revision) return Fail("REVISION_CONFLICT", "Project changed while loading.");
            var diagnostics = project is null ? ImmutableArray<Diagnostic>.Empty : ManagedValidatorOracle.Validate(project);
            if (!diagnostics.IsEmpty) return new(false, revision, diagnostics);
            if (revision == long.MaxValue) return Fail("REVISION_OVERFLOW", "Start a new session.");
            InvalidateDocument();
            current = project;
            undo.Clear(); redo.Clear(); revision++;
            return new(true, revision, []);
        }
    }

    public Result<Sequence> GetSequence(Guid sequenceId)
    {
        lock (gate)
        {
            var s = current?.Sequences.FirstOrDefault(x => x.Id == sequenceId);
            return s is null ? Result<Sequence>.Fail(Diagnostic.Error("SEQUENCE_NOT_FOUND", "Sequence not found.", sequenceId)) : Result<Sequence>.Ok(s);
        }
    }

    private EditResult Travel(List<Project?> from, List<Project?> to, long? expectedRevision, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (expectedRevision is { } expected && expected != revision) return Fail("REVISION_CONFLICT", "Query the latest project before changing history.");
            if (from.Count == 0) return Fail("HISTORY_EMPTY", "No history entry available.");
            if (revision == long.MaxValue) return Fail("REVISION_OVERFLOW", "Start a new session.");
            cancellationToken.ThrowIfCancellationRequested();
            if (current?.Id != from[^1]?.Id) InvalidateDocument();
            Push(to, current); current = from[^1]; from.RemoveAt(from.Count - 1); revision++;
            return new(true, revision, []);
        }
    }
    private void Push(List<Project?> list, Project? value)
    {
        list.Add(value);
        if (list.Count > historyLimit) list.RemoveAt(0);
    }
    private EditResult Fail(string code, string message) => new(false, revision, [Diagnostic.Error(code, message)]);
}
