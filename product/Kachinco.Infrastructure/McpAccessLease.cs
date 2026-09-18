using Kachinco.Core;

namespace Kachinco.Infrastructure;

public enum McpPermission { ReadOnly, Edit }

// Transient authorization only. No Project, revision, history or timeline authority.
public sealed class McpAccessLease(McpPermission permission) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource revoked = new();
    private bool active = true;
    public McpPermission Permission { get; } = permission;
    public CancellationToken Token => revoked.Token;
    public bool IsActive { get { lock (gate) return active; } }

    public void Demand(bool edit = false, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!active) throw new OperationCanceledException("Attachment revoked.");
            cancellationToken.ThrowIfCancellationRequested();
            if (edit && Permission != McpPermission.Edit) throw new UnauthorizedAccessException("Edit permission required.");
        }
    }

    // Revoke and a synchronous commit have a single linearization boundary.
    // Cancellation is additionally checked by Core immediately before changing history.
    public T Run<T>(Func<T> action, bool edit = false, CancellationToken cancellationToken = default)
    {
        lock (gate) { Demand(edit, cancellationToken); return action(); }
    }
    public EditResult Commit(EditorSession session, EditBatch batch, CancellationToken token = default) =>
        Run(() => session.Execute(batch, token), true, token);

    public void Revoke()
    {
        lock (gate) { if (!active) return; active = false; }
        revoked.Cancel();
    }
    // Tokens may still be held by finishing jobs; do not dispose their source prematurely.
    public void Dispose() => Revoke();
}
