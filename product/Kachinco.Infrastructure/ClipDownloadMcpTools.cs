using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public static class ClipDownloadMcpTools
{
    public sealed record IdInput(Guid DownloadId);
    public sealed record ReadInput(Guid DownloadId, long Offset, int Count = ClipDownloadStore.MaximumChunkBytes);
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true, Converters = { new TickConverter(), new JsonStringEnumConverter(allowIntegerValues: false) } };
    public static IReadOnlyList<HostTool> Create(EditorSession session, ClipDownloadStore store, Func<string?> projectPath) => Create(session, () => store, projectPath);

    public static IReadOnlyList<HostTool> Create(EditorSession session, Func<ClipDownloadStore> store, Func<string?> projectPath)
    {
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire);
        return [
            new HostTool<ClipDownloadInput>("clip_download_begin",
                "Render one edited clip in the background. ReadOnly/Edit. WAV PCM16 stereo 48kHz or silent H.264 MP4; black sequence canvas, Full/Half/Quarter. Clip-relative range, at most 30s/64MiB. inspectDisabled ignores enable/mute only. Poll status then read bounded base64 chunks. No paths or raw source access.",
                Schema<ClipDownloadInput>(), OperationKind.Query, Decode<ClipDownloadInput>,
                (context, input, token) => context.ReadAsync(() => Json(store().Begin(session.GetProject(), session.DocumentToken, projectPath(), input))), foreground: false),
            new HostTool<IdInput>("clip_download_status", "Read render state and verified file metadata. Edits, document/path changes and source changes invalidate the download; expires after 10 idle minutes.",
                Schema<IdInput>(), OperationKind.Query, Decode<IdInput>,
                (context, input, token) => context.ReadAsync(() => Json(store().Status(input.DownloadId, session.GetProject(), session.DocumentToken, projectPath()))), foreground: false),
            new HostTool<ReadInput>("clip_download_read", "Read actual bytes from a ready download, at most 131072 bytes per call. Verify chunkSha256, assemble offsets, verify metadata sizeBytes/sha256, save fileName. Audio analysis depends on client/model audio support.",
                Schema<ReadInput>(), OperationKind.Query, Decode<ReadInput>,
                (context, input, token) => context.ReadAsync(() => Json(store().Read(input.DownloadId, input.Offset, input.Count, session.GetProject(), session.DocumentToken, projectPath()))), foreground: false),
            new HostTool<IdInput>("clip_download_cancel", "Cancel/release an owned temporary download without changing project/history.",
                Schema<IdInput>(), OperationKind.Query, Decode<IdInput>,
                (context, input, token) => context.ReadAsync(() => Json(new { cancelled = store().Cancel(input.DownloadId) })), foreground: false)
        ];
    }
    public static JsonElement Schema<T>() => JsonSerializer.SerializeToElement(McpTypedSchema.Describe(typeof(T)));
    private static T Decode<T>(JsonElement json)
    {
        McpTypedSchema.Validate(typeof(T), json);
        return json.Deserialize<T>(Wire) ?? throw new JsonException();
    }
    private sealed class TickConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            string? text = reader.GetString();
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value.ToString(CultureInfo.InvariantCulture) == text
                ? value : throw new JsonException();
        }
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
