using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kachinco.Infrastructure;

// The closed registry decides exposure. Reflection only describes/validates the
// already approved constructor payload; adding a Core command never exposes it.
public static class McpTypedSchema
{
    private static readonly NullabilityInfoContext Nullability = new();
    private static ParameterInfo[] Fields(Type type) => type.GetConstructors()
        .OrderByDescending(c => c.GetParameters().Length).First().GetParameters();
    private static string Name(ParameterInfo field) => JsonNamingPolicy.CamelCase.ConvertName(field.Name!);
    private static bool Nullable(ParameterInfo field) => System.Nullable.GetUnderlyingType(field.ParameterType) is not null ||
        !field.ParameterType.IsValueType && Nullability.Create(field).ReadState == NullabilityState.Nullable;

    public static JsonObject Describe(Type type)
    {
        if (System.Nullable.GetUnderlyingType(type) is { } inner)
            return new() { ["anyOf"] = new JsonArray(Describe(inner), new JsonObject { ["type"] = "null" }) };
        if (type == typeof(string)) return new() { ["type"] = "string" };
        if (type == typeof(Guid)) return new() { ["type"] = "string", ["format"] = "uuid" };
        if (type == typeof(long)) return new() { ["type"] = "string", ["pattern"] = "^-?[0-9]+$", ["description"] = "Signed Int64 decimal string; range checked by the typed decoder." };
        if (type == typeof(bool)) return new() { ["type"] = "boolean" };
        if (type == typeof(int)) return new() { ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue };
        if (type == typeof(double)) return new() { ["type"] = "number" };
        if (type.IsEnum) return new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
        var properties = new JsonObject(); var required = new JsonArray();
        foreach (var field in Fields(type))
        {
            JsonObject shape = Describe(field.ParameterType);
            if (Nullable(field) && System.Nullable.GetUnderlyingType(field.ParameterType) is null)
                shape = new() { ["anyOf"] = new JsonArray(shape, new JsonObject { ["type"] = "null" }) };
            properties[Name(field)] = shape;
            if (!field.HasDefaultValue) required.Add(Name(field));
        }
        return new() { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }
    public static JsonObject Command(Type type)
    {
        var schema = Describe(type);
        schema["properties"]!.AsObject()["type"] = new JsonObject { ["type"] = "string", ["const"] = type.Name };
        schema["required"]!.AsArray().Insert(0, JsonValue.Create("type"));
        return schema;
    }
    public static void Validate(Type type, JsonElement value)
    {
        if (System.Nullable.GetUnderlyingType(type) is { } inner)
        { if (value.ValueKind != JsonValueKind.Null) Validate(inner, value); return; }
        if (type == typeof(string)) { Require(value.ValueKind == JsonValueKind.String); return; }
        if (type == typeof(Guid)) { Require(value.ValueKind == JsonValueKind.String && value.TryGetGuid(out _)); return; }
        if (type == typeof(long))
        {
            Require(value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _) && System.Text.RegularExpressions.Regex.IsMatch(value.GetString()!, "^-?[0-9]+$")); return;
        }
        if (type == typeof(bool)) { Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False); return; }
        if (type == typeof(int)) { Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)); return; }
        if (type == typeof(double)) { Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d)); return; }
        if (type.IsEnum) { Require(value.ValueKind == JsonValueKind.String && Enum.GetNames(type).Contains(value.GetString(), StringComparer.Ordinal)); return; }
        Require(value.ValueKind == JsonValueKind.Object);
        var fields = Fields(type).ToDictionary(Name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !fields.TryGetValue(property.Name, out var field)) throw new JsonException("Unknown constructor field.");
            if (property.Value.ValueKind == JsonValueKind.Null && Nullable(field)) continue;
            Validate(field.ParameterType, property.Value);
        }
        foreach (var field in fields.Values)
            if (!field.HasDefaultValue && !value.TryGetProperty(Name(field), out _)) throw new JsonException("Missing constructor field.");
    }
    private static void Require(bool condition) { if (!condition) throw new JsonException("Invalid typed field."); }
}
