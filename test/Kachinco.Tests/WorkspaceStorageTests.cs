using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Cli;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class WorkspaceStorageTests
{
    [TestMethod]
    public async Task ConfigSelectsIndependentProjectsAndRestartPreservesGuardedCheckpoint()
    {
        using var area = new Area(); var first = await area.Configure("Gravity Green"); first.Prepare();
        Guid projectId, sequence;
        using (var host = Open(first))
        {
            await host.LoadAsync(default, createIfMissing: true);
            projectId = host.Session.GetProject().Project!.Id;
            sequence = host.Session.GetProject().Project!.Sequences.Single().Id;
            using var grant = await host.Boundary.EnableAsync(McpPermission.Edit);
            var edit = await host.Boundary.InvokeAsync(grant, "edit_batch", JsonSerializer.SerializeToElement(new { commands = new[] {
                new { type = "SetSequenceDuration", sequenceId = sequence, durationTicks = TimelineTime.TicksPerSecond.ToString() } } }), host.Guard());
            Assert.IsFalse(edit.IsError, edit.Error);
            Assert.IsFalse(host.Dirty);
            Assert.Throws<IOException>(() => Open(first));
        }
        byte[] saved = File.ReadAllBytes(first.ProjectPath);
        var other = await area.Configure("Second"); other.Prepare();
        using (var host = Open(other))
        {
            await host.LoadAsync(default, createIfMissing: true);
            Assert.AreNotEqual(projectId, host.Session.GetProject().Project!.Id);
        }
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(first.ProjectPath));
        using var reopened = Open(first); await reopened.LoadAsync(default);
        Assert.AreEqual(projectId, reopened.Session.GetProject().Project!.Id);
        Assert.AreEqual(TimelineTime.TicksPerSecond, reopened.Session.GetProject().Project!.Sequences.Single().DurationTicks);
        Assert.IsTrue(Directory.Exists(first.MediaDirectory)); Assert.IsTrue(Directory.Exists(first.ExportDirectory));
    }

    [TestMethod]
    public async Task ExistingMalformedProjectAndReadOnlyMissingProjectAreNeverReplaced()
    {
        using var area = new Area(); var storage = await area.Configure("Keep"); storage.Prepare();
        using (var host = Open(storage))
            await Assert.ThrowsAsync<IOException>(() => host.LoadAsync(default));
        Assert.IsFalse(File.Exists(storage.ProjectPath));
        File.WriteAllText(storage.ProjectPath, "not a project");
        using (var host = Open(storage))
            await Assert.ThrowsAsync<InvalidDataException>(() => host.LoadAsync(default, createIfMissing: true));
        Assert.AreEqual("not a project", File.ReadAllText(storage.ProjectPath));
    }

    [TestMethod]
    public async Task InvalidNamesFieldsAndOverlappingRootsFailBeforeCreatingData()
    {
        using var area = new Area();
        foreach (string name in new[] { "../other", ".hidden", "a/b", "a\\b", "trailing.", "NUL", "COM1.txt", " a ", "x:y" })
            await Assert.ThrowsAsync<InvalidDataException>(() => area.Configure(name));
        string good = JsonSerializer.Serialize(new { projectsRoot = area.Root, projectName = "Movie", temporaryExportRoot = area.Temporary });
        foreach (string json in new[] { good.Replace("\"Movie\"", "\"Movie\",\"projectName\":\"Other\""), good.Replace("\"Movie\"", "\"Movie\",\"extra\":\"value\""),
            JsonSerializer.Serialize(new { projectsRoot = area.Root, projectName = "Movie", temporaryExportRoot = Path.Combine(area.Root, "scratch") }) })
        {
            File.WriteAllText(area.Config, json);
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStorage.LoadAsync(area.Config));
        }
        Assert.AreEqual(0, Directory.GetFileSystemEntries(area.Root).Length);
    }

    [TestMethod]
    public async Task MissingRootOrNetworkMountNeverCreatesLocalReplacement()
    {
        using var area = new Area(); var storage = await area.Configure("Offline"); Directory.Delete(area.Root);
        Assert.Throws<IOException>(() => storage.Prepare()); Assert.IsFalse(Directory.Exists(area.Root));
        Directory.CreateDirectory(area.Root);
        File.WriteAllText(area.Config, JsonSerializer.Serialize(new { projectsRoot = area.Root, projectName = "Offline", temporaryExportRoot = area.Temporary, requiredMount = area.Root }));
        storage = await WorkspaceStorage.LoadAsync(area.Config);
        Assert.Throws<IOException>(() => storage.Prepare());
        Assert.IsFalse(Directory.Exists(storage.ProjectDirectory));
        Assert.IsFalse(WorkspaceStorage.HasNetworkMount("1 0 0:1 / /mnt/share rw - ext4 /dev/root rw", "/mnt/share"));
        Assert.IsTrue(WorkspaceStorage.HasNetworkMount("1 0 0:1 / /mnt/a\\040b rw shared:1 - cifs //host/share rw", "/mnt/a b"));
        Assert.IsFalse(WorkspaceStorage.HasNetworkMount("1 0 0:1 / /mnt/share rw - cifs //host/share rw", "/mnt/other"));
    }

    [TestMethod]
    public async Task LinkedProjectAndOutputPathsCannotEscapeWorkspace()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var area = new Area(); var storage = await area.Configure("Linked");
        Directory.CreateSymbolicLink(storage.ProjectDirectory, area.Temporary);
        Assert.Throws<IOException>(() => storage.Prepare()); Directory.Delete(storage.ProjectDirectory);
        storage.Prepare();
        Directory.Delete(storage.ExportDirectory); Directory.CreateSymbolicLink(storage.ExportDirectory, area.Temporary);
        Assert.Throws<IOException>(() => storage.VerifyAvailable());
    }

    [TestMethod]
    public async Task PublicationIsCompleteNonOverwritingAndCancellationKeepsSource()
    {
        using var area = new Area(); var storage = await area.Configure("Publish"); storage.Prepare();
        string source = Path.Combine(area.Temporary, "encoded.mp4");
        byte[] bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray(); File.WriteAllBytes(source, bytes);
        var job = Guid.NewGuid(); var sequence = Guid.NewGuid();
        string published = await storage.PublishAsync(source, job, sequence, 7, default);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(published));
        await Assert.ThrowsAsync<IOException>(() => storage.PublishAsync(source, job, sequence, 7, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => storage.PublishAsync(source, Guid.NewGuid(), sequence, 7, cancel.Token));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        Assert.AreEqual(1, Directory.GetFiles(storage.ExportDirectory).Length);
    }

    [TestMethod]
    public async Task RealExportsSurviveNextJobAndShutdownAndFailedPublicationRetainsEncodedFile()
    {
        using var area = new Area(); var storage = await area.Configure("Export"); storage.Prepare();
        string durable, failedJobFile;
        using (var host = Open(storage))
        {
            await host.LoadAsync(default, createIfMissing: true);
            var sequence = host.Session.GetProject().Project!.Sequences.Single().Id;
            Assert.IsTrue(host.Session.Execute(new([new SetSequenceDuration(sequence, TimelineTime.TicksPerSecond / 10)], host.Session.GetProject().Revision)).Success);
            using var grant = await host.Boundary.EnableAsync(McpPermission.Edit);
            JsonElement first = await Export(host, grant, sequence);
            Assert.AreEqual("Completed", first.GetProperty("stage").GetString(), first.ToString());
            Assert.AreEqual("Completed", first.GetProperty("publicationStage").GetString());
            durable = first.GetProperty("publishedPath").GetString()!;
            Assert.IsTrue(new FileInfo(durable).Length > 8);
            var second = await Export(host, grant, sequence);
            Assert.AreEqual("Completed", second.GetProperty("stage").GetString(), second.ToString());
            Assert.IsTrue(File.Exists(durable));
            // Failure after admission must remain observable without erasing the encoded file.
            string retained = Path.Combine(area.Temporary, "durable-backup");
            Directory.Move(storage.ExportDirectory, retained); File.WriteAllText(storage.ExportDirectory, "blocked publication");
            JsonElement failed = await Export(host, grant, sequence);
            Assert.AreEqual("Failed", failed.GetProperty("stage").GetString(), failed.ToString());
            Assert.AreEqual("Failed", failed.GetProperty("publicationStage").GetString());
            var job = failed.GetProperty("jobId").GetGuid();
            var read = await host.Boundary.InvokeAsync(grant, "export_read", JsonSerializer.SerializeToElement(new { jobId = job, offset = "0" }), null);
            Assert.IsFalse(read.IsError, read.Error);
            failedJobFile = Path.Combine(storage.TemporaryExportRoot, job.ToString("N") + ".mp4");
            File.Delete(storage.ExportDirectory); Directory.Move(retained, storage.ExportDirectory);
            Assert.IsTrue(File.Exists(durable));
            JsonElement final = await Export(host, grant, sequence);
            durable = final.GetProperty("publishedPath").GetString()!;
            Assert.IsTrue(File.Exists(failedJobFile));
            await host.Jobs!.DisposeAsync();
        }
        Assert.IsTrue(File.Exists(durable)); Assert.IsTrue(File.Exists(failedJobFile));
        Assert.AreEqual(0, Directory.GetFiles(storage.ExportDirectory, "*.tmp").Length);
    }

    [TestMethod]
    public async Task WorkspaceUploadRegistersPortableRelativeMediaWithoutChangingLegacyUploads()
    {
        using var area = new Area(); var storage = await area.Configure("Media"); storage.Prepare();
        using var host = Open(storage); await host.LoadAsync(default, createIfMissing: true);
        using var upload = new MediaUploadStore(storage.MediaDirectory, new Probe(), projectDirectory: storage.ProjectDirectory);
        byte[] bytes = [1, 2, 3]; var id = Guid.NewGuid();
        upload.Begin(id, "voice.wav", bytes.Length, MediaUploadStore.Hash(bytes), host.Session.GetProject());
        upload.Write(id, 0, Convert.ToBase64String(bytes), MediaUploadStore.Hash(bytes));
        var prepared = await upload.PrepareAsync(id, default); Assert.IsTrue(upload.Finish(prepared, host.Session, default).Success);
        host.Save();
        var asset = host.Session.GetProject().Project!.Assets.Single();
        Assert.IsFalse(Path.IsPathFullyQualified(asset.SourcePath));
        Assert.IsTrue(asset.SourcePath.StartsWith("media" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(storage.ProjectDirectory, asset.SourcePath)));
    }

    private static HeadlessSession Open(WorkspaceStorage storage) => new(storage.ProjectPath, writable: true, autosave: true,
        outputRoot: storage.TemporaryExportRoot, workspace: storage);
    private static async Task<JsonElement> Export(HeadlessSession host, CapabilityGrant grant, Guid sequence)
    {
        var begin = await host.Boundary.InvokeAsync(grant, "export_begin", JsonSerializer.SerializeToElement(new { sequenceId = sequence }), host.Guard());
        Assert.IsFalse(begin.IsError, begin.Error); var jobId = begin.Value!.Value.GetProperty("jobId").GetGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            await Task.Delay(20, deadline.Token);
            var reply = await host.Boundary.InvokeAsync(grant, "export_status", JsonSerializer.SerializeToElement(new { jobId }), null, deadline.Token);
            Assert.IsFalse(reply.IsError, reply.Error); var status = reply.Value!.Value;
            if (status.GetProperty("stage").GetString() is "Completed" or "Failed" or "Cancelled") return status;
        }
    }
    private sealed class Probe : IMediaProbe
    {
        public Task<Result<MediaProbeInfo>> ProbeAsync(string path, CancellationToken token = default) =>
            Task.FromResult(Result<MediaProbeInfo>.Ok(new(path, MediaKind.Wav, TimelineTime.TicksPerSecond, 48000, 2, null, null, null, ["pcm_s16le"])));
    }
    private sealed class Area : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kachinco-workspace-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(directory, "projects");
        public string Temporary => Path.Combine(directory, "jobs");
        public string Config => Path.Combine(directory, "workspace.json");
        public Area() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Temporary); }
        public async Task<WorkspaceStorage> Configure(string name)
        {
            File.WriteAllText(Config, JsonSerializer.Serialize(new { projectsRoot = Root, projectName = name, temporaryExportRoot = Temporary }));
            return await WorkspaceStorage.LoadAsync(Config);
        }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
