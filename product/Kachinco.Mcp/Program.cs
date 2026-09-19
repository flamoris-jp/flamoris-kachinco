using System.IO.Pipes;
using Kachinco.Infrastructure;

var logger = KachincoLogging.Create("mcp-bridge").Logger;
var transportDiagnostics = new McpTransportDiagnostics(logger, "stdio-to-same-user-named-pipe");
transportDiagnostics.EndpointStarted();

if (args.Length != 2 || args[0] != "--pipe" || !(args[1].StartsWith("kachinco-", StringComparison.Ordinal) && Guid.TryParseExact(args[1][9..], "N", out _)))
{
    logger.Warn("mcp.protocol", "MCP bridge arguments rejected",
        new Dictionary<string, object?> { ["argumentsCount"] = args.Length });
    transportDiagnostics.EndpointStopped();
    Console.Error.WriteLine("Usage: Kachinco.Mcp --pipe <name displayed by the running editor>");
    return 2;
}
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try
{
    await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(10000, lifetime.Token);
    transportDiagnostics.ClientAttached();
    var input = Console.OpenStandardInput().CopyToAsync(pipe, lifetime.Token);
    var output = pipe.CopyToAsync(Console.OpenStandardOutput(), lifetime.Token);
    await Task.WhenAny(input, output);
    lifetime.Cancel();
    pipe.Dispose();
    // Windows console stdin may not honor cancellation. A losing pump must not
    // hold the bridge alive after editor EOF or stdin closure.
    try { await Task.WhenAll(input, output).WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
    transportDiagnostics.ClientDetached();
    transportDiagnostics.EndpointStopped();
    return 0;
}
catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
{
    transportDiagnostics.ConnectionFailed(e);
    transportDiagnostics.EndpointStopped();
    Console.Error.WriteLine("MCP bridge connection closed or unavailable."); return 1;
}
catch (Exception exception)
{
    logger.Error("mcp.transport", "MCP bridge failed unexpectedly", exception,
        new Dictionary<string, object?> { ["transport"] = "stdio-to-same-user-named-pipe" });
    transportDiagnostics.EndpointStopped();
    Console.Error.WriteLine("MCP bridge failed."); return 1;
}
