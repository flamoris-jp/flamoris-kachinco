using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public static class TitleImageMcpTools
{
    public sealed record CaptureInput(Guid SequenceId, long TimeTicks);
    public sealed record ImageInput(Guid SequenceId);
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString };
    public static IReadOnlyList<HostTool> Create(EditorSession session, TitleImageService titles, IPreviewImageEncoder encoder,
        Func<string?> projectPath, Action changed)
    {
        var admission = new SemaphoreSlim(1, 1);
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire);
        return [
            new HostTool<CaptureInput>("title_image_from_frame",
                "Capture a full composited sequence frame as an immutable PNG asset and select it as title image. Shared Undo/Redo. Does not reserve the first frame automatically.",
                Json(McpTypedSchema.Describe(typeof(CaptureInput))), OperationKind.Transaction, Decode<CaptureInput>,
                async (context, input, token) => {
                    if (!admission.Wait(0)) throw new McpFault(McpErrors.Busy);
                    try
                    {
                        ProjectSnapshot snapshot = null!; string? path = null; string document = "";
                        await context.ReadAsync(() => { snapshot = session.GetProject(); path = projectPath(); document = session.DocumentToken; return Json(new {}); });
                        using var prepared = await titles.PrepareAsync(snapshot.Project!, input.SequenceId, input.TimeTicks, path, token);
                        return await context.CommitAsync(() => {
                            if (session.DocumentToken != document) throw new McpFault(McpErrors.StaleDocument);
                            if (projectPath() != path) throw new McpFault(McpErrors.StaleRevision);
                            var result = session.Execute(prepared.Batch(input.SequenceId, snapshot.Revision), token);
                            if (result.Success) { prepared.Keep(); try { changed(); } catch { } }
                            return Json(new { result.Success, revision = result.Revision.ToString(CultureInfo.InvariantCulture), result.Diagnostics,
                                mediaAssetId = result.Success ? prepared.Asset.Id : (Guid?)null });
                        });
                    }
                    catch (InvalidDataException) { throw new McpFault("title_capture_failed"); }
                    catch (InvalidOperationException) { throw new McpFault("sequence_not_found"); }
                    finally { admission.Release(); }
                }),
            new HostImageTool<ImageInput>("title_image",
                "Read/export the selected title image as a standalone JPEG (maximum 1920 wide, 1 MiB). Does not seek or change the project; use SetTitleImage through edit_batch to select an uploaded image.",
                Json(McpTypedSchema.Describe(typeof(ImageInput))), Decode<ImageInput>,
                async (context, input, token) => {
                    if (!admission.Wait(0)) throw new McpFault(McpErrors.Busy);
                    try
                    {
                        ProjectSnapshot snapshot = null!; string? path = null; string document = "";
                        await context.ReadAsync(() => { snapshot = session.GetProject(); path = projectPath(); document = session.DocumentToken; return Json(new {}); });
                        var frame = await titles.RenderSelectionAsync(snapshot.Project!, input.SequenceId, path, token);
                        byte[] bytes = await encoder.EncodeAsync(frame, frame.Width, frame.Height, "jpeg", 85, token);
                        if (bytes.Length is < 1 or > McpImage.MaximumBytes) throw new McpFault("response_too_large");
                        var metadata = await context.ReadAsync(() => {
                            if (session.DocumentToken != document) throw new McpFault(McpErrors.StaleDocument);
                            if (session.GetProject().Revision != snapshot.Revision || projectPath() != path) throw new McpFault(McpErrors.StaleRevision);
                            return Json(new { sequenceId = input.SequenceId, projectRevision = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
                                width = frame.Width, height = frame.Height, mimeType = "image/jpeg", encodedBytes = bytes.Length });
                        });
                        return new McpResult(metadata) { Image = new McpImage("image/jpeg", bytes) };
                    }
                    catch (PreviewImageTooLargeException) { throw new McpFault("response_too_large"); }
                    catch (InvalidDataException) { throw new McpFault("title_image_unavailable"); }
                    catch (InvalidOperationException) { throw new McpFault("sequence_not_found"); }
                    finally { admission.Release(); }
                })
        ];
    }
    private static T Decode<T>(JsonElement input)
    {
        McpTypedSchema.Validate(typeof(T), input);
        if (input.GetProperty("sequenceId").GetGuid() == Guid.Empty) throw new JsonException();
        if (input.TryGetProperty("timeTicks", out var ticks) && long.Parse(ticks.GetString()!, CultureInfo.InvariantCulture) < 0) throw new JsonException();
        return input.Deserialize<T>(Wire) ?? throw new JsonException();
    }
}
