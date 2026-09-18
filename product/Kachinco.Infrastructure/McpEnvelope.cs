using System.Text.Json;

namespace Kachinco.Infrastructure;

// One validation path for busy and normal requests. Never return a borrowed JsonElement.
public static class McpEnvelope
{
    public static JsonElement Parse(string line)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(line) > 4 * 1024 * 1024)
            throw new McpRequestException(-32600);
        JsonDocument document;
        try { document = JsonDocument.Parse(line, new() { MaxDepth = 64 }); }
        catch (JsonException) { throw new McpRequestException(-32700); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new McpRequestException(-32600);
            RejectDuplicates(root);
            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(method.GetString()))
                throw new McpRequestException(-32600);
            if (root.TryGetProperty("id", out var id) && !(id.ValueKind == JsonValueKind.String || id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)))
                throw new McpRequestException(-32600);
            if (root.TryGetProperty("params", out var args) && args.ValueKind != JsonValueKind.Object)
                throw new McpRequestException(-32602);
            return root.Clone();
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new McpRequestException(-32600);
                RejectDuplicates(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    public static string Error(JsonElement? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } });
}

public sealed class McpRequestException(int code) : Exception("Invalid MCP request.")
{
    public int Code { get; } = code;
}
