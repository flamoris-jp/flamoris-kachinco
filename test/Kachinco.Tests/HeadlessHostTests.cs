using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Cli;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class HeadlessHostTests
{
    [TestMethod]
    public async Task SharedEditCheckpointUndoAndReadOnlyGuards()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "movie.fkproj");
            var initial = new Fixture();
            Assert.IsTrue((await new ProjectFileStore().SaveAsync(path, initial.Project)).Success);
            using var host = new HeadlessSession(path, writable: true, autosave: true, outputRoot: dir);
            await host.LoadAsync(default);
            Assert.Throws<IOException>(() => new HeadlessSession(path, writable: true));
            var read = await host.Boundary.EnableAsync(McpPermission.ReadOnly);
            var forbidden = await host.Boundary.InvokeAsync(read, "project_save", JsonSerializer.SerializeToElement(new { }), host.Guard());
            Assert.IsTrue(forbidden.IsError);
            var grant = await host.Boundary.EnableAsync(McpPermission.Edit);
            var stale = host.Guard();
            var result = await host.Boundary.InvokeAsync(grant, "edit_batch", JsonSerializer.SerializeToElement(new { commands = new[] {
                new { type = "SetTrackEnabled", sequenceId = initial.SequenceId, trackId = initial.VideoTrackId, enabled = false } } }), host.Guard());
            Assert.IsFalse(result.IsError);
            var loaded = await new ProjectFileStore().LoadAsync(path);
            Assert.IsFalse(loaded.Value!.Sequences[0].Tracks.Single(t => t.Id == initial.VideoTrackId).Enabled);
            Assert.IsFalse(host.Dirty);
            var conflict = await host.Boundary.InvokeAsync(grant, "undo", JsonSerializer.SerializeToElement(new { }), stale);
            Assert.IsTrue(conflict.IsError);
            var undo = await host.Boundary.InvokeAsync(grant, "undo", JsonSerializer.SerializeToElement(new { }), host.Guard());
            Assert.IsFalse(undo.IsError);
            loaded = await new ProjectFileStore().LoadAsync(path);
            Assert.IsTrue(loaded.Value!.Sequences[0].Tracks.Single(t => t.Id == initial.VideoTrackId).Enabled);
            await host.Jobs!.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task SaveAsKeepsRelativeMediaMeaningAndRejectsSourceOverwrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-saveas-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "other"));
            var initial = new Fixture();
            var p = initial.Project with { Assets = [.. initial.Project.Assets.Select(a => a with { SourcePath = a.Kind == MediaKind.Mov ? "video.mov" : "audio.wav" })] };
            string input = Path.Combine(dir, "movie.fkproj"), output = Path.Combine(dir, "other", "saved.fkproj");
            Assert.IsTrue((await new ProjectFileStore().SaveAsync(input, p)).Success);
            var bytes = await File.ReadAllBytesAsync(input);
            using var host = new HeadlessSession(input, output, writable: true); await host.LoadAsync(default); host.Save();
            var saved = await new ProjectFileStore().LoadAsync(output);
            Assert.IsTrue(saved.Success);
            Assert.IsTrue(saved.Value!.Assets.All(a => Path.IsPathFullyQualified(a.SourcePath)));
            Assert.IsTrue(saved.Value.Assets.All(a => Path.GetDirectoryName(a.SourcePath) == dir));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input));
            Assert.Throws<IOException>(() => CliOperations.NewOutput(input));
            var savedBytes = await File.ReadAllBytesAsync(output);
            Assert.Throws<IOException>(() => host.Save());
            CollectionAssert.AreEqual(savedBytes, await File.ReadAllBytesAsync(output));
            if (OperatingSystem.IsLinux())
            {
                var windows = p with { Assets = [.. p.Assets.Select(a => a with { SourcePath = @"C:\Media\" + a.Name })] };
                Assert.IsTrue(CliOperations.InspectMedia(windows, input).All(a => !a.IsAvailable && a.ResolvedPath is null));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task ExportJobUsesSnapshotAndOpaqueBoundedArtifact()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-job-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "movie.fkproj"), outputs = Path.Combine(dir, "exports");
            using var session = new EditorSession(); var sequence = Guid.NewGuid();
            Assert.IsTrue(session.Execute(new([new CreateProject(Guid.NewGuid(), "Job"),
                new CreateSequence(sequence, "Main", SequenceSettings.Landscape, TimelineTime.TicksPerSecond / 10)])).Success);
            Assert.IsTrue((await new ProjectFileStore().SaveAsync(path, session.GetProject().Project!)).Success);
            using var host = new HeadlessSession(path, writable: true, outputRoot: outputs); await host.LoadAsync(default);
            await using var jobs = host.Jobs!;
            var readOnly = await host.Boundary.EnableAsync(McpPermission.ReadOnly);
            var forbidden = await host.Boundary.InvokeAsync(readOnly, "export_begin", JsonSerializer.SerializeToElement(new { sequenceId = sequence }), host.Guard());
            Assert.AreEqual(McpErrors.Forbidden, forbidden.Error);
            var grant = await host.Boundary.EnableAsync(McpPermission.Edit);
            var begin = await host.Boundary.InvokeAsync(grant, "export_begin", JsonSerializer.SerializeToElement(new { sequenceId = sequence }), host.Guard());
            Assert.IsFalse(begin.IsError);
            var jobId = begin.Value!.Value.GetProperty("jobId").GetGuid();
            string revision = begin.Value.Value.GetProperty("revision").GetString()!;
            // A later ordinary edit cannot alter the admitted export snapshot.
            Assert.IsTrue(host.Session.Execute(new([new SetSequenceDuration(sequence, TimelineTime.TicksPerSecond / 5)], host.Session.GetProject().Revision)).Success);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            JsonElement status;
            do
            {
                await Task.Delay(20, deadline.Token);
                var response = await host.Boundary.InvokeAsync(grant, "export_status", JsonSerializer.SerializeToElement(new { jobId }), null, deadline.Token);
                Assert.IsFalse(response.IsError); status = response.Value!.Value;
            } while (status.GetProperty("stage").GetString() is "Planning" or "Rendering" or "Encoding");
            Assert.AreEqual("Completed", status.GetProperty("stage").GetString(), status.ToString());
            Assert.AreEqual(revision, status.GetProperty("revision").GetString());
            var chunk = await host.Boundary.InvokeAsync(grant, "export_read", JsonSerializer.SerializeToElement(new { jobId, offset = "0" }), null);
            Assert.IsFalse(chunk.IsError, chunk.Error); var bytes = Convert.FromBase64String(chunk.Value!.Value.GetProperty("data").GetString()!);
            Assert.IsTrue(bytes.Length is > 8 and <= 196608);
            Assert.AreEqual("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
            var wrong = await host.Boundary.InvokeAsync(grant, "export_read", JsonSerializer.SerializeToElement(new { jobId = Guid.NewGuid(), offset = "0" }), null);
            Assert.AreEqual("export_not_found", wrong.Error);
            await jobs.DisposeAsync(); Assert.AreEqual(0, Directory.GetFiles(outputs).Length); Assert.IsTrue(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task LinuxCaptionRgbaHasTransparencyColorAndCancellation()
    {
        if (!OperatingSystem.IsLinux()) return; // the Windows WPF path has its own raster gate
        var rasterizer = new LinuxCaptionRasterizer();
        var caption = new EvaluatedCaption(Guid.NewGuid(), Guid.NewGuid(), "Kachinco\nCLI") {
            Style = new(FontFamily: "sans-serif", FontSize: 140, Color: "#FF0000", OutlineWidth: 0, X: .5, Y: .5, VerticalAnchor: "Center") };
        var rgba = await rasterizer.RasterizeAsync([caption], 320, 180, default);
        Assert.AreEqual(320 * 180 * 4, rgba.Length);
        Assert.IsTrue(Enumerable.Range(0, 320 * 180).Any(i => rgba[i * 4] > 200 && rgba[i * 4 + 3] > 200));
        Assert.IsTrue(Enumerable.Range(0, 320 * 180).Any(i => rgba[i * 4 + 3] == 0));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await rasterizer.RasterizeAsync([caption], 320, 180, cancel.Token));
    }
}
