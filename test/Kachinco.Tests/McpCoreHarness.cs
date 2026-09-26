using System.Text.Json;
using Flamoris.Logging;
using Flamoris.Mcp.Core;
using Kachinco.Infrastructure;

namespace Kachinco.Tests;

internal sealed class McpCoreHarness : IDisposable
{
    private readonly SemaphoreSlim lane = new(1);
    public Fixture Fixture { get; } = new();
    public KachincoMcpHost Host { get; }
    public McpBoundary Boundary { get; }
    public bool Busy { get; set; }
    public int Changes { get; private set; }
    public McpCoreHarness(McpOptions? options = null, IEnumerable<HostTool>? additional = null, FlamorisLogger? logger = null)
    {
        Host = new(Fixture.Session, () => Busy, async (action, token) => {
            await lane.WaitAsync(token);
            try { token.ThrowIfCancellationRequested(); action(); } finally { lane.Release(); }
        });
        Boundary = new(Host, KachincoMcpTools.Create(Fixture.Session, () => new { sequenceId = Fixture.SequenceId }, () => Changes++)
            .Concat(additional ?? []), options ?? new(), new McpDiagnostics(logger ?? KachincoLogging.Create().Logger));
    }
    public RequestGuard Guard() => new(Host.Snapshot.RuntimeId, Host.Snapshot.DocumentToken, Host.Snapshot.Revision);
    public object Batch(bool enabled = false) => new { commands = new[] {
        new { type = "SetTrackEnabled", sequenceId = Fixture.SequenceId, trackId = Fixture.VideoTrackId, enabled } } };
    public Task<McpResult> Call(CapabilityGrant grant, string name, object? input = null, RequestGuard? guard = null, CancellationToken token = default) =>
        Boundary.InvokeAsync(grant, name, JsonSerializer.SerializeToElement(input ?? new { }), guard, token);
    public Task<T> Human<T>(Func<T> action) => Host.InvokeAsync(action, CancellationToken.None);
    public void Dispose() { Boundary.Dispose(); Host.Dispose(); }
}
