using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public static class ProjectJson
{
    public const string Format = "flamoris-kachinco";
    public const int SchemaVersion = 2;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64,
        Converters = { new TickStringConverter(), new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static Result<string> Serialize(Project project)
    {
        var errors = ProjectValidator.Validate(project);
        if (!errors.IsEmpty) return new(null, errors);
        var json = JsonSerializer.Serialize(FormatV2.Encode(project), Options);
        return Encoding.UTF8.GetByteCount(json) <= MaxFileBytes ? Result<string>.Ok(json) :
            Result<string>.Fail(Diagnostic.Error("PROJECT_TOO_LARGE", "Project exceeds the 16 MiB foundation file limit."));
    }

    public static Result<Project> Deserialize(string json)
    {
        if (json is null) return Result<Project>.Fail(Diagnostic.Error("INVALID_PROJECT_FILE", "JSON is required."));
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            return Result<Project>.Fail(Diagnostic.Error("PROJECT_TOO_LARGE", "Project exceeds the 16 MiB foundation file limit."));
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int schema))
                return Result<Project>.Fail(Diagnostic.Error("INVALID_ENVELOPE", "An integer schemaVersion is required."));
            // Never attempt to decode a future payload as v1.
            if (schema != 1 && schema != SchemaVersion) return Result<Project>.Fail(Diagnostic.Error("SCHEMA_UNSUPPORTED", $"Schema {schema} is not supported."));
            RejectDuplicateProperties(root);
            Project project;
            if (schema == 1)
            {
                var envelope = root.Deserialize<EnvelopeV1>(Options) ?? throw new FormatException("Envelope is null.");
                if (envelope.Format != Format || envelope.Timebase != TimelineTime.TicksPerSecond) throw new FormatException("Invalid format/timebase.");
                project = FormatV1.Decode(envelope);
            }
            else
            {
                var envelope = root.Deserialize<EnvelopeV2>(Options) ?? throw new FormatException("Envelope is null.");
                if (envelope.Format != Format || envelope.Timebase != TimelineTime.TicksPerSecond) throw new FormatException("Invalid format/timebase.");
                project = FormatV2.Decode(envelope);
            }
            var errors = ProjectValidator.Validate(project);
            return errors.IsEmpty ? Result<Project>.Ok(project) : new(null, errors);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return Result<Project>.Fail(Diagnostic.Error("INVALID_PROJECT_FILE", "Malformed, missing, duplicate or unsupported v1 fields."));
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private sealed class TickStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String ||
                !long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ticks))
                throw new JsonException("Tick values must be decimal integer strings.");
            return ticks;
        }
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
