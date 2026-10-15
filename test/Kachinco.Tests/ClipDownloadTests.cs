using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class ClipDownloadTests
{
    [TestMethod]
    public void CatalogMatchesProducerWireDescriptor()
    {
        using var h = new Harness();
        var describe = typeof(McpBoundary).Assembly.GetType("Flamoris.Mcp.Core.McpProtocol")!
            .GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var tools = KachincoMcpTools.Create(h.Fixture.Session, () => new {}, () => {})
            .Where(t => t.Name == "edit_batch").Concat(ClipDownloadMcpTools.Create(h.Fixture.Session, h.Store, () => null))
            .Select(t => (ModelContextProtocol.Protocol.Tool)describe.Invoke(null, [t])!).ToArray();
        var actual = JsonSerializer.SerializeToElement(tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }));
        if (Environment.GetEnvironmentVariable("KACHINCO_EXPORT_CLIP_CATALOG") is { } export)
            File.WriteAllText(export, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip-download-tools.json"));
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected),
            System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText())), "Update producer and Hub fixtures together.");
    }

    [TestMethod]
    public async Task RealWavTrimGainBytesMetadataAndReadOnlyHistoryAreConsistent()
    {
        using var h = new Harness(real: true); using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        Guid bgm = Guid.NewGuid();
        Assert.IsTrue(h.Fixture.Edit(new AddTrack(h.Fixture.SequenceId, bgm, "Unrelated BGM", TrackKind.Audio),
            new InsertClip(h.Fixture.SequenceId, bgm, Fixture.Clip(Guid.NewGuid(), h.Fixture.WavId, 0, 2 * Fixture.T, Fixture.T) with { Audio = new(16, false) })).Success);
        var before = h.Fixture.Session.GetProject(); string saved = ProjectJson.Serialize(before.Project!).Value!;
        var result = await h.Begin(grant); Assert.IsFalse(result.IsError, result.Error);
        Guid id = result.Value!.Value.GetProperty("downloadId").GetGuid();
        var ready = await h.Ready(grant, id); var metadata = ready.GetProperty("metadata");
        Assert.AreEqual("audio/wav", metadata.GetProperty("mimeType").GetString());
        Assert.AreEqual(before.Revision.ToString(), metadata.GetProperty("projectRevision").GetString());
        using var output = new MemoryStream();
        while (output.Length < long.Parse(metadata.GetProperty("sizeBytes").GetString()!))
        {
            var chunk = await h.Call(grant, "clip_download_read", new { downloadId = id, offset = output.Length.ToString(), count = 1024 });
            Assert.IsFalse(chunk.IsError, chunk.Error); var value = chunk.Value!.Value;
            byte[] bytes = Convert.FromBase64String(value.GetProperty("dataBase64").GetString()!);
            Assert.AreEqual(MediaUploadStore.Hash(bytes), value.GetProperty("chunkSha256").GetString());
            output.Write(bytes);
        }
        byte[] wav = output.ToArray();
        Assert.AreEqual(MediaUploadStore.Hash(wav), metadata.GetProperty("sha256").GetString());
        Assert.AreEqual(44 + 12000 * 4, wav.Length);
        foreach (int frame in new[] { 0, 5999, 11999 })
            Assert.AreEqual(0.3, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + frame * 4)) / 32767d, 0.0002);
        Assert.AreEqual(saved, ProjectJson.Serialize(h.Fixture.Project).Value);
        Assert.AreEqual(before.Revision, h.Fixture.Session.GetProject().Revision);
        Assert.AreEqual(before.CanUndo, h.Fixture.Session.GetProject().CanUndo);
        Assert.IsTrue((await h.Call(grant, "clip_download_read", new { downloadId = id, offset = "0", count = ClipDownloadStore.MaximumChunkBytes + 1 })).IsError);
        Assert.IsTrue((await h.Call(grant, "clip_download_read", new { downloadId = id, offset = wav.Length.ToString() })).IsError);
        Assert.IsFalse((await h.Call(grant, "clip_download_cancel", new { downloadId = id })).IsError);
    }

    [TestMethod]
    public async Task DisabledInspectionStaleRevisionDocumentExpiryAndRevocationAreBounded()
    {
        var clock = new Clock();
        using var h = new Harness(clock: clock); using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        Assert.IsTrue(h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.AudioTrackId, false)).Success);
        Guid id = (await h.Begin(grant)).Value!.Value.GetProperty("downloadId").GetGuid();
        await h.Ready(grant, id);
        var chunk = await h.Call(grant, "clip_download_read", new { downloadId = id, offset = "44", count = 100 });
        Assert.IsTrue(Convert.FromBase64String(chunk.Value!.Value.GetProperty("dataBase64").GetString()!).All(b => b == 0));
        await h.Call(grant, "clip_download_cancel", new { downloadId = id });
        id = (await h.Begin(grant, inspect: true)).Value!.Value.GetProperty("downloadId").GetGuid(); await h.Ready(grant, id);
        chunk = await h.Call(grant, "clip_download_read", new { downloadId = id, offset = "44", count = 100 });
        Assert.IsTrue(Convert.FromBase64String(chunk.Value!.Value.GetProperty("dataBase64").GetString()!).Any(b => b != 0));
        Assert.IsFalse(h.Fixture.Project.Sequences[0].Tracks[1].Enabled);
        Assert.IsTrue(h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.AudioTrackId, true)).Success);
        Assert.AreEqual(McpErrors.StaleRevision, (await h.Call(grant, "clip_download_status", new { downloadId = id })).Error);
        id = (await h.Begin(grant)).Value!.Value.GetProperty("downloadId").GetGuid(); await h.Ready(grant, id);
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.AreEqual("download_not_found", (await h.Call(grant, "clip_download_status", new { downloadId = id })).Error);
        id = (await h.Begin(grant)).Value!.Value.GetProperty("downloadId").GetGuid(); await h.Ready(grant, id);
        Assert.IsTrue(h.Fixture.Session.ReplaceProject(h.Fixture.Project).Success);
        Assert.IsTrue((await h.Call(grant, "clip_download_status", new { downloadId = id })).IsError);
        grant.Dispose(); h.Store.Dispose();
        Assert.IsTrue((await h.Begin(grant)).IsError);
    }

    [TestMethod]
    public async Task SilentVideoUsesSharedEvaluationTransformSpeedAndRealMedia()
    {
        using var h = new Harness(real: true); using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        Assert.IsTrue(h.Fixture.Edit(new TrimClip(h.Fixture.SequenceId, h.Fixture.ClipId, 0, 0, 2 * Fixture.T),
            new SetClipSpeed(h.Fixture.SequenceId, h.Fixture.ClipId, 8),
            new SetClipProperties(h.Fixture.SequenceId, h.Fixture.ClipId, true,
                new(new(10, 0, 1, 1, 0), .5, BlendMode.Normal), AudioProperties.Default)).Success);
        var generate = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=480x270:rate=30:duration=2", "-an", "-c:v", "libx264", "-threads", "1", "-pix_fmt", "yuv420p", Path.Combine(h.Root,"input.mov") }) generate.ArgumentList.Add(arg);
        using (var generation = Process.Start(generate)!) { string error = await generation.StandardError.ReadToEndAsync(); await generation.WaitForExitAsync(); Assert.AreEqual(0, generation.ExitCode, error); }
        var result = await h.Call(grant, "clip_download_begin", new { sequenceId = h.Fixture.SequenceId, clipId = h.Fixture.ClipId, quality = "Quarter" });
        Assert.IsFalse(result.IsError, result.Error);
        Guid id = result.Value!.Value.GetProperty("downloadId").GetGuid(); var ready = await h.Ready(grant, id);
        var metadata = ready.GetProperty("metadata");
        Assert.AreEqual(480, metadata.GetProperty("width").GetInt32());
        Assert.AreEqual(270, metadata.GetProperty("height").GetInt32());
        Assert.IsFalse(metadata.GetProperty("embeddedAudio").GetBoolean());
        using var evaluator = TimelineEvaluator.Create(h.Fixture.Project, h.Fixture.SequenceId).Value!;
        Assert.AreEqual(0L, evaluator.Evaluate(0).Value!.VideoLayers[0].SourceTicks);
        Assert.AreEqual(8 * Fixture.T / 10, evaluator.Evaluate(Fixture.T / 10).Value!.VideoLayers[0].SourceTicks);
        var bytes = await h.Call(grant, "clip_download_read", new { downloadId = id, offset = "0" });
        string path = Path.Combine(h.Root, "download.mp4");
        File.WriteAllBytes(path, Convert.FromBase64String(bytes.Value!.Value.GetProperty("dataBase64").GetString()!));
        var probe = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-show_streams", "-of", "json", path }) probe.ArgumentList.Add(arg);
        using var process = Process.Start(probe)!; string output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode); var streams = JsonDocument.Parse(output).RootElement.GetProperty("streams");
        Assert.AreEqual(1, streams.GetArrayLength()); Assert.AreEqual("video", streams[0].GetProperty("codec_type").GetString());
        Assert.AreEqual(metadata.GetProperty("frameCount").GetString(), streams[0].GetProperty("nb_frames").GetString());
        using var decoder = new FfmpegMediaDecoder();
        var renderer = new SharedFrameRenderer(decoder, Path.Combine(h.Root, "project.fkproj"));
        // Compare the encoded first/middle/last frames against the same preview evaluation.
        long count = long.Parse(metadata.GetProperty("frameCount").GetString()!);
        foreach (long index in new[] { 0L, count / 2, count - 1 })
        {
            long tick = TimelineTime.FrameToTicks(index, new(30,1));
            var expected = await renderer.RenderPreviewAsync(h.Fixture.Project, evaluator.Evaluate(tick).Value!, PreviewQuality.Quarter, default);
            var actual = await decoder.VideoAsync(path, tick, 480, 270, default);
            double difference = Enumerable.Range(0, actual.Length).Where(i => i % 4 != 3).Average(i => Math.Abs(actual[i] - expected.Value!.Rgba8[i]));
            Assert.IsLessThan(12d, difference, "Lossy H.264 output must match edited preview.");
        }
    }

    [TestMethod]
    public async Task InvalidIdsRangesMissingSourceChangesAndCancelFailWithoutProjectEdits()
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        foreach (var input in new object[] {
            new { sequenceId = Guid.NewGuid(), clipId = h.Fixture.AudioClipId },
            new { sequenceId = h.Fixture.SequenceId, clipId = Guid.NewGuid() },
            new { sequenceId = h.Fixture.SequenceId, clipId = h.Fixture.AudioClipId, startTicks = "-1" },
            new { sequenceId = h.Fixture.SequenceId, clipId = h.Fixture.AudioClipId, durationTicks = "0" },
            new { sequenceId = h.Fixture.SequenceId, clipId = h.Fixture.AudioClipId, durationTicks = long.MaxValue.ToString() },
            new { sequenceId = h.Fixture.SequenceId, clipId = h.Fixture.AudioClipId, outputPath = "other.wav" }
        }) Assert.IsTrue((await h.Call(grant, "clip_download_begin", input)).IsError);
        var id = (await h.Begin(grant)).Value!.Value.GetProperty("downloadId").GetGuid();
        Assert.AreEqual("download_limit", (await h.Begin(grant)).Error); await h.Ready(grant, id);
        File.AppendAllText(Path.Combine(h.Root, "input.wav"), "changed");
        Assert.AreEqual("source_changed", (await h.Call(grant, "clip_download_read", new { downloadId = id, offset = "0" })).Error);
        File.Delete(Path.Combine(h.Root, "input.wav")); Assert.AreEqual("MEDIA_MISSING", (await h.Begin(grant)).Error);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("revoke")]
    [DataRow("edit")]
    public async Task InFlightCancellationRevocationAndEditingJoinRenderAndRemoveTemporaryBytes(string mode)
    {
        using var h = new Harness(); using var grant = await h.Boundary.EnableAsync(McpPermission.ReadOnly);
        h.Decoder.PauseAudio = true;
        Guid id = (await h.Begin(grant)).Value!.Value.GetProperty("downloadId").GetGuid();
        await h.Decoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (mode == "cancel") Assert.IsFalse((await h.Call(grant, "clip_download_cancel", new { downloadId = id })).IsError);
        else if (mode == "revoke") grant.Dispose();
        else
        {
            h.Fixture.Edit(new SetTrackEnabled(h.Fixture.SequenceId, h.Fixture.VideoTrackId, false));
            Assert.AreEqual(McpErrors.StaleRevision, (await h.Call(grant, "clip_download_status", new { downloadId = id })).Error);
        }
        await h.Store.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories).Count(p => p != Path.Combine(h.Root, "input.wav")));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Decoder : IMediaDecoder
    {
        public List<long> VideoTicks { get; } = [];
        public bool PauseAudio;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token)
        {
            VideoTicks.Add(sourceTicks); var bytes = new byte[width * height * 4];
            for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 200; bytes[i + 3] = 255; }
            return Task.FromResult(ImmutableArray.CreateRange(bytes));
        }
        public async Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token)
        {
            if (PauseAudio) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return ImmutableArray.CreateRange(Enumerable.Repeat(.5f, count * channels));
        }
    }
    private sealed class Harness : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "kachinco-download-test-" + Guid.NewGuid().ToString("N"));
        public Fixture Fixture { get; } = new();
        public Decoder Decoder { get; } = new();
        public ClipDownloadStore Store { get; }
        public KachincoMcpHost Host { get; }
        public McpBoundary Boundary { get; }
        private readonly SemaphoreSlim lane = new(1);
        public Harness(bool real = false, TimeProvider? clock = null)
        {
            Directory.CreateDirectory(Root);
            // The actual source is PCM16: first 2 seconds quiet, then 0.5 amplitude.
            byte[] wav = new byte[44 + 48000 * 10 * 4];
            "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
            "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16),16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20),1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22),2);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24),48000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28),192000);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32),4); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34),16);
            "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40),wav.Length-44);
            for (int i = 0; i < 48000 * 10 * 2; i++) BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), (short)(i < 48000 * 2 * 2 ? 3277 : 16384));
            File.WriteAllBytes(Path.Combine(Root, "input.wav"), wav);
            Fixture.Edit(new TrimClip(Fixture.SequenceId, Fixture.AudioClipId, 0, 2 * global::Kachinco.Tests.Fixture.T, global::Kachinco.Tests.Fixture.T / 4),
                new SetClipProperties(Fixture.SequenceId, Fixture.AudioClipId, true, ClipAppearance.Default, new(.6, false)));
            Store = new(new(1, 1), Root, clock, real ? null : () => Decoder);
            Host = new(Fixture.Session, () => false, async (action, token) => {
                await lane.WaitAsync(token); try { token.ThrowIfCancellationRequested(); action(); } finally { lane.Release(); }
            });
            Boundary = new(Host, ClipDownloadMcpTools.Create(Fixture.Session, Store, () => Path.Combine(Root, "project.fkproj")), new(), new(KachincoLogging.Create().Logger));
        }
        public Task<McpResult> Call(CapabilityGrant grant, string tool, object input) => Boundary.InvokeAsync(grant, tool,
            JsonSerializer.SerializeToElement(input), new(Host.Snapshot.RuntimeId, Host.Snapshot.DocumentToken, Host.Snapshot.Revision), default);
        public Task<McpResult> Begin(CapabilityGrant grant, bool inspect = false)
        { grant.Revoked.Register(Store.Dispose); return Call(grant, "clip_download_begin", new { sequenceId = Fixture.SequenceId, clipId = Fixture.AudioClipId, inspectDisabled = inspect }); }
        public async Task<JsonElement> Ready(CapabilityGrant grant, Guid id)
        {
            for (int i = 0; i < 2000; i++)
            {
                var result = await Call(grant, "clip_download_status", new { downloadId = id }); Assert.IsFalse(result.IsError, result.Error);
                var value = result.Value!.Value;
                if (value.GetProperty("state").GetString() == "ready") return value;
                Assert.AreEqual("rendering", value.GetProperty("state").GetString(), value.ToString()); await Task.Delay(10);
            }
            Assert.Fail("Timed out waiting for render."); return default;
        }
        public void Dispose()
        {
            Boundary.Dispose(); Host.Dispose(); Store.Dispose(); Store.Completion.GetAwaiter().GetResult(); Fixture.Session.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
