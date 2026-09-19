using System.Text.Json;
using Flamoris.Logging;
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
        var transport = new McpTransportDiagnostics(logger, "test-pipe");
        transport.EndpointStarted();
        transport.ClientAttached();
        transport.ConnectionFailed(new IOException("connection lost"));
        transport.ClientDetached();
        transport.EndpointStopped();

        var fixture = new Fixture();
        using (var editLease = new McpAccessLease(fixture.Session, McpPermission.Edit))
        {
            var adapter = new McpEditorAdapter(fixture.Session, () => new { }, () => { }, editLease, logger);
            await adapter.HandleAsync(McpLeaseTests.Initialize);
            await adapter.HandleAsync(McpLeaseTests.Call("edit_batch", new
            {
                expectedRevision = "0",
                commands = new object[]
                {
                    new { type = "SetTrackEnabled", sequenceId = fixture.SequenceId, trackId = fixture.VideoTrackId, enabled = false },
                },
            }));
        }
        using (var readLease = new McpAccessLease(fixture.Session, McpPermission.ReadOnly))
        {
            var adapter = new McpEditorAdapter(fixture.Session, () => new { }, () => { }, readLease, logger);
            await adapter.HandleAsync(McpLeaseTests.Initialize);
            await adapter.HandleAsync(McpLeaseTests.Call("undo", new
            {
                expectedRevision = fixture.Session.GetProject().Revision.ToString(),
            }));
        }

        var text = File.ReadAllText(Path.Combine(temp.Path, "logs", "mcp.log"));
        StringAssert.Contains(text, "[INFO ] [mcp.transport] MCP endpoint started");
        StringAssert.Contains(text, "[INFO ] [mcp.transport] MCP client attached");
        StringAssert.Contains(text, "[WARN ] [mcp.transport] MCP connection closed or unavailable");
        StringAssert.Contains(text, "[INFO ] [mcp.transport] MCP client detached");
        StringAssert.Contains(text, "[WARN ] [mcp.command] MCP revision conflict");
        StringAssert.Contains(text, "diagnosticCodes=REVISION_CONFLICT");
        StringAssert.Contains(text, "[WARN ] [mcp.auth] MCP permission denied");
        Assert.IsFalse(text.Contains("expectedRevision", StringComparison.Ordinal));
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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Kachinco.Logging.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
