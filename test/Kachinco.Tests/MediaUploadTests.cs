using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class MediaUploadTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("bounded media fixture");

    [TestMethod]
    public void CatalogMatchesMcpCoreWireContract()
    {
        using var h = new Harness();
        CollectionAssert.AreEqual(new[] { "media_upload_finish" },
            MediaUploadMcpTools.Create(h.Fixture.Session, h.Store, () => { }).Where(t => t.Foreground).Select(t => t.Name).ToArray());
        // Pin the actual MCP Core envelope as well as the application's input schemas.
        var describe = typeof(McpBoundary).Assembly.GetType("Flamoris.Mcp.Core.McpProtocol")!
            .GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var tools = MediaUploadMcpTools.Create(h.Fixture.Session, h.Store, () => { })
            .Select(t => (ModelContextProtocol.Protocol.Tool)describe.Invoke(null, [t])!).ToArray();
        var actual = JsonSerializer.SerializeToElement(tools.Select(t => new {
            name = t.Name, description = t.Description, inputSchema = t.InputSchema
        }));
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media-upload-tools.json");
        if (Environment.GetEnvironmentVariable("KACHINCO_EXPORT_UPLOAD_CATALOG") is { } export)
            File.WriteAllText(export, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture)),
            System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText())), "Update the reviewed Hub catalog with any upstream contract change.");
    }

    [TestMethod]
    public async Task GuardedImportUsesProbeSharedHistoryAndDurableMedia()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        var before = h.Fixture.Session.GetProject(); Guid id = Guid.NewGuid();
        var begin = await h.Begin(grant, id);
        Assert.IsFalse(begin.IsError, begin.Error);
        Assert.AreEqual(MediaUploadStore.MaximumChunkBytes, begin.Value!.Value.GetProperty("chunkBytes").GetInt32());
        Assert.IsFalse((await h.Write(grant, id, 0, Bytes)).IsError);
        Assert.AreEqual(before.Revision, h.Fixture.Session.GetProject().Revision);
        var finish = await h.Call(grant, "media_upload_finish", new { uploadId = id });
        Assert.IsFalse(finish.IsError, finish.Error);
        var snapshot = h.Fixture.Session.GetProject();
        Assert.AreEqual(before.Revision + 1, snapshot.Revision);
        var asset = snapshot.Project!.Assets.Single(a => a.Id == id);
        Assert.AreEqual("voice.wav", asset.Name); Assert.AreEqual(2 * Fixture.T, asset.DurationTicks);
        CollectionAssert.AreEqual(Bytes, File.ReadAllBytes(asset.SourcePath));
        Assert.AreEqual(1, h.Changes);
        Assert.IsTrue(h.Fixture.Session.Undo(snapshot.Revision).Success);
        Assert.IsTrue(File.Exists(asset.SourcePath));
        Assert.IsFalse(h.Fixture.Project.Assets.Any(a => a.Id == id));
        Assert.IsTrue(h.Fixture.Session.Redo(h.Fixture.Session.GetProject().Revision).Success);
        var codec = ProjectJson.Deserialize(ProjectJson.Serialize(h.Fixture.Project).Value!);
        Assert.IsTrue(codec.Success); Assert.IsTrue(codec.Value!.Assets.Any(a => a.Id == id));
        h.Store.Dispose(); Assert.IsTrue(File.Exists(asset.SourcePath));
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Count(p => p != asset.SourcePath));
    }

    [TestMethod]
    public async Task ReadOnlyStaleAndMalformedCallsCannotWriteFiles()
    {
        using (var readHost = new Harness())
        using (var read = await readHost.Boundary.EnableAsync(McpPermission.ReadOnly))
        {
            Assert.IsTrue((await readHost.Begin(read, Guid.NewGuid())).IsError);
            Assert.IsFalse(Directory.Exists(readHost.Root));
        }
        using var h = new Harness(); using var edit = await h.Boundary.EnableAsync(McpPermission.Edit);
        var guard = h.Guard(); h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.VideoTrackId, false));
        Assert.AreEqual(McpErrors.StaleRevision, (await h.Begin(edit, Guid.NewGuid(), guard: guard)).Error);
        foreach (var payload in new object[] {
            new { uploadId = Guid.NewGuid(), fileName = "voice.wav", sizeBytes = Bytes.Length, sha256 = MediaUploadStore.Hash(Bytes) },
            new { uploadId = Guid.NewGuid(), fileName = "voice.wav", sizeBytes = "01", sha256 = MediaUploadStore.Hash(Bytes) },
            new { uploadId = Guid.NewGuid(), fileName = "voice.wav", sizeBytes = "20", sha256 = MediaUploadStore.Hash(Bytes), path = "outside.wav" }
        }) Assert.AreEqual(McpErrors.InvalidRequest, (await h.Call(edit, "media_upload_begin", payload)).Error);
        foreach (string name in new[] { "../voice.wav", "C:\\voice.wav", "dir/voice.wav", "file.wav:stream", "x.exe", "https://host/x.wav" })
            Assert.IsTrue((await h.Begin(edit, Guid.NewGuid(), name)).IsError, name);
        Assert.IsFalse(Directory.Exists(h.Root));
    }

    [TestMethod]
    public async Task BoundedChunksResumeWithoutDuplicatingBytesAndCancelOnlyPartialFiles()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        Guid id = Guid.NewGuid(); await h.Begin(grant, id);
        Assert.IsTrue((await h.Write(grant, id, 1, Bytes)).IsError);
        Assert.IsFalse((await h.Write(grant, id, 0, Bytes)).IsError);
        Assert.IsFalse((await h.Write(grant, id, 0, Bytes)).IsError);
        var resumed = await h.Begin(grant, id);
        Assert.AreEqual(Bytes.Length.ToString(), resumed.Value!.Value.GetProperty("receivedBytes").GetString());
        Assert.IsTrue((await h.Write(grant, id, 0, Encoding.UTF8.GetBytes(new string('z', Bytes.Length)))).IsError);
        Assert.IsTrue((await h.Write(grant, id, 0, new byte[MediaUploadStore.MaximumChunkBytes + 1])).IsError);
        Assert.IsTrue((await h.Call(grant, "media_upload_write", new { uploadId = id, offset = "0", dataBase64 = Convert.ToBase64String(Bytes), chunkSha256 = new string('0',64) })).IsError);
        Assert.IsFalse((await h.Call(grant, "media_upload_cancel", new { uploadId = id })).IsError);
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
        Assert.IsTrue((await h.Begin(grant, Guid.NewGuid(), size: MediaUploadStore.MaximumFileBytes + 1)).IsError);
        for (int i = 0; i < 2; i++) Assert.IsFalse((await h.Begin(grant, Guid.NewGuid())).IsError);
        Assert.AreEqual("upload_limit", (await h.Begin(grant, Guid.NewGuid())).Error);
    }

    [TestMethod]
    public async Task DigestProbeAndIncompleteFailuresCannotPublishMedia()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        Guid id = Guid.NewGuid(); await h.Begin(grant, id, digest: new string('0',64));
        Assert.AreEqual("upload_incomplete", (await h.Call(grant, "media_upload_finish", new { uploadId = id })).Error);
        await h.Write(grant, id, 0, Bytes);
        Assert.AreEqual("upload_digest_mismatch", (await h.Call(grant, "media_upload_finish", new { uploadId = id })).Error);
        Assert.AreEqual(0, h.Probe.Calls);
        id = Guid.NewGuid(); await h.Begin(grant, id); await h.Write(grant, id, 0, Bytes);
        h.Probe.Reject = true;
        Assert.AreEqual("upload_media_invalid", (await h.Call(grant, "media_upload_finish", new { uploadId = id })).Error);
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
        Assert.AreEqual(2, h.Fixture.Project.Assets.Length);
    }

    [TestMethod]
    public async Task EditDuringProbeRequiresFreshGuardAndRevocationDiscardsStaging()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        Guid id = Guid.NewGuid(); await h.Begin(grant, id); await h.Write(grant, id, 0, Bytes);
        h.Probe.Pause = true;
        var finishing = h.Call(grant, "media_upload_finish", new { uploadId = id });
        await h.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.VideoTrackId, false));
        h.Probe.Release.TrySetResult();
        Assert.AreEqual(McpErrors.StaleRevision, (await finishing).Error);
        Assert.IsFalse(h.Fixture.Project.Assets.Any(a => a.Id == id));
        Assert.IsFalse((await h.Call(grant, "media_upload_finish", new { uploadId = id })).IsError);
        var durable = h.Fixture.Project.Assets.Single(a => a.Id == id).SourcePath;
        var partial = Guid.NewGuid(); await h.Begin(grant, partial);
        grant.Revoke();
        Assert.IsTrue(File.Exists(durable));
        Assert.AreEqual(1, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task RevocationDuringProbeAndOldPreparedReservationCannotImport()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        Guid id = Guid.NewGuid(); await h.Begin(grant, id); await h.Write(grant, id, 0, Bytes);
        var prepared = await h.Store.PrepareAsync(id, CancellationToken.None);
        h.Store.Cancel(id); h.Store.Begin(id, "voice.wav", Bytes.Length, MediaUploadStore.Hash(Bytes), h.Fixture.Session.GetProject());
        Assert.ThrowsExactly<McpFault>(() => h.Store.Finish(prepared, h.Fixture.Session, CancellationToken.None));
        await h.Write(grant, id, 0, Bytes);
        h.Probe.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Probe.Pause = true;
        var finishing = h.Call(grant, "media_upload_finish", new { uploadId = id });
        await h.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        grant.Revoke(); h.Probe.Release.TrySetResult();
        Assert.IsTrue((await finishing).IsError);
        Assert.IsFalse(h.Fixture.Project.Assets.Any(a => a.Id == id));
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task RealWavMovMp4AndPngAreProbedAndRegistered()
    {
        foreach (string extension in new[] { ".wav", ".mov", ".mp4", ".png" })
        {
            using var h = new Harness(realProbe: true); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
            string source = Path.Combine(h.Parent, "source" + extension);
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (string arg in extension == ".wav"
                ? new[] { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.1", source }
                : extension == ".png"
                    ? new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=32x32", "-frames:v", "1", source }
                    : new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=32x32:r=24:d=0.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", source }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, error);
            byte[] bytes = await File.ReadAllBytesAsync(source); Guid id = Guid.NewGuid();
            Assert.IsFalse((await h.Begin(grant, id, "generated" + extension, bytes.Length, MediaUploadStore.Hash(bytes))).IsError);
            Assert.IsFalse((await h.Write(grant, id, 0, bytes)).IsError);
            var finish = await h.Call(grant, "media_upload_finish", new { uploadId = id });
            Assert.IsFalse(finish.IsError, finish.Error);
            Assert.AreEqual(extension, Path.GetExtension(h.Fixture.Project.Assets.Single(a => a.Id == id).SourcePath));
        }
    }

    [TestMethod]
    public async Task FullSizeChunkCancellationAndDocumentReplacementAreBounded()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        byte[] bytes = new byte[MediaUploadStore.MaximumChunkBytes + 1]; Random.Shared.NextBytes(bytes);
        Guid id = Guid.NewGuid();
        Assert.IsFalse((await h.Begin(grant, id, size: bytes.Length, digest: MediaUploadStore.Hash(bytes))).IsError);
        Assert.IsFalse((await h.Write(grant, id, 0, bytes[..MediaUploadStore.MaximumChunkBytes])).IsError);
        Assert.IsFalse((await h.Write(grant, id, MediaUploadStore.MaximumChunkBytes, bytes[^1..])).IsError);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => h.Store.PrepareAsync(id, cancelled.Token));
        Assert.AreEqual(bytes.Length.ToString(), h.Store.Begin(id, "voice.wav", bytes.Length,
            MediaUploadStore.Hash(bytes), h.Fixture.Session.GetProject()).ReceivedBytes);
        var oldGuard = h.Guard();
        Assert.IsTrue(h.Fixture.Session.ReplaceProject(h.Fixture.Project).Success);
        Assert.IsTrue((await h.Call(grant, "media_upload_finish", new { uploadId = id }, oldGuard)).IsError);
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task RenamedPlaylistCannotBeImportedAsMp4()
    {
        using var h = new Harness(realProbe: true); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        byte[] manifest = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nhttp://127.0.0.1:1/private.ts\n#EXT-X-ENDLIST\n");
        Guid id = Guid.NewGuid();
        await h.Begin(grant, id, "manifest.mp4", manifest.Length, MediaUploadStore.Hash(manifest));
        await h.Write(grant, id, 0, manifest);
        Assert.AreEqual("upload_media_invalid", (await h.Call(grant, "media_upload_finish", new { uploadId = id })).Error);
        Assert.IsFalse(h.Fixture.Project.Assets.Any(a => a.Id == id));
    }

    [TestMethod]
    public async Task IdleTransfersExpireAndFailedRegistrationRollsBackPublishedFile()
    {
        var clock = new Clock();
        using var h = new Harness(clock: clock); using var grant = await h.Boundary.EnableAsync(McpPermission.Edit);
        Guid expired = Guid.NewGuid(); await h.Begin(grant, expired); await h.Write(grant, expired, 0, Bytes);
        clock.Now += TimeSpan.FromMinutes(31);
        Guid id = Guid.NewGuid(); await h.Begin(grant, id); await h.Write(grant, id, 0, Bytes);
        Assert.ThrowsExactly<McpFault>(() => h.Store.Write(expired, 0, Convert.ToBase64String(Bytes), MediaUploadStore.Hash(Bytes)));
        var prepared = await h.Store.PrepareAsync(id, CancellationToken.None);
        Assert.IsTrue(h.Fixture.Edit(new RegisterMedia(new(id, "Human import", "existing.wav", MediaKind.Wav, Fixture.T, 48000, 1))).Success);
        Assert.ThrowsExactly<McpFault>(() => h.Store.Finish(prepared, h.Fixture.Session, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(h.Root, id.ToString("N") + ".wav")));
        Assert.AreEqual(1, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
        Assert.IsTrue(h.Store.Cancel(id));
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Length);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Probe : IMediaProbe
    {
        public int Calls; public bool Reject; public bool Pause;
        public TaskCompletionSource Entered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Result<MediaProbeInfo>> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++; Entered.TrySetResult();
            if (Pause) await Release.Task.WaitAsync(cancellationToken);
            if (Reject) return Result<MediaProbeInfo>.Fail(Diagnostic.Error("BAD_FIXTURE", "Fixture rejected."));
            return Result<MediaProbeInfo>.Ok(new(path, MediaKind.Wav, 2 * Fixture.T, 48000, 1, null, null, null, ImmutableArray.Create("pcm_s16le")));
        }
    }
    private sealed class Harness : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "kachinco-upload-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, "media");
        public Fixture Fixture { get; } = new();
        public Probe Probe { get; } = new();
        public MediaUploadStore Store { get; }
        public KachincoMcpHost Host { get; }
        public McpBoundary Boundary { get; }
        public int Changes;
        private readonly SemaphoreSlim lane = new(1);
        public Harness(bool realProbe = false, TimeProvider? clock = null)
        {
            Directory.CreateDirectory(Parent);
            Store = new(Root, realProbe ? new FfprobeMediaProbe() : Probe, clock);
            Host = new(Fixture.Session, () => false, async (action, token) => {
                await lane.WaitAsync(token); try { action(); } finally { lane.Release(); }
            });
            Boundary = new(Host, MediaUploadMcpTools.Create(Fixture.Session, Store, () => Changes++), new(), new(KachincoLogging.Create().Logger));
        }
        public RequestGuard Guard() => new(Host.Snapshot.RuntimeId, Host.Snapshot.DocumentToken, Host.Snapshot.Revision);
        public Task<McpResult> Call(CapabilityGrant grant, string name, object input, RequestGuard? guard = null) =>
            Boundary.InvokeAsync(grant, name, JsonSerializer.SerializeToElement(input), guard ?? Guard(), CancellationToken.None);
        public Task<McpResult> Begin(CapabilityGrant grant, Guid id, string name = "voice.wav", long? size = null, string? digest = null, RequestGuard? guard = null)
        {
            grant.Revoked.Register(Store.Dispose);
            return Call(grant, "media_upload_begin", new { uploadId = id, fileName = name, sizeBytes = (size ?? Bytes.Length).ToString(), sha256 = digest ?? MediaUploadStore.Hash(Bytes) }, guard);
        }
        public Task<McpResult> Write(CapabilityGrant grant, Guid id, long offset, byte[] bytes) =>
            Call(grant, "media_upload_write", new { uploadId = id, offset = offset.ToString(), dataBase64 = Convert.ToBase64String(bytes), chunkSha256 = MediaUploadStore.Hash(bytes) });
        public void Dispose()
        {
            Boundary.Dispose(); Host.Dispose(); Store.Dispose(); Fixture.Session.Dispose();
            try { Directory.Delete(Parent, true); } catch (IOException) { }
        }
    }
}
