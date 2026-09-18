using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace Kachinco.Tests;
[TestClass]
public sealed class McpEnvelopeTests
{
    [TestMethod]
    public async Task InvalidEnvelopesAreBoundedInNormalAndBusyPaths()
    {
        string[] inputs = ["[]", "null", "1", "\"x\"", "{", "{}",
            "{\"jsonrpc\":2,\"method\":\"ping\",\"id\":1}",
            "{\"jsonrpc\":\"2.0\",\"method\":null,\"id\":1}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":null}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":[]}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1.5}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"id\":1,\"id\":2}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"id\":1,\"params\":null}"];
        var f = new Fixture(); var before = f.Session.GetProject();
        var adapter = new McpEditorAdapter(f.Session, () => new { }, () => Assert.Fail(), new(McpPermission.ReadOnly));
        foreach (bool busy in new[] { false, true })
            foreach (string input in inputs)
            {
                using var response = JsonDocument.Parse((await adapter.HandleAsync(input, busy: busy))!);
                Assert.IsTrue(response.RootElement.TryGetProperty("error", out _), input);
            }
        Assert.AreEqual(before, f.Session.GetProject());
        using var ping = JsonDocument.Parse((await adapter.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}"))!);
        Assert.IsTrue(ping.RootElement.TryGetProperty("result", out _));
    }
    [TestMethod]
    public async Task BlockedFrameReadCancelsWithoutClosingTheSharedSession()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new BlockedStream();
        var read = new McpBoundedLineReader(stream).ReadAsync(cancellation.Token).AsTask();
        await stream.Entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await read);
    }
    private sealed class BlockedStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
