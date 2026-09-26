using System.Collections.Immutable;
using System.Text.Json;
using Flamoris.Logging;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class LoggingIntegrationTests
{
    [TestMethod]
    public void ApplicationConfigurationBindsLevelCategoriesOutputsAndRotation()
    {
        using var temp = new TempDirectory();
        var configuration = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(configuration, """
        {
          "logging": {
            "level": "info",
            "categories": { "mcp": "warn", "mcp.transport": "debug" },
            "outputs": [
              {
                "type": "file",
                "path": "logs/custom.log",
                "format": "text",
                "rotation": { "enabled": true, "maxFileSizeMb": 7, "maxFiles": 4 }
              }
            ]
          }
        }
        """);

        var options = KachincoLogging.LoadOptions(configuration);

        Assert.AreEqual("info", options.Level);
        Assert.AreEqual("warn", options.Categories["mcp"]);
        Assert.AreEqual("debug", options.Categories["mcp.transport"]);
        Assert.AreEqual("logs/custom.log", options.Outputs[0].Path);
        Assert.AreEqual(7, options.Outputs[0].Rotation.MaxFileSizeMb);
        Assert.AreEqual(4, options.Outputs[0].Rotation.MaxFiles);
    }

    [TestMethod]
    public void StartupCreatesLoggerUnderExplicitWritableBasePath()
    {
        using var temp = new TempDirectory();
        var configuration = WriteFileConfiguration(temp.Path, "startup.log");

        var host = KachincoLogging.Create(configurationPath: configuration, basePath: temp.Path);
        host.Logger.Info("app.startup", "Application starting", new Dictionary<string, object?> { ["version"] = "test" });

        Assert.AreEqual(Path.GetFullPath(temp.Path), Path.GetFullPath(host.BasePath));
        var text = File.ReadAllText(Path.Combine(temp.Path, "logs", "startup.log"));
        StringAssert.Contains(text, "[INFO ] [app.startup]");
        StringAssert.Contains(text, "version=test");
    }

    [TestMethod]
    public void InvalidFileOutputNeverEscapesIntoHost()
    {
        var options = new LoggingOptions
        {
            Outputs = [new() { Type = "file", Path = "\0invalid" }],
        };

        var logger = FlamorisLogger.Create(options, basePath: "\0invalid");
        logger.Info("app.startup", "Application starting");
    }

    [TestMethod]
    public async Task ExceptionsAndProjectFailuresUseStructuredDiagnostics()
    {
        using var temp = new TempDirectory();
        var configuration = WriteFileConfiguration(temp.Path, "diagnostics.log");
        var logger = KachincoLogging.Create(configurationPath: configuration, basePath: temp.Path).Logger;
        var store = new ProjectFileStore(logger);

        const string sentinel = "PRIVATE-PROJECT-PATH-SENTINEL";
        var result = await store.LoadAsync(Path.Combine(temp.Path, sentinel, "missing.fkproj"));
        logger.Error("preview", "Preview generation failed", new InvalidOperationException("decoder failed"),
            new Dictionary<string, object?> { ["sequenceId"] = Guid.Empty });

        Assert.IsFalse(result.Success);
        var text = File.ReadAllText(Path.Combine(temp.Path, "logs", "diagnostics.log"));
        StringAssert.Contains(text, "[ERROR] [document.open]");
        StringAssert.Contains(text, "diagnosticCode=PROJECT_READ_FAILED");
        StringAssert.Contains(text, "errorType=DirectoryNotFoundException");
        Assert.IsFalse(text.Contains(sentinel, StringComparison.Ordinal));
        StringAssert.Contains(text, "[ERROR] [preview]");
        StringAssert.Contains(text, "System.InvalidOperationException");
    }

    [TestMethod]
    public async Task McpLifecycleTransportRevisionAndPermissionEventsAreStructured()
    {
        using var temp = new TempDirectory();
        var configuration = WriteFileConfiguration(temp.Path, "mcp.log");
        var logger = KachincoLogging.Create(configurationPath: configuration, basePath: temp.Path).Logger;
        using var h = new McpCoreHarness(logger: logger);
        using var grant = await h.Boundary.EnableAsync(Flamoris.Mcp.Core.McpPermission.Edit);
        string credential = grant.ExportCredential();
        var stale = await h.Call(grant, "edit_batch", h.Batch(), h.Guard() with { ExpectedRevision = 0 });
        Assert.AreEqual(Flamoris.Mcp.Core.McpErrors.StaleRevision, stale.Error);
        h.Boundary.Disable();
        var text = File.ReadAllText(Path.Combine(temp.Path, "logs", "mcp.log"));
        StringAssert.Contains(text, "[mcp.auth]");
        StringAssert.Contains(text, "outcome=enabled");
        StringAssert.Contains(text, "outcome=revoked");
        StringAssert.Contains(text, "outcome=stale_revision");
        Assert.IsFalse(text.Contains(credential, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("expectedRevision", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("SetTrackEnabled", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BridgeUsesASeparateFileAndNeverConsoleOutput()
    {
        using var temp = new TempDirectory();
        var configuration = WriteFileConfiguration(temp.Path, "kachinco.log", includeConsole: true);

        var host = KachincoLogging.Create("mcp-bridge", configuration, temp.Path);

        Assert.IsFalse(host.Options.Outputs.Any(output =>
            string.Equals(output.Type, "console", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(Path.Combine("logs", "kachinco-mcp.log"), host.Options.Outputs.Single().Path);
    }

    [TestMethod]
    public async Task PreviewLifecycleClipTransitionAndCacheOutcomeAreStructured()
    {
        using var temp = new TempDirectory();
        var configuration = WriteFileConfiguration(temp.Path, "preview.log");
        var logger = KachincoLogging.Create(configurationPath: configuration, basePath: temp.Path).Logger;
        var path = Path.Combine(temp.Path, "source.mov"); File.WriteAllText(path, "decoder fixture");
        var fixture = new Fixture(); Assert.IsTrue(fixture.Edit(new RelinkMedia(fixture.MovId, path, 10 * Fixture.T)).Success);
        var context = InteractivePreviewTests.Context(fixture);
        var decoder = new PreviewDecoder();
        using (var source = new InteractivePreviewSource(randomDecoder: decoder, forwardVideo: decoder, forwardAudio: decoder, logger: logger))
        {
            using var firstOwner = new CancellationTokenSource();
            using var secondOwner = new CancellationTokenSource();
            Assert.IsTrue((await source.FrameAsync(context, 0, PreviewQuality.Quarter, true, firstOwner.Token)).Success);
            Assert.IsTrue((await source.FrameAsync(context, 0, PreviewQuality.Quarter, true, secondOwner.Token)).Success);
        }
        using (var preview = new InteractivePreview(new PreviewSource(), () => new PreviewDevice(), logger))
        {
            preview.SetContext(context); await preview.Completion;
            preview.Play(); await Until(() => preview.State == InteractivePreviewState.Playing);
            preview.Dispose(); await preview.Completion;
        }

        var text = File.ReadAllText(Path.Combine(temp.Path, "logs", "preview.log"));
        StringAssert.Contains(text, "[INFO ] [preview.transition] Active preview video contributors changed");
        StringAssert.Contains(text, $"activeClipId={fixture.ClipId}");
        StringAssert.Contains(text, $"mediaAssetId={fixture.MovId}");
        StringAssert.Contains(text, "sourceTick=");
        StringAssert.Contains(text, "cache=miss");
        StringAssert.Contains(text, "cache=hit");
        StringAssert.Contains(text, "[INFO ] [preview.playback] Preview playback session started");
        StringAssert.Contains(text, "[INFO ] [preview.playback] Preview playback session stopped");
        StringAssert.Contains(text, "playbackSessionId=");
    }

    private static string WriteFileConfiguration(string directory, string fileName, bool includeConsole = false)
    {
        var path = Path.Combine(directory, "appsettings.json");
        var outputs = includeConsole
            ? """[{"type":"console"},{"type":"file","path":"logs/kachinco.log","format":"text"}]"""
            : $"[{{\"type\":\"file\",\"path\":\"logs/{fileName}\",\"format\":\"text\"}}]";
        File.WriteAllText(path, $$"""
        {
          "logging": {
            "level": "debug",
            "categories": { "mcp": "debug" },
            "outputs": {{outputs}}
          }
        }
        """);
        return path;
    }

    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) Assert.Fail("Preview did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    private sealed class PreviewDecoder : IMediaDecoder
    {
        public Task<ImmutableArray<byte>> VideoAsync(string path, long sourceTicks, int width, int height, CancellationToken token)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return Task.FromResult(pixels.ToImmutableArray());
        }
        public Task<ImmutableArray<float>> AudioAsync(string path, long sourceTicks, int count, int rate, int channels, CancellationToken token) =>
            Task.FromResult(new float[count * channels].ToImmutableArray());
    }

    private sealed class PreviewSource : IInteractivePreviewSource
    {
        public ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext context, long tick, PreviewQuality quality, bool forward, CancellationToken token) =>
            ValueTask.FromResult(Result<RenderedVideoFrame>.Ok(new(0, tick, 1, 1, [0, 0, 0, 255])));
        public ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext context, long firstSample, int count, CancellationToken token) =>
            ValueTask.FromResult(Result<RenderedAudioBlock>.Ok(new(firstSample, 48000, 2, new float[count * 2].ToImmutableArray())));
    }

    private sealed class PreviewDevice : IPreviewAudioOutput
    {
        private long queued;
        public long PlayedFrames => 0;
        public long QueuedFrames => queued;
        public void Enqueue(RenderedAudioBlock block) => queued += block.Samples.Length / block.Channels;
        public void Play() { }
        public void Pause() { }
        public void Dispose() { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Kachinco.Logging.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
