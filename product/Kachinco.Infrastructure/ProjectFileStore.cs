using System.Text;
using Flamoris.Logging;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class ProjectFileStore(FlamorisLogger? logger = null)
{
    public async Task<Result<Project>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            if (file.Length > ProjectJson.MaxFileBytes) return TooLarge();
            using var bytes = new MemoryStream();
            var buffer = new byte[65536];
            int read;
            while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bytes.Length + read > ProjectJson.MaxFileBytes) return TooLarge();
                bytes.Write(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var json = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            // Accept the conventional UTF-8 BOM, without changing the saved contract.
            return ProjectJson.Deserialize(json.TrimStart('\uFEFF'));
        }
        catch (OperationCanceledException exception)
        {
            logger?.Log(LogLevel.Warn, "document.open", "Project load cancelled",
                new Dictionary<string, object?> { ["diagnosticCode"] = "CANCELLED" }, exception);
            return Result<Project>.Fail(Diagnostic.Error("CANCELLED", "Project load cancelled."));
        }
        catch (DecoderFallbackException exception)
        {
            logger?.Error("document.open", "Project decoding failed", exception,
                new Dictionary<string, object?> { ["diagnosticCode"] = "INVALID_PROJECT_FILE" });
            return Result<Project>.Fail(Diagnostic.Error("INVALID_PROJECT_FILE", "Project is not valid UTF-8."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Expected filesystem exceptions commonly embed the absolute path in
            // their message. Keep the failure observable without handing that
            // message to the logger.
            logger?.Error("document.open", "Project load failed", properties:
                new Dictionary<string, object?>
                {
                    ["diagnosticCode"] = "PROJECT_READ_FAILED",
                    ["errorType"] = e.GetType().Name,
                });
            return Result<Project>.Fail(Diagnostic.Error("PROJECT_READ_FAILED", "Could not read the project file."));
        }
    }

    public async Task<Result<string>> SaveAsync(string path, Project project, CancellationToken cancellationToken = default)
    {
        var serialized = ProjectJson.Serialize(project);
        if (!serialized.Success)
        {
            LogDiagnostics("document.save", "Project serialization failed", serialized.Diagnostics);
            return serialized;
        }
        string? temp = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(path);
            temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(serialized.Value!), cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
            temp = null;
            return Result<string>.Ok(target);
        }
        catch (OperationCanceledException exception)
        {
            logger?.Log(LogLevel.Warn, "document.save", "Project save cancelled",
                new Dictionary<string, object?> { ["diagnosticCode"] = "CANCELLED" }, exception);
            return Result<string>.Fail(Diagnostic.Error("CANCELLED", "Project save cancelled."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger?.Error("document.save", "Project save failed", properties:
                new Dictionary<string, object?>
                {
                    ["diagnosticCode"] = "PROJECT_WRITE_FAILED",
                    ["errorType"] = e.GetType().Name,
                });
            return Result<string>.Fail(Diagnostic.Error("PROJECT_WRITE_FAILED", "Could not save the project file."));
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private Result<Project> TooLarge()
    {
        logger?.Error("document.open", "Project load failed",
            properties: new Dictionary<string, object?> { ["diagnosticCode"] = "PROJECT_TOO_LARGE" });
        return Result<Project>.Fail(Diagnostic.Error("PROJECT_TOO_LARGE", "Project exceeds the 16 MiB foundation file limit."));
    }

    private void LogDiagnostics(string category, string message, IEnumerable<Diagnostic> diagnostics) =>
        logger?.Error(category, message, properties: new Dictionary<string, object?>
        {
            ["diagnosticCodes"] = string.Join(",", diagnostics.Select(diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal)),
        });
}
