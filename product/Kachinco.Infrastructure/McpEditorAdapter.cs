using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class McpEditorAdapter(EditorSession session, Func<object> editorContext, Action changed, McpAccessLease lease)
{
    private bool initialized;
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
    public async Task<string?> HandleAsync(string line, CancellationToken cancellationToken = default, bool busy = false)
    {
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Token);
        cancellationToken = requestLifetime.Token;
        JsonElement? id = null;
        try
        {
            lease.Demand(cancellationToken: cancellationToken);
            var root = McpEnvelope.Parse(line);
            if (root.TryGetProperty("id", out var requestId)) id = requestId;
            string? method = root.GetProperty("method").GetString();
            if (id is null) return null;
            if (busy) return Error(id, -32000, "Editor is busy.");
            object result;
            if (method == "initialize")
            {
                var initialization = root.GetProperty("params");
                if (initialization.GetProperty("protocolVersion").ValueKind != JsonValueKind.String ||
                    initialization.GetProperty("capabilities").ValueKind != JsonValueKind.Object ||
                    initialization.GetProperty("clientInfo").GetProperty("name").ValueKind != JsonValueKind.String ||
                    initialization.GetProperty("clientInfo").GetProperty("version").ValueKind != JsonValueKind.String)
                    throw new JsonException();
                initialized = true;
                result = new { protocolVersion = "2025-03-26", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "Kachinco", version = "0.2.0" } };
            }
            else if (method == "ping") result = new { };
            else if (!initialized) return Error(id, -32002, "Initialize first.");
            else if (method == "tools/list") result = new { tools = ToolDefinitions().Where(VisibleTool) };
            else if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                var args = parameters.TryGetProperty("arguments", out var arguments) ? arguments : JsonSerializer.SerializeToElement(new { });
                ValidateArguments(name,args);
                if (name is "edit_batch" or "undo" or "redo") lease.Demand(true, cancellationToken);
                if (name is "recipe_generate" or "export_start" or "job_status" or "job_cancel")
                    throw new UnauthorizedAccessException("File/job access is not granted by this attachment.");
                object value;
                switch (name)
                {
                    case "get_project":
                        var snapshot = session.GetProject();
                        value = new { revision = snapshot.Revision.ToString(CultureInfo.InvariantCulture), project = snapshot.Project is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(ProjectJson.Serialize(snapshot.Project).Value!), context = editorContext(), snapshot.CanUndo, snapshot.CanRedo };
                        break;
                    case "edit_batch":
                        var commands = new List<EditCommand>();
                        foreach (var item in args.GetProperty("commands").EnumerateArray())
                        {
                            if (commands.Count >= 10000) throw new JsonException("Too many commands.");
                            string type = item.GetProperty("type").GetString() ?? "";
                            if (!Commands.TryGetValue(type, out var commandType)) throw new JsonException("Unknown command type.");
                            if (!AllowedCommand(commandType)) throw new UnauthorizedAccessException("Command requires a separate lifecycle/file grant.");
                            var payload = JsonNode.Parse(item.GetRawText())!.AsObject(); payload.Remove("type");
                            commands.Add((EditCommand)(payload.Deserialize(commandType, Wire) ?? throw new JsonException("Command required.")));
                        }
                        var edit = lease.Commit(session, new([.. commands], Revision(args), args.TryGetProperty("dryRun", out var dry) && dry.GetBoolean()), cancellationToken);
                        if (edit.Success && !(args.TryGetProperty("dryRun", out dry) && dry.GetBoolean())) changed();
                        value = edit; break;
                    case "undo": case "redo":
                        var history = lease.Run(() => name == "undo" ? session.Undo(Revision(args), cancellationToken) : session.Redo(Revision(args), cancellationToken), true, cancellationToken);
                        if (history.Success) changed(); value = history; break;
                    case "clapper_resolve":
                        var project = session.GetProject().Project ?? throw new JsonException("Project required.");
                        value = ClapperQueries.Resolve(project,args.GetProperty("sequenceId").GetGuid(),args.GetProperty("name").GetString()!); break;
                    case "recipe_validate":
                        value = await new RecipeCompiler().CompileAsync(args.GetProperty("source").GetString()!, cancellationToken); break;
                    default: return Error(id, -32602, "Unknown tool.");
                }
                var payloadResult = JsonSerializer.SerializeToElement(value,Wire);
                bool failed = payloadResult.TryGetProperty("error",out _) ||
                    payloadResult.TryGetProperty("success",out var success) && success.ValueKind == JsonValueKind.False;
                result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, Wire) } }, isError = failed };
            }
            else return Error(id, -32601, "Method not found.");
            lease.Demand(cancellationToken: cancellationToken);
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        }
        catch (UnauthorizedAccessException) { return Error(id, -32001, "Permission denied."); }
        catch (McpRequestException e) { return Error(null, e.Code, e.Message); }
        catch (OperationCanceledException) { return Error(id, -32000, "Request cancelled."); }
        catch (JsonException) { return Error(id, -32602, "Invalid tool arguments."); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        { return Error(id, -32602, "Invalid tool arguments."); }
    }
    private static bool AllowedCommand(Type type) => type != typeof(CreateProject) && type != typeof(RegisterMedia) &&
        type != typeof(RelinkMedia) && type != typeof(SetGeneratedProvenance);

    private static void ValidateArguments(string? name,JsonElement args)
    {
        string[] allowed = name switch
        {
            "get_project" => [], "edit_batch" => ["expectedRevision","commands","dryRun"], "undo" or "redo" => ["expectedRevision"],
            "clapper_resolve" => ["sequenceId","name"], "recipe_validate" => ["source"],
            "export_start" => ["expectedRevision","sequenceId","outputPath"], "job_status" or "job_cancel" => ["jobId"],
            "recipe_generate" => ["expectedRevision","sequenceId","recipe","outputPath","replaceMediaId"],
            _ => throw new JsonException("Unknown tool.")
        };
        if(args.ValueKind != JsonValueKind.Object) throw new JsonException("Arguments must be an object.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in args.EnumerateObject()) if(!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException("Unknown or duplicate tool argument.");
    }

    private static long Revision(JsonElement args) => long.Parse(args.GetProperty("expectedRevision").GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);
    private static string Error(JsonElement? id, int code, string message) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
    private bool VisibleTool(object tool)
    {
        string name = JsonSerializer.SerializeToElement(tool).GetProperty("name").GetString()!;
        return name is "get_project" or "clapper_resolve" or "recipe_validate" ||
            lease.Permission == McpPermission.Edit && name is "edit_batch" or "undo" or "redo";
    }
    private IEnumerable<object> ToolDefinitions()
    {
        object schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);
        yield return new { name = "get_project", description = "Read the same visible Project, revision and transient editor context.", inputSchema = schema("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}") };
        yield return new { name = "edit_batch", description = "Atomic typed commands. Each command has a type discriminator and camelCase constructor fields. All Int64 values, including ticks, are decimal strings. Allowed types: " + string.Join(", ", Commands.Keys), inputSchema = schema("{\"type\":\"object\",\"properties\":{\"expectedRevision\":{\"type\":\"string\"},\"dryRun\":{\"type\":\"boolean\"},\"commands\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":10000,\"items\":{\"type\":\"object\",\"required\":[\"type\"],\"properties\":{\"type\":{\"type\":\"string\"}}}}},\"required\":[\"expectedRevision\",\"commands\"],\"additionalProperties\":false}") };
        foreach (var name in new[] { "undo", "redo" }) yield return new { name, description = "Travel the shared editor history.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"expectedRevision\":{\"type\":\"string\"}},\"required\":[\"expectedRevision\"],\"additionalProperties\":false}") };
        yield return new { name = "clapper_resolve", description = "Resolve a sequence-local Clapper name to stable identity and coordinates.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"sequenceId\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"}},\"required\":[\"sequenceId\",\"name\"]}") };
        yield return new { name = "recipe_validate", description = "Compile bounded literal-only Python text/particles calls without executing user code.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\",\"maxLength\":65536}},\"required\":[\"source\"]}") };
    }
    private sealed class IntegerStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw new JsonException("Int64 requires a decimal string.");
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
