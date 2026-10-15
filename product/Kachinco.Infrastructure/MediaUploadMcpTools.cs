using System.Text.Json;
using System.Text.Json.Serialization;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public static class MediaUploadMcpTools
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    public sealed record BeginInput(Guid UploadId, string FileName, long SizeBytes, string Sha256);
    public sealed record WriteInput(Guid UploadId, long Offset, string DataBase64, string ChunkSha256);
    public sealed record IdInput(Guid UploadId);

    public static IReadOnlyList<HostTool> Create(EditorSession session, MediaUploadStore uploads, Action changed)
    {
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire);
        return [
            new HostTool<BeginInput>("media_upload_begin",
                "Reserve a bounded media upload. Caller UUID becomes asset ID. Supported image/video/audio; max 512 MiB. Edit grant required; no path or URL.",
                Schema<BeginInput>(), OperationKind.Mutation, Decode<BeginInput>,
                (context, input, token) => context.CommitAsync(() => Json(uploads.Begin(
                    input.UploadId, input.FileName, input.SizeBytes, input.Sha256, session.GetProject()))), foreground: false),
            new HostTool<WriteInput>("media_upload_write",
                "Write the next media chunk (max 128 KiB decoded). Identical retries are accepted; offsets are decimal strings. Edit grant required.",
                Schema<WriteInput>(), OperationKind.Mutation, Decode<WriteInput>,
                (context, input, token) => context.CommitAsync(() => Json(uploads.Write(
                    input.UploadId, input.Offset, input.DataBase64, input.ChunkSha256))), foreground: false),
            new HostTool<IdInput>("media_upload_finish",
                "Verify the complete upload and probe media, then register the asset in shared Undo/Redo. Does not insert a clip. Edit grant required.",
                Schema<IdInput>(), OperationKind.Mutation, Decode<IdInput>, async (context, input, token) => {
                    await context.ReadAsync(() => Json(new { ready = true }));
                    var prepared = await uploads.PrepareAsync(input.UploadId, token);
                    return await context.CommitAsync(() => {
                        var result = uploads.Finish(prepared, session, token);
                        try { changed(); } catch { /* notification cannot undo a committed import */ }
                        return Json(result);
                    });
                }),
            new HostTool<IdInput>("media_upload_cancel", "Discard only an unfinished owned upload. Imported files remain available for Undo/Redo. Edit grant required.",
                Schema<IdInput>(), OperationKind.Mutation, Decode<IdInput>,
                (context, input, token) => context.CommitAsync(() => Json(new { cancelled = uploads.Cancel(input.UploadId) })), foreground: false)
        ];
    }
    public static JsonElement Schema<T>()
    {
        var schema = McpTypedSchema.Describe(typeof(T));
        var properties = schema["properties"]!.AsObject();
        foreach (var key in new[] { "sha256", "chunkSha256" })
            if (properties[key] is { } digest) { digest["pattern"] = "^[0-9a-f]{64}$"; digest["maxLength"] = 64; }
        foreach (var key in new[] { "sizeBytes", "offset" })
            if (properties[key] is { } size) { size["pattern"] = "^(0|[1-9][0-9]*)$"; size["maxLength"] = 19; }
        if (properties["fileName"] is { } name) { name["minLength"] = 1; name["maxLength"] = 180; }
        if (properties["dataBase64"] is { } data) { data["minLength"] = 1; data["maxLength"] = MediaUploadStore.MaximumBase64Chars; }
        return JsonSerializer.SerializeToElement(schema);
    }
    private static T Decode<T>(JsonElement input)
    {
        McpTypedSchema.Validate(typeof(T), input);
        foreach (var key in new[] { "sizeBytes", "offset" })
            if (input.TryGetProperty(key, out var value) && (value.GetString() is not { } text ||
                !long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                number.ToString(System.Globalization.CultureInfo.InvariantCulture) != text)) throw new JsonException();
        return input.Deserialize<T>(Wire) ?? throw new JsonException();
    }
}
