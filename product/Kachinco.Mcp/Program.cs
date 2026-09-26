using Flamoris.Mcp.Core;
using Kachinco.Infrastructure;

using var output = Console.OpenStandardOutput();
// Even a logging sink fallback must never write to the protocol stream.
Console.SetOut(Console.Error);
string? capability = Environment.GetEnvironmentVariable(StdioBridge.CredentialEnvironmentVariable);
Environment.SetEnvironmentVariable(StdioBridge.CredentialEnvironmentVariable, null);
var logger = KachincoLogging.Create("mcp-bridge").Logger;
if (args.Length != 2 || args[0] != "--pipe" || !McpOptions.ValidPipeName(args[1]))
{
    Console.Error.WriteLine("{\"error\":\"invalid_request\"}");
    return 2;
}
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try
{
    logger.Info("mcp.transport", "MCP bridge started");
    await StdioBridge.RunAsync(args[1], capability, Console.OpenStandardInput(), output, cancellationToken: lifetime.Token);
    logger.Info("mcp.transport", "MCP bridge stopped");
    return 0;
}
catch (McpFault fault)
{
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = fault.Code }));
    return 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("{\"error\":\"cancelled\"}"); return 1; }
catch { Console.Error.WriteLine("{\"error\":\"transport_unavailable\"}"); return 1; }
