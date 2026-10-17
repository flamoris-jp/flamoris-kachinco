using System.Text.Json;

namespace Kachinco.Infrastructure;

public sealed record EditorWindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Math.Abs(Left) < 1_000_000 &&
        Math.Abs(Top) < 1_000_000 && Width is >= 980 and <= 100_000 && Height is >= 640 and <= 100_000;
}

// User preference only: no project revisions or undo records.
public sealed class WindowPlacementPreferences(string? path = null)
{
    private readonly string filename = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FLAMORIS", "Kachinco", "window.json");
    public EditorWindowPlacement? Load()
    {
        try
        {
            using var stream = File.OpenRead(filename);
            if (stream.Length > 4096) return null;
            var bytes = new byte[4097]; int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            if (count > 4096) return null;
            var value = JsonSerializer.Deserialize<EditorWindowPlacement>(bytes.AsSpan(0, count));
            return value?.IsValid == true ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return null; }
    }
    public bool Save(EditorWindowPlacement value)
    {
        if (!value.IsValid) return false;
        string? temporary = null;
        try
        {
            string target = Path.GetFullPath(filename); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(JsonSerializer.SerializeToUtf8Bytes(value)); stream.Flush(true); }
            File.Move(temporary, target, true); temporary = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
