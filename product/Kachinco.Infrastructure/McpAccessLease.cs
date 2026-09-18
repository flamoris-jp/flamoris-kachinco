using Kachinco.Core;

namespace Kachinco.Infrastructure;

public enum McpPermission { ReadOnly, Edit }

// Transient authorization only. No Project, revision, history or timeline authority.
public sealed class McpAccessLease(EditorSession session, McpPermission permission) : IDisposable
{
    private readonly object gate = new();
    private readonly Guid projectId = session.GetProject().Project?.Id
        ?? throw new InvalidOperationException("Open a project before granting access.");
    private readonly CancellationTokenSource revoked = new();
    private bool active = true;
    public McpPermission Permission { get; } = permission;
    public CancellationToken Token => revoked.Token;
    // Called at every WPF Refresh, before the dispatcher can process another edit.
    // Observing null is irreversible, including when a later human Redo restores this ID.
    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                if (active && session.GetProject().Project?.Id != projectId) Revoke();
                return active;
            }
        }
    }

    public void Demand(bool edit = false, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!IsActive) throw new OperationCanceledException("Attachment revoked.");
            cancellationToken.ThrowIfCancellationRequested();
            if (edit && Permission != McpPermission.Edit) throw new UnauthorizedAccessException("Edit permission required.");
        }
    }

    // Revoke and a synchronous commit have a single linearization boundary.
    // Cancellation is additionally checked by Core immediately before changing history.
    public T Run<T>(Func<T> action, bool edit = false, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            Demand(edit, cancellationToken);
            try { return action(); }
            finally { _ = IsActive; } // Undo can remove the document being authorized.
        }
    }
    public EditResult Commit(EditorSession target, EditBatch batch, CancellationToken token = default)
    {
        if (!ReferenceEquals(session, target)) throw new UnauthorizedAccessException("Different editor session.");
        return Run(() => session.Execute(batch, token), true, token);
    }

    public void Revoke()
    {
        lock (gate) { if (!active) return; active = false; }
        revoked.Cancel();
    }
    // Tokens may still be held by finishing jobs; do not dispose their source prematurely.
    public void Dispose() => Revoke();
}
