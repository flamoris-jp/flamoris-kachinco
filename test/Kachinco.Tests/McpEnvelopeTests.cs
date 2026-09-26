using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

// Consumer-level transport regression through the actual Core endpoint. Framing
// implementation stays solely in the package; this is a deliberately raw client.
[TestClass]
public sealed class McpEnvelopeTests
{
    [TestMethod]
    public async Task WrongCredentialMalformedAndOversizedFramesDoNotPoisonTheEditor()
    {
        using var h = new McpCoreHarness(new McpOptions { MaxRequestBytes = 4096, ReadTimeoutMs = 200 });
        using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new LocalMcpEndpoint(h.Boundary).RunAsync(grant, lifetime.Token);
        var before = h.Fixture.Session.GetProject();
        try
        {
            var ready = NextEndpoint(h);
            await using (var denied = await Connect(h, lifetime.Token))
            {
                await Send(denied, new string('0', 64), lifetime.Token);
                using var reader = new StreamReader(denied, leaveOpen: true);
                StringAssert.Contains((await reader.ReadLineAsync(lifetime.Token))!, "unauthorized");
            }
            await ready.WaitAsync(lifetime.Token);
            foreach (byte[] input in new[] { "{\n"u8.ToArray(), new byte[] { 0xff, 10 }, Encoding.UTF8.GetBytes(new string('x', 5000) + "\n") })
            {
                ready = NextEndpoint(h);
                await using (var pipe = await Connect(h, lifetime.Token))
                {
                    await Authenticate(pipe, grant, lifetime.Token);
                    try { await pipe.WriteAsync(input, lifetime.Token); await pipe.FlushAsync(lifetime.Token); }
                    catch (IOException) { /* oversized/invalid input may close immediately */ }
                }
                // Wait for a new listening instance, not the closing Unix socket.
                await ready.WaitAsync(lifetime.Token);
            }
            await using var healthy = await Connect(h, lifetime.Token);
            await Authenticate(healthy, grant, lifetime.Token);
            await healthy.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"boundary-test\",\"version\":\"1\"}}}\n"), lifetime.Token);
            using var reply = new StreamReader(healthy, leaveOpen: true);
            using var json = JsonDocument.Parse((await reply.ReadLineAsync(lifetime.Token))!);
            Assert.IsTrue(json.RootElement.TryGetProperty("result", out _));
            Assert.AreEqual(before, h.Fixture.Session.GetProject());
            h.Boundary.Disable();
        }
        finally { lifetime.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task PartialFrameDeadlineAndRevokeUnblockConnection()
    {
        using var h = new McpCoreHarness(new McpOptions { ReadTimeoutMs = 100 });
        using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new LocalMcpEndpoint(h.Boundary).RunAsync(grant, lifetime.Token);
        try
        {
            var ready = NextEndpoint(h);
            await using (var partial = await Connect(h, lifetime.Token))
            {
                await Authenticate(partial, grant, lifetime.Token);
                await partial.WriteAsync("{"u8.ToArray(), lifetime.Token); await partial.FlushAsync(lifetime.Token);
                using var reader = new StreamReader(partial, leaveOpen: true);
                Assert.IsNull(await reader.ReadLineAsync(lifetime.Token));
            }
            await ready.WaitAsync(lifetime.Token);
            await using var idle = await Connect(h, lifetime.Token);
            await Authenticate(idle, grant, lifetime.Token);
            h.Boundary.Disable();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(grant.IsActive);
        }
        finally { lifetime.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    private static Task NextEndpoint(McpCoreHarness h)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool unavailable = false;
        void Changed()
        {
            var state = h.Boundary.Status.Current;
            if (!state.EndpointAvailable) unavailable = true;
            if (unavailable && state.IsGreen && !state.Connected)
            {
                h.Boundary.Status.Changed -= Changed;
                ready.TrySetResult();
            }
        }
        h.Boundary.Status.Changed += Changed;
        return ready.Task;
    }
    private static async Task<NamedPipeClientStream> Connect(McpCoreHarness h, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", h.Boundary.Options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, token); return pipe;
    }
    private static async Task Send(Stream stream, string credential, CancellationToken token)
    {
        var line = JsonSerializer.Serialize(new { version = 1, capability = credential });
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token); await stream.FlushAsync(token);
    }
    private static async Task Authenticate(Stream stream, CapabilityGrant grant, CancellationToken token)
    {
        await Send(stream, grant.ExportCredential(), token);
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var response = JsonDocument.Parse((await reader.ReadLineAsync(token))!);
        Assert.AreEqual(1, response.RootElement.GetProperty("version").GetInt32());
    }
}
