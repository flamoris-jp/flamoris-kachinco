using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class McpEditorAdapter(EditorSession session, Func<object> editorContext, Action changed,
    Func<string, JsonElement, Task<object>>? jobs = null)
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
    public async Task<string?> HandleAsync(string line)
    {
        JsonElement? id = null;
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(line) > 4 * 1024 * 1024) return Error(null, -32600, "Message too large.");
            using var document = JsonDocument.Parse(line, new() { MaxDepth = 64 });
            var root = document.RootElement;
            RejectDuplicates(root);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0") return Error(null, -32600, "Invalid JSON-RPC request.");
            if (root.TryGetProperty("id", out var requestId)) id = requestId.Clone();
            string? method = root.GetProperty("method").GetString();
            if (id is null) return null;
            object result;
            if (method == "initialize")
            {
                initialized = true;
                result = new { protocolVersion = "2025-03-26", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "Kachinco", version = "0.2.0" } };
            }
            else if (method == "ping") result = new { };
            else if (!initialized) return Error(id, -32002, "Initialize first.");
            else if (method == "tools/list") result = new { tools = ToolDefinitions() };
            else if (method == "tools/call")
            {
                var parameters = root.GetProperty("params");
                var name = parameters.GetProperty("name").GetString();
                var args = parameters.TryGetProperty("arguments", out var arguments) ? arguments : JsonSerializer.SerializeToElement(new { });
                ValidateArguments(name,args);
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
                            var payload = JsonNode.Parse(item.GetRawText())!.AsObject(); payload.Remove("type");
                            commands.Add((EditCommand)(payload.Deserialize(commandType, Wire) ?? throw new JsonException("Command required.")));
                        }
                        var edit = session.Execute(new([.. commands], Revision(args), args.TryGetProperty("dryRun", out var dry) && dry.GetBoolean()));
                        if (edit.Success && !(args.TryGetProperty("dryRun", out dry) && dry.GetBoolean())) changed();
                        value = edit; break;
                    case "undo": case "redo":
                        var history = name == "undo" ? session.Undo(Revision(args)) : session.Redo(Revision(args));
                        if (history.Success) changed(); value = history; break;
                    case "clapper_resolve":
                        var project = session.GetProject().Project ?? throw new JsonException("Project required.");
                        value = ClapperQueries.Resolve(project,args.GetProperty("sequenceId").GetGuid(),args.GetProperty("name").GetString()!); break;
                    case "recipe_validate":
                        value = await new RecipeCompiler().CompileAsync(args.GetProperty("source").GetString()!); break;
                    case "recipe_generate": case "export_start": case "job_status": case "job_cancel":
                        if (jobs is null) throw new JsonException("Jobs are not available on this host.");
                        value = await jobs(name, args); break;
                    default: return Error(id, -32602, "Unknown tool.");
                }
                var payloadResult = JsonSerializer.SerializeToElement(value,Wire);
                bool failed = payloadResult.TryGetProperty("error",out _) ||
                    payloadResult.TryGetProperty("success",out var success) && success.ValueKind == JsonValueKind.False;
                result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, Wire) } }, isError = failed };
            }
            else return Error(id, -32601, "Method not found.");
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        }
        catch (JsonException e) { return Error(id, -32602, e.Message); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        { return Error(id, -32602, "Invalid tool arguments."); }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if(value.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in value.EnumerateObject()) { if(!names.Add(p.Name)) throw new JsonException("Duplicate JSON property."); RejectDuplicates(p.Value); }
        }
        else if(value.ValueKind == JsonValueKind.Array) foreach(var item in value.EnumerateArray()) RejectDuplicates(item);
    }
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
    private IEnumerable<object> ToolDefinitions()
    {
        object schema(string json) => JsonSerializer.Deserialize<JsonElement>(json);
        yield return new { name = "get_project", description = "Read the same visible Project, revision and transient editor context.", inputSchema = schema("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}") };
        yield return new { name = "edit_batch", description = "Atomic typed commands. Each command has a type discriminator and camelCase constructor fields. All Int64 values, including ticks, are decimal strings. Allowed types: " + string.Join(", ", Commands.Keys), inputSchema = schema("{\"type\":\"object\",\"properties\":{\"expectedRevision\":{\"type\":\"string\"},\"dryRun\":{\"type\":\"boolean\"},\"commands\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":10000,\"items\":{\"type\":\"object\",\"required\":[\"type\"],\"properties\":{\"type\":{\"type\":\"string\"}}}}},\"required\":[\"expectedRevision\",\"commands\"],\"additionalProperties\":false}") };
        foreach (var name in new[] { "undo", "redo" }) yield return new { name, description = "Travel the shared editor history.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"expectedRevision\":{\"type\":\"string\"}},\"required\":[\"expectedRevision\"],\"additionalProperties\":false}") };
        yield return new { name = "clapper_resolve", description = "Resolve a sequence-local Clapper name to stable identity and coordinates.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"sequenceId\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"}},\"required\":[\"sequenceId\",\"name\"]}") };
        yield return new { name = "recipe_validate", description = "Compile bounded literal-only Python text/particles calls without executing user code.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\",\"maxLength\":65536}},\"required\":[\"source\"]}") };
        if (jobs is null) yield break;
        yield return new { name = "recipe_generate", description = "Render and commit a normal MOV clip. Recipe fields: id, clapperId, source, revision (int), seed (int), apiVersion='1', rendererVersion='1'. Optional replaceMediaId regenerates the same asset lineage and preserves clip edits.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"expectedRevision\":{\"type\":\"string\"},\"sequenceId\":{\"type\":\"string\"},\"recipe\":{\"type\":\"object\"},\"outputPath\":{\"type\":\"string\"},\"replaceMediaId\":{\"type\":\"string\"}},\"required\":[\"expectedRevision\",\"sequenceId\",\"recipe\",\"outputPath\"]}") };
        yield return new { name = "export_start", description = "Export the visible snapshot to MP4; return a job ID.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"expectedRevision\":{\"type\":\"string\"},\"sequenceId\":{\"type\":\"string\"},\"outputPath\":{\"type\":\"string\"}},\"required\":[\"expectedRevision\",\"sequenceId\",\"outputPath\"],\"additionalProperties\":false}") };
        foreach (var name in new[] { "job_status", "job_cancel" }) yield return new { name, description = "Inspect or cancel an editor export job.", inputSchema = schema("{\"type\":\"object\",\"properties\":{\"jobId\":{\"type\":\"string\"}},\"required\":[\"jobId\"],\"additionalProperties\":false}") };
    }
    private sealed class IntegerStringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw new JsonException("Int64 requires a decimal string.");
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
