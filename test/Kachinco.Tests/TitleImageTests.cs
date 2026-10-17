using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class TitleImageTests
{
    [TestMethod]
    public async Task CaptureAndIndependentExportUseCompositedSnapshotAndRetainCommittedFilesForUndo()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var before = h.Fixture.Session.GetProject();
        var reply = await h.Call(grant, "title_image_from_frame", new { sequenceId = h.Fixture.SequenceId, timeTicks = (Fixture.T + 1).ToString() }, h.Guard());
        Assert.IsFalse(reply.IsError, reply.Error); Assert.IsTrue(reply.Value!.Value.GetProperty("success").GetBoolean());
        var sequence = h.Fixture.Project.Sequences[0];
        Assert.IsNotNull(sequence.TitleImage); Assert.IsNull(sequence.TitleSlot);
        Assert.AreEqual(Fixture.T, sequence.TitleImage.SourceFrameTicks);
        Assert.AreEqual(1, h.Renderer.Last!.VideoLayers.Length); Assert.AreEqual(1, h.Renderer.Last.Captions.Length);
        Assert.AreEqual(before.Project!.Sequences[0].DurationTicks, sequence.DurationTicks);
        string image = h.Fixture.Project.Assets.Single(a => a.Id == sequence.TitleImage.MediaAssetId).SourcePath;
        Assert.IsTrue(File.Exists(image)); Assert.IsTrue(h.Fixture.Session.Undo().Success);
        Assert.IsTrue(File.Exists(image)); Assert.IsNull(h.Fixture.Project.Sequences[0].TitleImage);
        Assert.IsTrue(h.Fixture.Session.Redo().Success);
        using var read = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        var snapshot = h.Fixture.Session.GetProject();
        var exported = await h.Call(read, "title_image", new { sequenceId = h.Fixture.SequenceId });
        Assert.IsFalse(exported.IsError, exported.Error); Assert.AreEqual("image/jpeg", exported.Image!.MimeType);
        Assert.AreEqual(sequence.TitleImage.MediaAssetId, h.Renderer.Last!.VideoLayers.Single().MediaAssetId);
        Assert.AreEqual(0, h.Renderer.Last.Captions.Length);
        Assert.AreEqual(snapshot, h.Fixture.Session.GetProject());
        Assert.AreEqual(McpErrors.Forbidden, (await h.Call(read, "title_image_from_frame",
            new { sequenceId = h.Fixture.SequenceId, timeTicks = "0" }, h.Guard())).Error);
        Assert.IsTrue(File.Exists(image));
    }
    [TestMethod]
    public async Task StaleCaptureRemovesUncommittedPngAndDoesNotChangeSelection()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        h.Renderer.Hold = true;
        var pending = h.Call(grant, "title_image_from_frame", new { sequenceId = h.Fixture.SequenceId, timeTicks = "0" }, h.Guard());
        await h.Renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.VideoTrackId, false)).Success);
        h.Renderer.Release.TrySetResult();
        var reply = await pending;
        Assert.IsTrue(reply.IsError || !reply.Value!.Value.GetProperty("success").GetBoolean());
        Assert.IsNull(h.Fixture.Project.Sequences[0].TitleImage);
        Assert.AreEqual(0, Directory.Exists(h.Directory) ? Directory.GetFiles(h.Directory).Length : 0);
    }
    [TestMethod]
    public async Task CancelAndInvalidTimeNeverPublishAnAssetAndImageQueriesRejectRevisionChanges()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var reply = await h.Call(grant, "title_image_from_frame", new { sequenceId = h.Fixture.SequenceId, timeTicks = "-1" }, h.Guard());
        Assert.IsTrue(reply.IsError); Assert.IsNull(h.Fixture.Project.Sequences[0].TitleImage);
        h.Renderer.Hold = true; using var cancelled = new CancellationTokenSource();
        var pending = h.Call(grant, "title_image_from_frame", new { sequenceId = h.Fixture.SequenceId, timeTicks = "0" }, h.Guard(), cancelled.Token);
        await h.Renderer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
        Assert.AreEqual(McpErrors.Cancelled, (await pending).Error);
        h.Renderer.Release.TrySetResult();
    }
    private sealed class Harness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "title-" + Guid.NewGuid().ToString("N"));
        public Fixture Fixture { get; } = new();
        public Renderer Renderer { get; } = new();
        public KachincoMcpHost Host { get; }
        public McpBoundary Boundary { get; }
        public Harness()
        {
            Host = new(Fixture.Session, () => false, (action, token) => { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; });
            var service = new TitleImageService(Directory, new Encoder(), rendererFactory: _ => Renderer);
            Boundary = new(Host, TitleImageMcpTools.Create(Fixture.Session, service, new Encoder(), () => null, () => {}),
                new McpOptions(), new(KachincoLogging.Create().Logger));
        }
        public RequestGuard Guard() => new(Host.Snapshot.RuntimeId, Host.Snapshot.DocumentToken, Host.Snapshot.Revision);
        public Task<McpResult> Call(CapabilityGrant grant, string name, object input, RequestGuard? guard = null, CancellationToken token = default) =>
            Boundary.InvokeAsync(grant, name, JsonSerializer.SerializeToElement(input), guard, token);
        public void Dispose()
        {
            Boundary.Dispose(); Host.Dispose(); Fixture.Session.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
    private sealed class Encoder : IPreviewImageEncoder
    {
        public Task<byte[]> EncodeAsync(RenderedVideoFrame frame, int width, int height, string format, int quality, CancellationToken token) => Task.FromResult(new byte[8]);
    }
    private sealed class Renderer : IFrameRenderer
    {
        public bool Hold;
        public EvaluatedFrame? Last;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Result<RenderedVideoFrame>> RenderAsync(Project project, EvaluatedFrame frame, long index, CancellationToken token)
        {
            Last = frame;
            if (Hold) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return Result<RenderedVideoFrame>.Ok(new(index, frame.Tick, frame.Settings.Width, frame.Settings.Height, []));
        }
    }
}
