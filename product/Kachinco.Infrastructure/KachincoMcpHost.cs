using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// A facade over the caller's authority, never an owner of Project/session/history.
public sealed class KachincoMcpHost : IMcpHost, IDisposable
{
    private readonly EditorSession session;
    private readonly Func<bool> humanBusy;
    private readonly Func<Action, CancellationToken, Task> dispatch;
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private bool shuttingDown;

    public KachincoMcpHost(EditorSession session, Func<bool> humanBusy,
        Func<Action, CancellationToken, Task> dispatch)
    {
        this.session = session;
        this.humanBusy = humanBusy;
        this.dispatch = dispatch;
        session.DocumentReplacing += Invalidate;
    }

    public HostSnapshot Snapshot
    {
        get
        {
            var current = session.GetProject();
            return new("flamoris.kachinco", "0.2.0", runtimeId, session.DocumentToken,
                current.Revision, !shuttingDown && current.Project is not null, humanBusy());
        }
    }

    public event Action? Invalidating;
    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        T result = default!;
        await dispatch(() => { cancellationToken.ThrowIfCancellationRequested(); result = action(); }, cancellationToken);
        return result;
    }
    private void Invalidate()
    {
        foreach (Action observer in Invalidating?.GetInvocationList() ?? [])
            try { observer(); } catch { /* one observer cannot suppress other revocations */ }
    }
    public void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        Invalidate();
    }
    public void Dispose()
    {
        Shutdown();
        session.DocumentReplacing -= Invalidate;
    }
}
