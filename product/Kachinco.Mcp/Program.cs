using System.IO.Pipes;

if (args.Length != 2 || args[0] != "--pipe" || !args[1].StartsWith("kachinco-", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: Kachinco.Mcp --pipe <name displayed by the running editor>");
    return 2;
}
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
try
{
    await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(10000, lifetime.Token);
    var input = Console.OpenStandardInput().CopyToAsync(pipe, lifetime.Token);
    var output = pipe.CopyToAsync(Console.OpenStandardOutput(), lifetime.Token);
    await Task.WhenAny(input, output);
    lifetime.Cancel();
    try { await Task.WhenAll(input, output); } catch (OperationCanceledException) { }
    return 0;
}
catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
{
    Console.Error.WriteLine(e.Message); return 1;
}
