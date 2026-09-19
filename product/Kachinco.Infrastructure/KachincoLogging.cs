using System.Text.Json;
using Flamoris.Logging;

namespace Kachinco.Infrastructure;

public sealed record KachincoLoggingHost(FlamorisLogger Logger, LoggingOptions Options, string BasePath);

public static class KachincoLogging
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static KachincoLoggingHost Create(
        string component = "app",
        string? configurationPath = null,
        string? basePath = null,
        Action<string>? diagnostic = null)
    {
        var options = LoadOptions(configurationPath, diagnostic);
        if (string.Equals(component, "mcp-bridge", StringComparison.Ordinal))
            options = ForBridge(options);
        var resolvedBasePath = basePath ?? DefaultBasePath();
        return new(FlamorisLogger.Create(options, resolvedBasePath, diagnostic), options, resolvedBasePath);
    }

    public static LoggingOptions LoadOptions(string? configurationPath = null, Action<string>? diagnostic = null)
    {
        var path = configurationPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (!File.Exists(path)) return Defaults();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!TryProperty(document.RootElement, "logging", out var logging)) return Defaults();
            return logging.Deserialize<LoggingOptions>(Json) ?? Defaults();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            TryDiagnostic(diagnostic, $"Logging configuration could not be read; defaults are active: {exception.Message}");
            return Defaults();
        }
    }

    public static string DefaultBasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Kachinco");

    private static LoggingOptions Defaults() => new()
    {
        Level = "debug",
        Outputs =
        [
            new() { Type = "console" },
            new() { Type = "file", Path = "logs/kachinco.log", Rotation = new() { Enabled = true, MaxFileSizeMb = 20, MaxFiles = 10 } },
        ],
    };

    private static LoggingOptions ForBridge(LoggingOptions source)
    {
        var outputs = source.Outputs
            .Where(output => !string.Equals(output.Type, "console", StringComparison.OrdinalIgnoreCase))
            .Select(output => new LogOutputOptions
            {
                Type = output.Type,
                Format = output.Format,
                Path = BridgePath(output.Path),
                Rotation = new()
                {
                    Enabled = output.Rotation.Enabled,
                    MaxFileSizeMb = output.Rotation.MaxFileSizeMb,
                    MaxFiles = output.Rotation.MaxFiles,
                },
            }).ToList();
        if (outputs.Count == 0) outputs.Add(new() { Type = "file", Path = "logs/kachinco-mcp.log" });
        return new()
        {
            Level = source.Level,
            Categories = new Dictionary<string, string?>(source.Categories, StringComparer.OrdinalIgnoreCase),
            Outputs = outputs,
            UseLocalTime = source.UseLocalTime,
            RedactedPropertyNames = [.. source.RedactedPropertyNames],
        };
    }

    private static string BridgePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return "logs/kachinco-mcp.log";
        var extension = Path.GetExtension(configuredPath);
        return Path.Combine(Path.GetDirectoryName(configuredPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(configuredPath) + "-mcp" + extension);
    }

    private static bool TryProperty(JsonElement value, string name, out JsonElement property)
    {
        foreach (var candidate in value.EnumerateObject())
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            { property = candidate.Value; return true; }
        property = default;
        return false;
    }

    private static void TryDiagnostic(Action<string>? diagnostic, string message)
    {
        try { diagnostic?.Invoke(message); } catch { }
    }
}
