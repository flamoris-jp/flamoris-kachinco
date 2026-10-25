using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using Flamoris.Mcp.Core;
using Flamoris.Mcp.Wpf;
using Kachinco.Infrastructure;
using Kachinco.Native;

namespace KineticTypo.App;

public sealed partial class EditorWindow : IMcpHost
{
    private McpDesktopUi? mcp;
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private long documentGeneration;
    public event Action? Invalidating;
    public HostSnapshot Snapshot => new("flamoris.kinetai", "0.2.0", runtimeId,
        runtimeId + ":" + documentGeneration, revision, !closed && document is not null, fieldsDirty || exporting || compilingScript);
    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken token)
    {
        if (Dispatcher.CheckAccess()) { token.ThrowIfCancellationRequested(); return action(); }
        return await Dispatcher.InvokeAsync(() => { token.ThrowIfCancellationRequested(); return action(); }, DispatcherPriority.Background, token);
    }
    private void InitializeMcp(Panel toolbar)
    {
        var menu = new Menu(); var item = new MenuItem { Header = "MCP / AI" }; menu.Items.Add(item); toolbar.Children.Add(menu);
        mcp = new(this, item, "flamoris-kinetai",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "KineticTypo", "mcp-connection.json"),
            "FLAMORIS.KineticTypo", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mcp", "Flamoris.Mcp.Bridge.exe")),
            AttachMcp, () => document is not null, () => CultureInfo.GetCultureInfo("ja-JP"));
        toolbar.Children.Add(mcp.StatusIndicator);
        Loaded += (_, _) => mcp.NotifyHostReady();
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement EmptySchema => Json(new { type = "object", additionalProperties = false, properties = new { } });
    private static bool Empty(JsonElement input) { if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any()) throw new JsonException(); return true; }
    private async Task<McpDesktopAttachment> AttachMcp(McpPermission permission)
    {
        var tools = new List<HostTool> {
            new HostTool<bool>("get_composition", "Read the visible native text document, revision, selection, playhead and actual text backend.", EmptySchema, OperationKind.Query, Empty,
                (context, _, token) => context.ReadAsync(() => Json(new { revision = revision.ToString(CultureInfo.InvariantCulture), composition = document,
                    selectedClipId = Selected?["id"]?.GetValue<string>(), playheadTicks = Tick.ToString(CultureInfo.InvariantCulture), backend = renderer?.Info }))),
            KineticScriptMcpTools.Create(),
            new HostTool<string>("edit_composition", "Apply one validated text-document command with ordinary Undo/Redo. The request guard must match the current revision.",
                Json(new { type = "object", additionalProperties = false, required = new[] { "compositionJson" }, properties = new { compositionJson = new { type = "string", maxLength = KineticComposition.MaximumBytes } } }),
                OperationKind.Transaction, input => { if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Count() != 1) throw new JsonException(); return input.GetProperty("compositionJson").GetString() ?? throw new JsonException(); },
                (context, input, token) => context.CommitAsync(() => { var next = KineticComposition.Parse(input); if (!System.Text.Json.Nodes.JsonNode.DeepEquals(next["id"], document["id"])) throw new McpFault("document_identity_mismatch"); Commit(next); return Json(new { revision = revision.ToString(CultureInfo.InvariantCulture) }); })),
            new HostTool<bool>("undo", "Undo one native text edit.", EmptySchema, OperationKind.Undo, Empty,
                (context, _, token) => context.CommitAsync(() => { History("undo"); return Json(new { revision = revision.ToString(CultureInfo.InvariantCulture) }); })),
            new HostTool<bool>("redo", "Redo one native text edit.", EmptySchema, OperationKind.Redo, Empty,
                (context, _, token) => context.CommitAsync(() => { History("redo"); return Json(new { revision = revision.ToString(CultureInfo.InvariantCulture) }); })),
            new HostImageTool<long>("preview_frame", "Render a specified canonical tick through native GPU text, without seeking. PNG up to 1 MiB, maximum 960px width.",
                Json(new { type = "object", additionalProperties = false, required = new[] { "timeTicks" }, properties = new { timeTicks = new { type = "string", pattern = "^[0-9]+$" } } }),
                input => { if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Count() != 1 || !long.TryParse(input.GetProperty("timeTicks").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var tick) || tick < 0) throw new JsonException(); return tick; },
                async (context, tick, token) => {
                    string source = "", identity = ""; long expected = 0; int w = 0, h = 0;
                    await context.ReadAsync(() => { if (tick >= KineticComposition.Duration(document)) throw new McpFault("time_out_of_range");
                        source = document.ToJsonString(); expected = revision; identity = Snapshot.DocumentToken;
                        double ratio = Math.Min(1, 960d / document["width"]!.GetValue<int>()); w = Math.Max(1, (int)(document["width"]!.GetValue<int>() * ratio)); h = Math.Max(1, (int)(document["height"]!.GetValue<int>() * ratio)); return Json(new { }); });
                    if (!await rendering.WaitAsync(0, token)) throw new McpFault(McpErrors.Busy);
                    try {
                        byte[] png = await Task.Run(() => {
                            token.ThrowIfCancellationRequested(); renderer ??= new NativeKineticRenderer(); var pixels = renderer.Render(source, tick, w, h);
                            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels.BgraPremultiplied, w * 4);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
                        }, token);
                        if (png.Length > McpImage.MaximumBytes) throw new McpFault("response_too_large");
                        var metadata = await context.ReadAsync(() => { if (revision != expected || Snapshot.DocumentToken != identity) throw new McpFault(McpErrors.StaleRevision); return Json(new { timeTicks = tick.ToString(CultureInfo.InvariantCulture), width = w, height = h, backend = renderer!.Info }); });
                        return new McpResult(metadata) { Image = new McpImage("image/png", png) };
                    } finally { rendering.Release(); }
                })
        };
        var boundary = new McpBoundary(this, tools, new McpOptions(), new McpDiagnostics(KachincoLogging.Create("kinetai").Logger));
        try { return new(boundary, await boundary.EnableAsync(permission)); } catch { boundary.Dispose(); throw; }
    }
    private void InvalidateMcp()
    {
        ++documentGeneration; mcp?.Invalidate(); Invalidating?.Invoke(); mcp?.Refresh(); mcp?.NotifyHostReady();
    }
}
