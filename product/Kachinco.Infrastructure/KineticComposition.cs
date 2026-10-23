using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Native;

namespace Kachinco.Infrastructure;

/// <summary>Input/file adapters only. Native commands validate and own committed state.</summary>
public static class KineticComposition
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public static JsonObject New(int width = 1280, int height = 720, FrameRate? fps = null)
    {
        using var session = new NativeKineticDocument();
        var doc = session.Get()["document"]!.AsObject();
        doc["id"] = Guid.NewGuid().ToString(); doc["width"] = width; doc["height"] = height;
        doc["fpsNumerator"] = (fps ?? new(30, 1)).Numerator; doc["fpsDenominator"] = (fps ?? new(30, 1)).Denominator;
        doc["clips"]!.AsArray().Add(Clip("FLAMORIS / キネタイ", 0, TimelineTime.SecondsToTicks(5)));
        return Validate(doc);
    }
    public static JsonObject Clip(string text, long start, long duration, string provenance = "manual") => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["text"] = text, ["startTicks"] = start.ToString(CultureInfo.InvariantCulture),
        ["durationTicks"] = duration.ToString(CultureInfo.InvariantCulture), ["fontFamily"] = "Yu Gothic", ["fontSize"] = 64,
        ["color"] = new JsonArray(1, 1, 1, 1), ["strokeColor"] = new JsonArray(0, 0, 0, 1), ["strokeWidth"] = 2,
        ["shadowColor"] = new JsonArray(0, 0, 0, 0.5), ["shadowX"] = 4, ["shadowY"] = 4,
        ["x"] = 100, ["y"] = 250, ["seed"] = 1, ["source"] = "", ["provenance"] = provenance,
        ["program"] = Preset("shake")
    };
    public static JsonObject Preset(string name) => new()
    {
        ["version"] = 1,
        ["ops"] = name switch
        {
            "shake" => new JsonArray(new JsonObject { ["op"] = "shake", ["amplitude"] = 8, ["frequency"] = 12 }),
            "flight" => new JsonArray(new JsonObject { ["op"] = "stagger", ["delayTicks"] = TimelineTime.SecondsToTicks(0.035m).ToString() },
                new JsonObject { ["op"] = "flight", ["x"] = -500, ["y"] = -200, ["durationTicks"] = TimelineTime.SecondsToTicks(0.5m).ToString() }),
            "scatter" => new JsonArray(new JsonObject { ["op"] = "stagger", ["delayTicks"] = TimelineTime.SecondsToTicks(0.02m).ToString() },
                new JsonObject { ["op"] = "shake", ["amplitude"] = 120, ["frequency"] = 1.5 },
                new JsonObject { ["op"] = "rotate", ["degrees"] = 15 }),
            _ => new JsonArray()
        }
    };
    public static JsonObject Validate(JsonObject document)
    {
        using var session = new NativeKineticDocument();
        var result = session.Edit(document, 0, replace: true);
        if (result["success"]?.GetValue<bool>() != true) throw new InvalidDataException(result["error"]?.GetValue<string>() ?? "Invalid text composition.");
        return result["document"]!.AsObject();
    }
    public static JsonObject Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Text composition exceeds 16 MiB.");
        using var session = new NativeKineticDocument();
        var result = session.Request(new() { ["action"] = "deserialize", ["expectedRevision"] = 0, ["text"] = text.TrimStart('\uFEFF') });
        if (result["success"]?.GetValue<bool>() != true) throw new InvalidDataException(result["error"]?.GetValue<string>() ?? "Invalid text composition.");
        return result["document"]!.AsObject();
    }
    public static JsonObject Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Text composition exceeds 16 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        return Parse(reader.ReadToEnd());
    }
    public static void Save(string path, JsonObject document)
    {
        string text = Validate(document).ToJsonString(new() { WriteIndented = true });
        WriteAtomic(path, text);
    }
    public static void WriteAtomicForCompanion(string path, string text) { if (System.Text.Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Handoff too large."); WriteAtomic(path, text); }
    internal static void WriteAtomic(string path, string text)
    {
        var full = Path.GetFullPath(path); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true); } File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static long Tick(JsonNode node, string key) => long.Parse(node[key]!.GetValue<string>(), CultureInfo.InvariantCulture);
    public static long Duration(JsonObject document) => Tick(document, "durationTicks");
    public static JsonObject ImportLyrics(string text, bool srt, JsonObject basis)
    {
        if (Encoding.UTF8.GetByteCount(text) > 4 * 1024 * 1024) throw new InvalidDataException("Lyrics exceed 4 MiB.");
        var doc = (JsonObject)basis.DeepClone(); var clips = new JsonArray(); long end = 1;
        if (srt)
        {
            var parsed = SrtCodec.Parse(text);
            if (!parsed.Success) throw new InvalidDataException(string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
            foreach (var cue in parsed.Value) { clips.Add(Clip(cue.Text, cue.StartTicks, cue.DurationTicks, $"srt:{cue.Id}")); end = Math.Max(end, checked(cue.StartTicks + cue.DurationTicks)); }
        }
        else
        {
            var root = JsonNode.Parse(text)!;
            if (root is JsonObject obj && obj["format"] is { } f && f.GetValue<string>() != "ponpon_export") throw new InvalidDataException("Unsupported lyric format.");
            var lines = root as JsonArray ?? root["lines"] as JsonArray ?? root["items"] as JsonArray ?? throw new InvalidDataException("Expected PONPON lines.");
            if (lines.Count > 10000) throw new InvalidDataException("Too many lyric lines.");
            foreach (var line in lines)
            {
                if (line is null) throw new InvalidDataException("Null lyric line.");
                decimal start = (line["start"] ?? line["pressTime"] ?? throw new InvalidDataException("Missing lyric start.")).GetValue<decimal>();
                decimal until = (line["end"] ?? line["releaseTime"] ?? throw new InvalidDataException("Missing lyric end.")).GetValue<decimal>();
                long a = TimelineTime.SecondsToTicks(start), b = TimelineTime.SecondsToTicks(until);
                if (b <= a) throw new InvalidDataException("Lyric end must follow start.");
                var source = line["source_id"] ?? line["id"] ?? line["index"];
                clips.Add(Clip((line["text"] ?? line["lyrics_text"])!.GetValue<string>(), a, b-a, "ponpon:" + (source?.ToJsonString() ?? "line"))); end = Math.Max(end, b);
            }
        }
        doc["clips"] = clips; doc["durationTicks"] = Math.Max(Duration(doc), end).ToString(CultureInfo.InvariantCulture);
        return Validate(doc);
    }
}
