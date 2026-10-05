using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Bounded local assets. No code runs while browsing and no project state is owned here.
public sealed class EffectLibrary(string root)
{
    public const int MaximumItemBytes = 2 * 1024 * 1024;
    public string Root { get; } = Path.GetFullPath(root, AppContext.BaseDirectory);
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        MaxDepth = 32, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public Result<ImmutableArray<EffectDefinition>> List()
    {
        try
        {
            CheckRoot();
            if (!Directory.Exists(Root)) return Result<ImmutableArray<EffectDefinition>>.Ok([]);
            var effects = ImmutableArray.CreateBuilder<EffectDefinition>();
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var path in Directory.EnumerateFiles(Root, "*.effect.json").Take(1001))
            {
                if (effects.Count + diagnostics.Count >= 1000) return Failure<ImmutableArray<EffectDefinition>>("EFFECT_LIBRARY_LIMIT", "Library contains more than 1000 items.");
                string name = Path.GetFileName(path);
                if (!Guid.TryParseExact(name[..^12], "N", out var id)) { diagnostics.Add(Diagnostic.Error("INVALID_EFFECT_FILENAME", "Effect filenames must contain stable GUIDs.", path: path)); continue; }
                var item = Get(id);
                if (item.Success) effects.Add(item.Value!); else diagnostics.AddRange(item.Diagnostics);
            }
            // Good entries remain visible; malformed siblings are reported instead of silently lost.
            return new([.. effects.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id)], diagnostics.ToImmutable());
        }
        catch (Exception e) when (StorageError(e)) { return Failure<ImmutableArray<EffectDefinition>>("EFFECT_LIBRARY_READ_FAILED", e.Message); }
    }

    public Result<EffectDefinition> Get(Guid id)
    {
        try
        {
            CheckRoot(); string path = ItemPath(id); CheckLink(path);
            if (!File.Exists(path)) return Failure<EffectDefinition>("EFFECT_NOT_FOUND", "Effect not found.");
            using var file = File.OpenRead(path);
            if (file.Length > MaximumItemBytes) return Failure<EffectDefinition>("EFFECT_SIZE_LIMIT", "Effect exceeds the size limit.");
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int count;
            while ((count = file.Read(bytes, 0, bytes.Length)) > 0)
            {
                if (buffer.Length + count > MaximumItemBytes) return Failure<EffectDefinition>("EFFECT_SIZE_LIMIT", "Effect exceeds the size limit.");
                buffer.Write(bytes, 0, count);
            }
            var parsed = Decode(buffer.ToArray());
            if (parsed.Success && parsed.Value!.Id != id) return Failure<EffectDefinition>("EFFECT_ID_MISMATCH", "Stored identity does not match filename.");
            return parsed;
        }
        catch (Exception e) when (StorageError(e)) { return Failure<EffectDefinition>("EFFECT_LIBRARY_READ_FAILED", e.Message); }
    }

    public static Result<EffectDefinition> Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumItemBytes) return Failure<EffectDefinition>("EFFECT_SIZE_LIMIT", "Effect exceeds the size limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            RejectDuplicates(document.RootElement);
            var effect = document.RootElement.Deserialize<EffectDefinition>(Json);
            var errors = EffectComposition.Validate(effect);
            return errors.IsEmpty ? Result<EffectDefinition>.Ok(effect!) : new(null, errors);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        { return Failure<EffectDefinition>("INVALID_EFFECT_JSON", e.Message); }
    }

    public async Task<Result<EffectDefinition>> ValidateProgramAsync(EffectDefinition effect, CancellationToken token = default)
    {
        var errors = EffectComposition.Validate(effect);
        if (!errors.IsEmpty) return new(null, errors);
        if (effect.RecipeSource is null) return Result<EffectDefinition>.Ok(effect);
        var compiled = await new RecipeCompiler().CompileAsync(effect.RecipeSource, token);
        return compiled.Success ? Result<EffectDefinition>.Ok(effect) : new(null, compiled.Diagnostics);
    }

    // Call only after ValidateProgramAsync. Disk data is revalidated independently on reads.
    public async Task<Result<EffectDefinition>> SaveAsync(EffectDefinition effect, int? expectedVersion = null, CancellationToken token = default)
    {
        var validated = await ValidateProgramAsync(effect, token);
        if (!validated.Success) return validated;
        token.ThrowIfCancellationRequested();
        return SaveValidated(validated.Value!, expectedVersion);
    }
    internal Result<EffectDefinition> SaveValidated(EffectDefinition effect, int? expectedVersion = null)
    {
        var errors = EffectComposition.Validate(effect);
        if (!errors.IsEmpty) return new(null, errors);
        string? temporary = null;
        try
        {
            CheckRoot(); Directory.CreateDirectory(Root); CheckRoot();
            using var fileLock = Lock();
            var old = Get(effect.Id);
            if (expectedVersion is null ? old.Success || old.Diagnostics.Any(d => d.Code != "EFFECT_NOT_FOUND") : !old.Success || old.Value!.Version != expectedVersion)
                return Failure<EffectDefinition>("EFFECT_VERSION_CONFLICT", "Item changed or already exists. Inspect it before saving.");
            var saved = effect with { Version = expectedVersion is null ? 1 : checked(expectedVersion.Value + 1) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, Json);
            if (bytes.Length > MaximumItemBytes) return Failure<EffectDefinition>("EFFECT_SIZE_LIMIT", "Effect exceeds the size limit.");
            temporary = Path.Combine(Root, "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            string target = ItemPath(effect.Id); CheckLink(target);
            File.Move(temporary, target, expectedVersion is not null); temporary = null;
            return Result<EffectDefinition>.Ok(saved);
        }
        catch (Exception e) when (StorageError(e) || e is OverflowException) { return Failure<EffectDefinition>("EFFECT_LIBRARY_WRITE_FAILED", e.Message); }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public Result<bool> Delete(Guid id, int expectedVersion)
    {
        try
        {
            CheckRoot();
            if (!Directory.Exists(Root)) return Failure<bool>("EFFECT_NOT_FOUND", "Effect not found.");
            using var fileLock = Lock();
            var old = Get(id);
            if (!old.Success) return new(false, old.Diagnostics);
            if (old.Value!.Version != expectedVersion) return Failure<bool>("EFFECT_VERSION_CONFLICT", "Item changed. Inspect it before deleting.");
            string path = ItemPath(id); CheckLink(path); File.Delete(path); return Result<bool>.Ok(true);
        }
        catch (Exception e) when (StorageError(e)) { return Failure<bool>("EFFECT_LIBRARY_WRITE_FAILED", e.Message); }
    }

    private FileStream Lock()
    {
        string path = Path.Combine(Root, ".effects.lock"); CheckLink(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private string ItemPath(Guid id) => id == Guid.Empty ? throw new ArgumentException("Nonempty effect ID required.") : Path.Combine(Root, id.ToString("N") + ".effect.json");
    private void CheckRoot()
    {
        for (DirectoryInfo? directory = new(Root); directory is not null; directory = directory.Parent) CheckLink(directory.FullName);
        if (File.Exists(Root)) throw new IOException("Effect Library Path must be a directory.");
    }
    private static void CheckLink(string path)
    {
        // LinkTarget also identifies dangling links, unlike Exists.
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Effect library does not follow symbolic links or reparse points.");
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Effect library does not follow symbolic links or reparse points.");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException("Duplicate field."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static Result<T> Failure<T>(string code, string message) => Result<T>.Fail(Diagnostic.Error(code, message));
}

public sealed class EffectLibraryPreferences(string? filename = null)
{
    private readonly string path = filename ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Kachinco", "effects.json");
    public string DefaultPath => Path.Combine(AppContext.BaseDirectory, "library");
    public Result<string> Load()
    {
        try
        {
            if (!File.Exists(path)) return Result<string>.Ok(DefaultPath);
            using var file = File.OpenRead(path); var bytes = new byte[8193]; int size = 0, count;
            while (size < bytes.Length && (count = file.Read(bytes, size, bytes.Length - size)) > 0) size += count;
            if (size > 8192) throw new JsonException("Preference exceeds size limit.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, size));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("effectLibraryPath", out var value) ||
                value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new JsonException("Invalid Effect Library Path.");
            return Result<string>.Ok(Path.GetFullPath(value.GetString()!, AppContext.BaseDirectory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        { return Result<string>.Fail(Diagnostic.Error("EFFECT_PREFERENCE_READ_FAILED", e.Message)); }
    }
    public Result<string> Save(string root)
    {
        string? temporary = null;
        try
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Choose an Effect Library Path.");
            string full = Path.GetFullPath(root, AppContext.BaseDirectory);
            var check = new EffectLibrary(full).List();
            if (check.Diagnostics.Any(d => d.Code == "EFFECT_LIBRARY_READ_FAILED")) return new(null, check.Diagnostics);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(JsonSerializer.SerializeToUtf8Bytes(new { effectLibraryPath = full })); file.Flush(true); }
            File.Move(temporary, path, true); temporary = null;
            return Result<string>.Ok(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Result<string>.Fail(Diagnostic.Error("EFFECT_PREFERENCE_WRITE_FAILED", e.Message)); }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
