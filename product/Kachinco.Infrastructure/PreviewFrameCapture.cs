using System.Globalization;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed record PreviewFrameOutput(string Format = "jpeg", int Width = 960, int? Quality = null);
public sealed record PreviewFrameInput(Guid SequenceId, long TimeTicks, PreviewFrameOutput Output);
public interface IPreviewImageEncoder
{
    Task<byte[]> EncodeAsync(RenderedVideoFrame frame, int width, int height, string format, int quality, CancellationToken token);
}

// Per-window admission, independent of grant/reconnect and the playback mailbox.
public sealed class PreviewFrameCapture(IPreviewImageEncoder encoder, ICaptionRasterizer? captions = null,
    Func<string?, IFrameRenderer>? rendererFactory = null)
{
    private readonly SemaphoreSlim admission = new(1, 1);
    public static (int Width, int Height) OutputSize(int width, int height, int maximumWidth)
    {
        double scale = Math.Min(1, Math.Min((double)maximumWidth / width, 1920d / height));
        scale = Math.Min(scale, Math.Sqrt(2_073_600d / ((long)width * height)));
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }
    public HostTool CreateTool(EditorSession session, Func<string?> projectPath, Func<HostSnapshot> identity) =>
        new HostImageTool<PreviewFrameInput>("preview_frame",
            "Read one composited sequence frame without moving playback. JPEG/PNG image plus metadata; max 1 MiB. Ticks are decimal strings. Width is a maximum; output also fits 1920px height and 2073600 pixels.",
            Schema, Decode, async (context, input, token) =>
            {
                if (!admission.Wait(0)) throw new McpFault(McpErrors.Busy);
                try
                {
                    ProjectSnapshot snapshot = null!; string? path = null; HostSnapshot host = null!;
                    await context.ReadAsync(() => { snapshot = session.GetProject(); path = projectPath(); host = identity(); return JsonSerializer.SerializeToElement(new {}); });
                    var project = snapshot.Project!;
                    var sequence = project.Sequences.FirstOrDefault(s => s.Id == input.SequenceId);
                    if (sequence is null) throw new McpFault("sequence_not_found");
                    if (input.TimeTicks >= sequence.DurationTicks) throw new McpFault("time_out_of_range");
                    int w = sequence.Settings.Width, h = sequence.Settings.Height;
                    if (w > 4096 || h > 4096 || (long)w * h > 8_294_400) throw new McpFault("capture_limit");
                    long index = Math.Min(TimelineTime.TicksToFrame(input.TimeTicks, sequence.Settings.FrameRate),
                        TimelineTime.FrameCount(sequence.DurationTicks, sequence.Settings.FrameRate) - 1);
                    long tick = TimelineTime.FrameToTicks(index, sequence.Settings.FrameRate);
                    var evaluator = TimelineEvaluator.Create(project, sequence.Id);
                    if (!evaluator.Success) throw new McpFault(evaluator.Diagnostics[0].Code);
                    using var evaluation = evaluator.Value!;
                    var evaluated = evaluation.Evaluate(tick);
                    if (!evaluated.Success) throw new McpFault(evaluated.Diagnostics[0].Code);
                    if (evaluated.Value!.VideoLayers.Length > 64) throw new McpFault("capture_limit");
                    using var decoder = new FfmpegMediaDecoder();
                    var renderer = rendererFactory?.Invoke(path) ?? new SharedFrameRenderer(decoder, path, captions);
                    var rendered = await renderer.RenderAsync(project, evaluated.Value!, index, token);
                    if (!rendered.Success) throw new McpFault(rendered.Diagnostics[0].Code);
                    var size = OutputSize(w, h, input.Output.Width);
                    byte[] bytes;
                    try { bytes = await encoder.EncodeAsync(rendered.Value!, size.Width, size.Height, input.Output.Format, input.Output.Quality ?? 85, token); }
                    catch (PreviewImageTooLargeException) { throw new McpFault("response_too_large"); }
                    token.ThrowIfCancellationRequested();
                    if (bytes.Length is < 1 or > McpImage.MaximumBytes) throw new McpFault("response_too_large");
                    var mime = input.Output.Format == "png" ? "image/png" : "image/jpeg";
                    var image = new McpImage(mime, bytes);
                    var metadata = await context.ReadAsync(() => {
                        if (session.DocumentToken != host.DocumentToken) throw new McpFault(McpErrors.StaleDocument);
                        if (session.GetProject().Revision != snapshot.Revision || projectPath() != path) throw new McpFault(McpErrors.StaleRevision);
                        return JsonSerializer.SerializeToElement(new {
                            runtimeId = host.RuntimeId, documentToken = host.DocumentToken, sequenceId = sequence.Id,
                            projectRevision = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
                            requestedTimeTicks = input.TimeTicks.ToString(CultureInfo.InvariantCulture),
                            resolvedFrameIndex = index.ToString(CultureInfo.InvariantCulture), resolvedTimeTicks = tick.ToString(CultureInfo.InvariantCulture),
                            width = size.Width, height = size.Height, mimeType = mime, encodedBytes = bytes.Length,
                            quality = input.Output.Format == "jpeg" ? input.Output.Quality ?? 85 : (int?)null });
                    });
                    return new McpResult(metadata) { Image = image };
                }
                catch (System.ComponentModel.Win32Exception) { throw new McpFault("renderer_unavailable"); }
                finally { admission.Release(); }
            });

    public static PreviewFrameInput Decode(JsonElement json)
    {
        try { return DecodeCore(json); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
        { throw new JsonException("Invalid preview frame input.", e); }
    }
    private static PreviewFrameInput DecodeCore(JsonElement json)
    {
        Fields(json, ["sequenceId", "timeTicks", "output"], "sequenceId", "timeTicks");
        if (!json.GetProperty("sequenceId").TryGetGuid(out var id) || id == Guid.Empty) throw new JsonException();
        string? text = json.GetProperty("timeTicks").GetString();
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks < 0 || ticks.ToString(CultureInfo.InvariantCulture) != text) throw new JsonException();
        var output = new PreviewFrameOutput();
        if (json.TryGetProperty("output", out var o))
        {
            Fields(o, ["format", "width", "quality"]);
            string format = o.TryGetProperty("format", out var f) ? f.GetString()! : "jpeg";
            int width = o.TryGetProperty("width", out var w) ? w.GetInt32() : 960;
            int? quality = o.TryGetProperty("quality", out var q) ? q.GetInt32() : null;
            if (format is not ("jpeg" or "png") || width is < 64 or > 1920 || quality is < 1 or > 100 || format == "png" && quality is not null) throw new JsonException();
            output = new(format, width, quality);
        }
        return new(id, ticks, output);
    }
    private static void Fields(JsonElement value, string[] allowed, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject()) if (!allowed.Contains(p.Name) || !seen.Add(p.Name)) throw new JsonException();
        if (required.Any(k => !seen.Contains(k))) throw new JsonException();
    }
    public static JsonElement Schema => JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["sequenceId","timeTicks"],"properties":{
          "sequenceId":{"type":"string","format":"uuid"},
          "timeTicks":{"description":"35280000 ticks/second; nearest frame, ties forward; before sequence duration.","type":"string","pattern":"^(0|[1-9][0-9]*)$","maxLength":19},
          "output":{"type":"object","additionalProperties":false,"properties":{
            "format":{"type":"string","enum":["jpeg","png"]},"width":{"type":"integer","minimum":64,"maximum":1920},
            "quality":{"description":"JPEG only; omit for PNG.","type":"integer","minimum":1,"maximum":100}}}}}
        """);
}
public sealed class PreviewImageTooLargeException() : IOException("Encoded preview exceeds 1 MiB.");
