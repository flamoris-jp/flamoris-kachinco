using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Application semantics only. Core owns protocol, authorization and request guards.
public static class KachincoMcpTools
{
    private static readonly Dictionary<string, Type> Commands = new(StringComparer.Ordinal)
    {
        [nameof(AddClapper)] = typeof(AddClapper), [nameof(UpdateClapper)] = typeof(UpdateClapper), [nameof(DeleteClapper)] = typeof(DeleteClapper),
        [nameof(AddRecipe)] = typeof(AddRecipe), [nameof(UpdateRecipe)] = typeof(UpdateRecipe), [nameof(SetGeneratedProvenance)] = typeof(SetGeneratedProvenance),
        [nameof(CreateProject)] = typeof(CreateProject), [nameof(CreateSequence)] = typeof(CreateSequence),
        [nameof(RegisterMedia)] = typeof(RegisterMedia), [nameof(RelinkMedia)] = typeof(RelinkMedia),
        [nameof(SetSequenceDuration)] = typeof(SetSequenceDuration), [nameof(AddTrack)] = typeof(AddTrack),
        [nameof(InsertClip)] = typeof(InsertClip), [nameof(MoveClip)] = typeof(MoveClip), [nameof(TrimClip)] = typeof(TrimClip),
        [nameof(SplitClip)] = typeof(SplitClip), [nameof(DeleteClip)] = typeof(DeleteClip), [nameof(SetClipProperties)] = typeof(SetClipProperties),
        [nameof(SetTrackEnabled)] = typeof(SetTrackEnabled), [nameof(ReorderTrack)] = typeof(ReorderTrack),
        [nameof(AddCaption)] = typeof(AddCaption), [nameof(UpdateCaption)] = typeof(UpdateCaption), [nameof(DeleteCaption)] = typeof(DeleteCaption)
    };
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 64,
        Converters = { new IntegerStringConverter(), new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static IReadOnlyList<HostTool> Create(EditorSession session, Func<object> editorContext, Action changed)
    {
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire);
        JsonElement Edited(EditResult result, bool dryRun = false)
        {
            // A view/log observer must not convert a committed edit into a failed request.
            if (result.Success && !dryRun) { try { changed(); } catch { } }
            return Json(result);
        }
        return [
            new HostTool<EmptyInput>("get_project", "Read the visible Project, revision, selection and shared history.",
                Schema("{}"), OperationKind.Query, Empty,
                (context, _, token) => context.ReadAsync(() => {
                    var snapshot = session.GetProject();
                    return Json(new { revision = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
                        project = snapshot.Project is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(ProjectJson.Serialize(snapshot.Project).Value!),
                        context = editorContext(), snapshot.CanUndo, snapshot.CanRedo });
                }), foreground: false),
            new HostTool<BatchInput>("edit_batch", "Atomic ordinary typed edits. Int64 fields are decimal strings; no file/lifecycle grant.",
                BatchSchema(), OperationKind.Transaction, DecodeBatch,
                (context, input, token) => context.CommitAsync(() => Edited(session.Execute(
                    new(input.Commands, session.GetProject().Revision, input.DryRun), token), input.DryRun))),
            new HostTool<EmptyInput>("undo", "Undo one entry in the shared editor history.", Schema("{}"), OperationKind.Undo, Empty,
                (context, _, token) => context.CommitAsync(() => Edited(session.Undo(session.GetProject().Revision, token)))),
            new HostTool<EmptyInput>("redo", "Redo one entry in the shared editor history.", Schema("{}"), OperationKind.Redo, Empty,
                (context, _, token) => context.CommitAsync(() => Edited(session.Redo(session.GetProject().Revision, token)))),
            new HostTool<ResolveInput>("clapper_resolve", "Resolve a sequence-local Clapper name to stable identity.",
                Schema("{\"sequenceId\":{\"type\":\"string\",\"format\":\"uuid\"},\"name\":{\"type\":\"string\"}}", "sequenceId", "name"),
                OperationKind.Query, DecodeResolve,
                (context, input, token) => context.ReadAsync(() => Json(ClapperQueries.Resolve(
                    session.GetProject().Project!, input.SequenceId, input.Name)))),
            new HostTool<RecipeInput>("recipe_validate", "Compile bounded literal-only Recipe text without executing user code or publishing files.",
                Schema("{\"source\":{\"type\":\"string\",\"maxLength\":65536}}", "source"), OperationKind.Query, DecodeRecipe,
                async (context, input, token) => {
                    var result = await new RecipeCompiler().CompileAsync(input.Source, token);
                    return await context.ReadAsync(() => Json(result));
                })
        ];
    }

    private sealed record EmptyInput;
    private sealed record BatchInput(ImmutableArray<EditCommand> Commands, bool DryRun);
    private sealed record ResolveInput(Guid SequenceId, string Name);
    private sealed record RecipeInput(string Source);
    private static EmptyInput Empty(JsonElement input) { Fields(input, []); return new(); }
    private static ResolveInput DecodeResolve(JsonElement input)
    {
        Fields(input, ["sequenceId", "name"], "sequenceId", "name");
        if (!input.GetProperty("sequenceId").TryGetGuid(out var id) || id == Guid.Empty) throw new JsonException();
        return new(id, Text(input, "name"));
    }
    private static RecipeInput DecodeRecipe(JsonElement input)
    {
        Fields(input, ["source"], "source");
        string source = Text(input, "source");
        if (source.Length > 65536) throw new JsonException();
        return new(source);
    }
    private static BatchInput DecodeBatch(JsonElement input)
    {
        Fields(input, ["commands", "dryRun"], "commands");
        var items = input.GetProperty("commands");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 64) throw new JsonException();
        var commands = ImmutableArray.CreateBuilder<EditCommand>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Select(p => p.Name).Distinct().Count() != item.EnumerateObject().Count()) throw new JsonException();
            if (!item.TryGetProperty("type", out var discriminator) || discriminator.ValueKind != JsonValueKind.String ||
                !Commands.TryGetValue(discriminator.GetString()!, out var type)) throw new JsonException();
            if (!AllowedCommand(type)) throw new McpFault(McpErrors.Forbidden);
            var payload = JsonNode.Parse(item.GetRawText())!.AsObject(); payload.Remove("type");
            McpTypedSchema.Validate(type, JsonSerializer.SerializeToElement(payload));
            commands.Add((EditCommand)(payload.Deserialize(type, Wire) ?? throw new JsonException()));
        }
        return new(commands.ToImmutable(), input.TryGetProperty("dryRun", out var dry) && dry.GetBoolean());
    }
    private static string Text(JsonElement input, string name) => input.GetProperty(name).GetString() ?? throw new JsonException();
    private static void Fields(JsonElement input, string[] allowed, params string[] required)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException();
        if (required.Any(name => !seen.Contains(name))) throw new JsonException();
    }
    private static JsonElement Schema(string properties, params string[] required) => JsonSerializer.SerializeToElement(new {
        type = "object", additionalProperties = false, properties = JsonSerializer.Deserialize<JsonElement>(properties), required });
    private static JsonElement BatchSchema() => JsonSerializer.SerializeToElement(new JsonObject {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("commands"),
        ["properties"] = new JsonObject {
            ["dryRun"] = new JsonObject { ["type"] = "boolean" },
            ["commands"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
                ["items"] = new JsonObject { ["oneOf"] = new JsonArray(Commands.Values.Where(AllowedCommand)
                    .OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => (JsonNode?)McpTypedSchema.Command(t)).ToArray()) } }
        }
    });

    public static IReadOnlyDictionary<string, bool> CommandDispositions => Commands.ToDictionary(p => p.Key, p => AllowedCommand(p.Value), StringComparer.Ordinal);

    private static bool AllowedCommand(Type type) => type.Name is
        nameof(AddClapper) or nameof(UpdateClapper) or nameof(DeleteClapper) or
        nameof(AddRecipe) or nameof(UpdateRecipe) or nameof(CreateSequence) or nameof(SetSequenceDuration) or
        nameof(AddTrack) or nameof(InsertClip) or nameof(MoveClip) or nameof(TrimClip) or nameof(SplitClip) or
        nameof(DeleteClip) or nameof(SetClipProperties) or nameof(SetTrackEnabled) or nameof(ReorderTrack) or
        nameof(AddCaption) or nameof(UpdateCaption) or nameof(DeleteCaption);

    private sealed class IntegerStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw new JsonException("Int64 requires a decimal string.");
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
