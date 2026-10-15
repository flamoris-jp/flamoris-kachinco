using System.Collections.Immutable;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PreviewFrameTests
{
    [TestMethod]
    public void ClosedArgumentsAndIndependentResolutionLimits()
    {
        string id = Fixture.Id(2).ToString();
        foreach (string extra in new[] { "\"timeTicks\":0", "\"timeTicks\":\"-1\"", "\"timeTicks\":\"01\"",
            "\"timeTicks\":\"9223372036854775808\"", "\"timeTicks\":\"0\",\"timeSeconds\":0",
            "\"timeTicks\":\"0\",\"output\":{\"width\":1921}", "\"timeTicks\":\"0\",\"output\":{\"format\":\"png\",\"quality\":85}",
            "\"timeTicks\":\"0\",\"output\":{\"quality\":0}", "\"timeTicks\":\"0\",\"output\":{\"width\":64,\"width\":65}" })
            Assert.ThrowsExactly<JsonException>(() => PreviewFrameCapture.Decode(JsonSerializer.Deserialize<JsonElement>("{\"sequenceId\":\"" + id + "\"," + extra + "}")));
        Assert.AreEqual((960, 540), PreviewFrameCapture.OutputSize(1920, 1080, 960));
        Assert.AreEqual((1080, 1920), PreviewFrameCapture.OutputSize(1080, 1920, 1920));
        Assert.AreEqual((100, 200), PreviewFrameCapture.OutputSize(100, 200, 960));
    }

    [TestMethod]
    public async Task SnapshotMetadataFrameRoundingAndReadOnlyHistory()
    {
        using var test = new CaptureHarness();
        using var grant = await test.Boundary.EnableAsync(McpPermission.ReadOnly);
        var before = test.Fixture.Session.GetProject();
        foreach (long tick in new[] { 0L, Fixture.T / 2, 8 * Fixture.T - 1 })
        {
            var reply = await test.Call(grant, tick);
            Assert.IsFalse(reply.IsError, reply.Error);
            Assert.IsNotNull(reply.Image); Assert.AreEqual("image/jpeg", reply.Image.MimeType);
            var json = reply.Value!.Value;
            long index = Math.Min(TimelineTime.TicksToFrame(tick, new(30, 1)), 239);
            Assert.AreEqual(index.ToString(), json.GetProperty("resolvedFrameIndex").GetString());
            Assert.AreEqual(TimelineTime.FrameToTicks(index, new(30, 1)).ToString(), json.GetProperty("resolvedTimeTicks").GetString());
            Assert.AreEqual(before.Revision.ToString(), json.GetProperty("projectRevision").GetString());
            Assert.AreEqual(test.Host.Snapshot.DocumentToken, json.GetProperty("documentToken").GetString());
        }
        Assert.AreEqual(before, test.Fixture.Session.GetProject());
        Assert.AreEqual(0, test.Boundary.Status.Current.ForegroundCount);
        Assert.AreEqual("time_out_of_range", (await test.Call(grant, 8 * Fixture.T)).Error);
        test.Encoder.Size = McpImage.MaximumBytes + 1;
        Assert.AreEqual("response_too_large", (await test.Call(grant, 0)).Error);
    }

    [TestMethod]
    public async Task MidRenderEditAndSaveAsRejectDisclosureAndParallelCaptureIsBusy()
    {
        var renderer = new BarrierRenderer(); using var test = new CaptureHarness(renderer);
        using var grant = await test.Boundary.EnableAsync(McpPermission.ReadOnly);
        var first = test.Call(grant, 0); await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.Busy, (await test.Call(grant, 0)).Error);
        test.Fixture.Edit(new SetTrackEnabled(test.Fixture.SequenceId, test.Fixture.VideoTrackId, false));
        renderer.Release.TrySetResult();
        var stale = await first; Assert.AreEqual(McpErrors.StaleRevision, stale.Error); Assert.IsNull(stale.Image);
        renderer = new(); test.Renderer = renderer;
        first = test.Call(grant, 0); await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        test.Path = "changed.fkproj"; renderer.Release.TrySetResult();
        Assert.AreEqual(McpErrors.StaleRevision, (await first).Error);
    }

    [TestMethod]
    public async Task ReplacementAndCancellationReturnNoImage()
    {
        var renderer = new BarrierRenderer(); using var test = new CaptureHarness(renderer);
        using var grant = await test.Boundary.EnableAsync(McpPermission.ReadOnly);
        var call = test.Call(grant, 0); await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        test.Fixture.Session.ReplaceProject(test.Fixture.Project); renderer.Release.TrySetResult();
        Assert.IsNull((await call).Image);
        using var next = await test.Boundary.EnableAsync(McpPermission.ReadOnly);
        renderer = new(); test.Renderer = renderer;
        using var stop = new CancellationTokenSource();
        call = test.Call(next, 0, stop.Token); await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel(); Assert.AreEqual(McpErrors.Cancelled, (await call).Error);
        renderer.Release.TrySetResult();
    }

    [TestMethod]
    public async Task MissingMediaUsesOrdinaryRendererDiagnostic()
    {
        using var test = new CaptureHarness(new SharedFrameRenderer(new FfmpegMediaDecoder()));
        using var grant = await test.Boundary.EnableAsync(McpPermission.ReadOnly);
        Assert.AreEqual("MEDIA_MISSING", (await test.Call(grant, 0)).Error);
    }

    [TestMethod]
    public async Task RationalFrameTimeAndTimeoutRetainAdmissionUntilWorkerJoins()
    {
        using (var test = new CaptureHarness())
        {
            var id = Guid.NewGuid(); var fps = new FrameRate(30000, 1001);
            Assert.IsTrue(test.Fixture.Edit(new CreateSequence(id, "Fractional", new(1080, 1920, fps), 5 * Fixture.T)).Success);
            using var grant = await test.Boundary.EnableAsync(McpPermission.ReadOnly);
            long frameTick = TimelineTime.FrameToTicks(75, fps);
            var response = await test.Call(grant, frameTick + 1, sequenceId: id);
            Assert.AreEqual("75", response.Value!.Value.GetProperty("resolvedFrameIndex").GetString());
            Assert.AreEqual(frameTick.ToString(), response.Value.Value.GetProperty("resolvedTimeTicks").GetString());
            Assert.AreEqual("sequence_not_found", (await test.Call(grant, 0, sequenceId: Guid.NewGuid())).Error);
        }
        var renderer = new BarrierRenderer(ignoreCancellation: true);
        using var blocked = new CaptureHarness(renderer, new McpOptions { RequestTimeoutMs = 100 });
        using var read = await blocked.Boundary.EnableAsync(McpPermission.ReadOnly);
        var pending = blocked.Call(read, 0); await renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(McpErrors.Timeout, (await pending).Error);
        Assert.AreEqual(McpErrors.Busy, (await blocked.Call(read, 0)).Error);
        renderer.Release.TrySetResult();
    }

    private sealed class CaptureHarness : IDisposable
    {
        public Fixture Fixture { get; } = new();
        public KachincoMcpHost Host { get; }
        public McpBoundary Boundary { get; }
        public IFrameRenderer Renderer { get; set; }
        public Encoder Encoder { get; } = new();
        public string? Path { get; set; }
        public CaptureHarness(IFrameRenderer? renderer = null, McpOptions? options = null)
        {
            Renderer = renderer ?? new SolidRenderer();
            Host = new(Fixture.Session, () => false, (action, token) => { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; });
            var capture = new PreviewFrameCapture(Encoder, rendererFactory: _ => Renderer);
            Boundary = new(Host, [capture.CreateTool(Fixture.Session, () => Path, () => Host.Snapshot)], options ?? new(), new(KachincoLogging.Create().Logger));
        }
        public Task<McpResult> Call(CapabilityGrant grant, long tick, CancellationToken token = default, Guid? sequenceId = null) => Boundary.InvokeAsync(grant, "preview_frame",
            JsonSerializer.SerializeToElement(new { sequenceId = sequenceId ?? Fixture.SequenceId, timeTicks = tick.ToString() }), null, token);
        public void Dispose() { Boundary.Dispose(); Host.Dispose(); Fixture.Session.Dispose(); }
    }
    private sealed class Encoder : IPreviewImageEncoder
    {
        public int Size { get; set; } = 8;
        public Task<byte[]> EncodeAsync(RenderedVideoFrame frame, int width, int height, string format, int quality, CancellationToken token) => Task.FromResult(new byte[Size]);
    }
    private sealed class SolidRenderer : IFrameRenderer
    {
        public ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long index, CancellationToken token) =>
            ValueTask.FromResult(Result<RenderedVideoFrame>.Ok(new(index, frame.Tick, frame.Settings.Width, frame.Settings.Height, [])));
    }
    private sealed class BarrierRenderer(bool ignoreCancellation = false) : IFrameRenderer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long index, CancellationToken token)
        {
            Entered.TrySetResult(); await (ignoreCancellation ? Release.Task : Release.Task.WaitAsync(token));
            return Result<RenderedVideoFrame>.Ok(new(index, frame.Tick, frame.Settings.Width, frame.Settings.Height, []));
        }
    }
}
