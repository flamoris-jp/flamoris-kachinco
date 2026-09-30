using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Filesystem-independent native project codec facade; no managed schema authority.
public static class ProjectJson
{
    public const string Format = "flamoris-kachinco";
    public const int SchemaVersion = 2;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public static Result<string> Serialize(Project project) => NativeProjectCodec.Serialize(project);
    public static Result<Project> Deserialize(string json) => NativeProjectCodec.Deserialize(json);
}
