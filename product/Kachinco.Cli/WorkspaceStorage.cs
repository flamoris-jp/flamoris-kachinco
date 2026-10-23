using System.Globalization;
using System.Text.Json;

namespace Kachinco.Cli;

// Operator configuration, not a second project/session authority or a remote path API.
public sealed class WorkspaceStorage
{
    public string ProjectsRoot { get; }
    public string ProjectName { get; }
    public string TemporaryExportRoot { get; }
    public string? RequiredMount { get; }
    public string ProjectDirectory => Path.Combine(ProjectsRoot, ProjectName);
    public string ProjectPath => Path.Combine(ProjectDirectory, "project.fkproj");
    public string MediaDirectory => Path.Combine(ProjectDirectory, "media");
    public string ExportDirectory => Path.Combine(ProjectDirectory, "exports");

    private WorkspaceStorage(string root, string name, string temporary, string? mount)
    {
        ProjectsRoot = Absolute(root); TemporaryExportRoot = Absolute(temporary);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != name.Trim() ||
            name.StartsWith('.') || name.EndsWith('.') || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            name.Any(char.IsControl) || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid portable projectName; use one folder name.");
        ProjectName = name;
        if (Inside(ProjectsRoot, TemporaryExportRoot) || Inside(TemporaryExportRoot, ProjectsRoot))
            throw new InvalidDataException("Project and temporary export roots must be separate.");
        RequiredMount = mount is null ? null : Absolute(mount);
        if (RequiredMount is not null && !Inside(ProjectsRoot, RequiredMount))
            throw new InvalidDataException("requiredMount must enclose projectsRoot.");
    }

    public static async Task<WorkspaceStorage> LoadAsync(string path, CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(await Program.ReadBoundedAsync(path, 16384, token));
        var json = document.RootElement;
        string[] required = ["projectsRoot", "projectName", "temporaryExportRoot"];
        string[] allowed = [.. required, "requiredMount"];
        if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Any(p => !allowed.Contains(p.Name)) ||
            json.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1) ||
            required.Any(p => !json.TryGetProperty(p, out _)) || json.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Invalid workspace configuration fields.");
        return new(json.GetProperty("projectsRoot").GetString()!, json.GetProperty("projectName").GetString()!,
            json.GetProperty("temporaryExportRoot").GetString()!, json.TryGetProperty("requiredMount", out var mount) ? mount.GetString() : null);
    }

    public void VerifyAvailable()
    {
        if (RequiredMount is not null && (!OperatingSystem.IsLinux() || !HasNetworkMount(File.ReadAllText("/proc/self/mountinfo"), RequiredMount)))
            throw new IOException("WORKSPACE_MOUNT_UNAVAILABLE: configured network mount is not present.");
        NoLinks(ProjectsRoot); NoLinks(TemporaryExportRoot); NoLinks(ProjectDirectory);
        NoLinks(ProjectPath); NoLinks(ProjectPath + ".lock"); NoLinks(MediaDirectory); NoLinks(ExportDirectory);
        if (!Directory.Exists(ProjectsRoot))
            throw new IOException("WORKSPACE_ROOT_UNAVAILABLE: create/mount the configured projects root first.");
    }

    public void Prepare()
    {
        VerifyAvailable();
        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(MediaDirectory);
        Directory.CreateDirectory(ExportDirectory);
        Directory.CreateDirectory(TemporaryExportRoot);
    }

    // /proc escapes spaces, tabs, newlines and literal backslashes as octal.
    public static bool HasNetworkMount(string table, string path) => table.Split('\n').Any(line => {
        var fields = line.Split(' '); int separator = Array.IndexOf(fields, "-");
        if (separator < 6 || separator + 1 >= fields.Length) return false;
        string point = fields[4].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
        return point == path && fields[separator + 1] is "cifs" or "nfs" or "nfs4";
    });

    public async Task<string> PublishAsync(string source, Guid job, Guid sequence, long revision, CancellationToken token)
    {
        VerifyAvailable();
        string destination = Path.Combine(ExportDirectory, $"sequence-{sequence:N}-rev-{revision.ToString(CultureInfo.InvariantCulture)}-job-{job:N}.mp4");
        CliOperations.NewOutput(destination);
        string temporary = Path.Combine(ExportDirectory, ".kachinco-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested(); VerifyAvailable();
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
            path != Path.GetFullPath(path) || Path.GetPathRoot(path) == path || path.TrimEnd(Path.DirectorySeparatorChar) != path)
            throw new InvalidDataException("Workspace paths must be normalized absolute non-root directories.");
        return path;
    }
    private static bool Inside(string path, string root) => Path.GetRelativePath(root, path) is var relative &&
        relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    private static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("WORKSPACE_LINK_REFUSED: workspace paths cannot contain symbolic links.");
    }
}
