using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kachinco.Native;

namespace Kachinco.Core;

// Typed wire projection only. Disk schema, validation and editing are native responsibilities.
public static class NativeProjectCodec
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        MaxDepth = 64,
        Converters = { new ImmutableWireConverterFactory() }
    };
    internal static JsonElement Element(object? value) => value is null ? JsonSerializer.SerializeToElement<object?>(null, Options) :
        JsonSerializer.SerializeToElement(value, value.GetType(), Options);
    internal static JsonElement Request(object value) => JsonSerializer.Deserialize<JsonElement>(
        NativeEditorSession.CodecRequest(JsonSerializer.Serialize(value, Options)), Options);
    internal static ImmutableArray<Diagnostic> Diagnostics(JsonElement value) => value.GetProperty("diagnostics").Deserialize<ImmutableArray<Diagnostic>>(Options);
    public static Result<string> Serialize(Project project)
    {
        var result = Request(new { action = "serialize", project = Element(project) });
        return new(result.GetProperty("value").Deserialize<string>(Options), Diagnostics(result));
    }
    public static Result<Project> Deserialize(string? text)
    {
        var result = Request(new { action = "deserialize", text });
        return new(result.GetProperty("value").Deserialize<Project>(Options), Diagnostics(result));
    }
    internal static ImmutableArray<Diagnostic> Validate(Project? project) => Diagnostics(Request(new { action = "validate", project = Element(project) }));
    private sealed class ImmutableWireConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ImmutableWireConverter<>).MakeGenericType(type.GetGenericArguments()[0]))!;
    }
    private sealed class ImmutableWireConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? default : [.. JsonSerializer.Deserialize<T[]>(ref reader, options)!];
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            if (value.IsDefault) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, value.ToArray(), options);
        }
    }
}
