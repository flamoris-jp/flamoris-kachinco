using System.Text.Json;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Editor preference, deliberately outside the document, revision and undo history.
public sealed class MonitoringPreferences(string? path = null)
{
    private readonly string filename = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Kachinco", "playback.json");
    public Result<double> Load()
    {
        try
        {
            if (!File.Exists(filename)) return Result<double>.Ok(1);
            using var file = File.OpenRead(filename);
            if (file.Length > 4096) return Invalid();
            // Bound the actual read too, including a file growing after the length check.
            var buffer = new byte[4097]; int count = 0, read;
            while (count < buffer.Length && (read = file.Read(buffer, count, buffer.Length - count)) > 0) count += read;
            if (count > 4096) return Invalid();
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("monitoringGain", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double gain) ||
                !double.IsFinite(gain) || gain < 0 || gain > 1) return Invalid();
            return Result<double>.Ok(gain);
        }
        catch (JsonException) { return Invalid(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Result<double>.Fail(Diagnostic.Error("PREFERENCE_READ_FAILED", "Could not read monitoring volume preference.")); }
        static Result<double> Invalid() => Result<double>.Fail(Diagnostic.Error("INVALID_PREFERENCE", "Invalid monitoring volume preference; using 100%."));
    }
    public Result<double> Save(double gain)
    {
        if (!double.IsFinite(gain) || gain < 0 || gain > 1)
            return Result<double>.Fail(Diagnostic.Error("INVALID_PREFERENCE", "Monitoring volume must be between 0% and 100%."));
        string? temporary = null;
        try
        {
            string target = Path.GetFullPath(filename); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(new { monitoringGain = gain })); file.Flush(true); }
            File.Move(temporary, target, overwrite: true); temporary = null;
            return Result<double>.Ok(gain);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Result<double>.Fail(Diagnostic.Error("PREFERENCE_WRITE_FAILED", "Could not save monitoring volume preference.")); }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
