using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

/// <summary>Explicit local import. Persist numeric geometry, never playback file references.</summary>
public static class GlyphBundleImporter
{
    private static readonly Regex Variant = new("^g[0-9a-f]{24}$", RegexOptions.CultureInvariant);
    private static readonly Regex CueId = new("^cue[0-9]{4,}$", RegexOptions.CultureInvariant);
    private static readonly Regex Color = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new(@"[MLCQHVZmlcqhvz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);
    private const long MaximumAssets = 64_000_000;

    public static JsonObject Read(string manifestPath, JsonObject basis, long? untimedDurationTicks = null)
    {
        try { return ReadCore(manifestPath, basis, untimedDurationTicks); }
        catch (Exception e) when (e is System.Text.Json.JsonException or XmlException or ArgumentException or InvalidOperationException or
            NullReferenceException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid Glyph bundle: " + e.Message, e); }
    }
    private static JsonObject ReadCore(string manifestPath, JsonObject basis, long? untimedDurationTicks)
    {
        string full = Path.GetFullPath(manifestPath), root = Path.GetDirectoryName(full)!;
        byte[] bytes = ReadLocal(full, 20_000_000);
        var manifest = ParseJson(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
        if (manifest["schema_version"]?.GetValue<int>() != 2 || manifest["kind"]?.GetValue<string>() != "flamoris-glyph-bundle")
            throw new InvalidDataException("Expected a FLAMORIS Glyph schema-2 manifest.json.");
        var style = manifest["style"]!.AsObject();
        if (style["schema_version"]!.GetValue<int>() != 1) throw new InvalidDataException("Unsupported Glyph Style DNA version.");
        var palette = style["palette"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (palette.Count is < 1 or > 8 || palette.Any(c => !Color.IsMatch(c))) throw new InvalidDataException("Invalid Glyph palette.");
        var source = new JsonObject { ["song"] = manifest["song"]!.DeepClone(), ["style"] = style["name"]!.DeepClone(),
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["license"] = manifest["source"]!["license_note"]!.DeepClone() };
        var glyphs = new Dictionary<string, (string Character, double Advance, JsonArray Paths)>();
        var entries = manifest["glyphs"]!.AsArray();
        if (entries.Count is < 1 or > 5000) throw new InvalidDataException("Glyph count exceeds 5000.");
        long total = 0;
        foreach (var entry in entries)
        {
            var g = entry!.AsObject(); string id = g["variant_id"]!.GetValue<string>();
            if (!Variant.IsMatch(id) || glyphs.ContainsKey(id) || g["asset"]!.GetValue<string>() != $"glyphs/{id}.svg")
                throw new InvalidDataException("Invalid/duplicate Glyph identity or relative asset path.");
            string character = g["character"]!.GetValue<string>();
            if (character.EnumerateRunes().Count() != 1) throw new InvalidDataException("Glyph requires one Unicode scalar.");
            byte[] svg = ReadLocal(Path.Combine(root, "glyphs", id + ".svg"), 2_000_000);
            total = checked(total + svg.Length); if (total > MaximumAssets) throw new InvalidDataException("Glyph assets exceed 64 MB.");
            if (Convert.ToHexStringLower(SHA256.HashData(svg)) != g["sha256"]!.GetValue<string>()) throw new InvalidDataException("Glyph SVG SHA-256 mismatch.");
            var paths = ParseSvg(new UTF8Encoding(false, true).GetString(svg), palette);
            var strokes = g["strokes"]!.AsArray();
            if (strokes.Count != paths.Count || strokes.Where((s, i) => s!["element_id"]!.GetValue<string>() != paths[i]!["id"]!.GetValue<string>()).Any())
                throw new InvalidDataException("Glyph stroke IDs do not match SVG.");
            var metrics = g["metrics"]!;
            if (metrics["view_box"]!.ToJsonString() != "[0,0,1000,1000]" || metrics["anchor"]!.ToJsonString() != "[500,500]")
                throw new InvalidDataException("Unsupported Glyph coordinates/anchor.");
            double advance = metrics["advance"]!.GetValue<double>();
            if (!double.IsFinite(advance) || advance is <= 0 or > 4000) throw new InvalidDataException("Invalid Glyph advance.");
            glyphs.Add(id, (character, advance, paths));
        }
        var cues = manifest["cues"]!.AsArray(); var mapping = manifest["occurrences"]!.AsArray();
        if (cues.Count > 5000 || mapping.Count is < 1 or > 5000) throw new InvalidDataException("Glyph cue/occurrence limit.");
        var clips = new JsonArray(); var cueIds = new HashSet<string>(); var used = new HashSet<string>(); int occurrence = 0; long until = 1;
        foreach (var cueNode in cues)
        {
            var cue = cueNode!.AsObject(); string id = cue["id"]!.GetValue<string>(), text = cue["text"]!.GetValue<string>();
            if (!CueId.IsMatch(id) || !cueIds.Add(id) || !text.IsNormalized(NormalizationForm.FormC)) throw new InvalidDataException("Invalid cue ID or non-NFC text.");
            long start, duration;
            if (cue["start_ms"] is null && cue["end_ms"] is null)
            {
                if (untimedDurationTicks is not > 0) throw new InvalidDataException("Untimed Glyph cues need an explicit duration.");
                start = 0; duration = untimedDurationTicks.Value;
            }
            else
            {
                long a = cue["start_ms"]!.GetValue<long>(), b = cue["end_ms"]!.GetValue<long>();
                if (a < 0 || b <= a) throw new InvalidDataException("Invalid Glyph cue interval.");
                start = checked(a * (TimelineTime.TicksPerSecond / 1000)); duration = checked((b - a) * (TimelineTime.TicksPerSecond / 1000));
            }
            var units = new JsonArray(); int line = 1, column = 1, index = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (!WhiteSpace(rune))
                {
                    var category = Rune.GetUnicodeCategory(rune);
                    if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Control or UnicodeCategory.Format)
                        throw new InvalidDataException("Unsupported Glyph combining/sequence character.");
                    if (occurrence >= mapping.Count) throw new InvalidDataException("Incomplete Glyph occurrence map.");
                    var item = mapping[occurrence++]!; string variant = item["glyph_variant_id"]!.GetValue<string>(); ++index;
                    if (item["occurrence_id"]!.GetValue<string>() != $"{id}-char{index:D4}" || item["cue_id"]!.GetValue<string>() != id ||
                        item["character"]!.GetValue<string>() != rune.ToString() || item["line"]!.GetValue<int>() != line || item["column"]!.GetValue<int>() != column ||
                        !glyphs.TryGetValue(variant, out var glyph) || glyph.Character != rune.ToString()) throw new InvalidDataException("Glyph occurrence/text mapping mismatch.");
                    used.Add(variant);
                    units.Add(new JsonObject { ["occurrenceId"] = item["occurrence_id"]!.DeepClone(), ["variantId"] = variant, ["character"] = rune.ToString(), ["line"] = line,
                        ["column"] = column, ["advance"] = glyph.Advance, ["paths"] = glyph.Paths.DeepClone() });
                }
                if (rune.Value == 10) { ++line; column = 1; } else ++column;
            }
            if (units.Count == 0) continue;
            var clip = KineticComposition.Clip(text, start, duration, "flamoris-glyph:" + id);
            clip["color"] = new JsonArray(1, 1, 1, 1); clip["strokeWidth"] = 0; clip["shadowColor"] = new JsonArray(0, 0, 0, 0);
            clip["program"] = KineticComposition.Preset("none");
            clip["x"] = basis["width"]!.GetValue<int>() * .1; clip["y"] = basis["height"]!.GetValue<int>() * .35;
            clip["fontSize"] = Math.Min(96, basis["height"]!.GetValue<int>() * .15);
            clip["glyphs"] = new JsonObject { ["version"] = 1, ["source"] = source.DeepClone(), ["units"] = units };
            clips.Add(clip); until = Math.Max(until, checked(start + duration));
        }
        if (occurrence != mapping.Count || used.Count != glyphs.Count) throw new InvalidDataException("Extra/unreferenced Glyph entries.");
        var doc = (JsonObject)basis.DeepClone(); doc["version"] = 2; doc["clips"] = clips;
        doc["durationTicks"] = Math.Max(KineticComposition.Duration(doc), until).ToString(CultureInfo.InvariantCulture);
        return KineticComposition.Validate(doc);
    }

    private static bool WhiteSpace(Rune rune) => Rune.IsWhiteSpace(rune) || rune.Value is >= 0x1C and <= 0x1F;
    private static byte[] ReadLocal(string path, int maximum)
    {
        // Explicit local access only. Reject every symlink/reparse component;
        // no follow-and-copy of an outside file into an apparently portable bundle.
        for (FileSystemInfo? part = new FileInfo(path); part is not null; part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Glyph input may not use symlinks/reparse points.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("Glyph input exceeds size limit.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static JsonObject ParseJson(string text)
    {
        // JsonNode rejects duplicate object keys when materialized recursively.
        var root = JsonNode.Parse(text, documentOptions: new() { MaxDepth = 64 })!.AsObject();
        void Visit(JsonNode? node) { if (node is JsonObject o) foreach (var pair in o) Visit(pair.Value); else if (node is JsonArray a) foreach (var item in a) Visit(item); }
        Visit(root); return root;
    }
    private static JsonArray ParseSvg(string svg, HashSet<string> palette)
    {
        using var reader = XmlReader.Create(new StringReader(svg), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        var document = XDocument.Load(reader); var root = document.Root!; XNamespace ns = "http://www.w3.org/2000/svg";
        if (root.Name != ns + "svg" || root.Attribute("viewBox")?.Value != "0 0 1000 1000" ||
            root.Attributes().Any(a => !(a.Name == "viewBox" || a.IsNamespaceDeclaration && a.Name.LocalName == "xmlns" && a.Value == ns.NamespaceName)))
            throw new InvalidDataException("Glyph SVG requires the normalized viewBox.");
        var result = new JsonArray(); var ids = new HashSet<string>();
        foreach (var node in root.Nodes())
        {
            if (node is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) continue;
            if (node is not XElement path || path.Name != ns + "path" || path.HasElements || path.Value.Length != 0 || path.Attributes().Count() != 3 ||
                path.Attributes().Any(a => a.Name != "id" && a.Name != "d" && a.Name != "fill")) throw new InvalidDataException("Glyph SVG permits filled paths only.");
            string id = path.Attribute("id")!.Value, fill = path.Attribute("fill")!.Value;
            if (!Regex.IsMatch(id, "^stroke[0-9]{3}$", RegexOptions.CultureInvariant) || !ids.Add(id) || !palette.Contains(fill)) throw new InvalidDataException("Glyph stroke ID/color mismatch.");
            uint rgb = uint.Parse(fill.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            result.Add(new JsonObject { ["id"] = id, ["fill"] = new JsonArray((rgb >> 16 & 255) / 255d, (rgb >> 8 & 255) / 255d, (rgb & 255) / 255d, 1d),
                ["commands"] = ParsePath(path.Attribute("d")!.Value) });
        }
        if (result.Count is < 1 or > 64) throw new InvalidDataException("Glyph SVG stroke limit.");
        return result;
    }
    internal static JsonArray ParsePath(string data)
    {
        var matches = Token.Matches(data); int end = 0;
        foreach (Match match in matches) { if (data.AsSpan(end, match.Index - end).IndexOfAnyExcept(" ,\t\r\n".AsSpan()) >= 0) throw new InvalidDataException("Unsupported SVG path token."); end = match.Index + match.Length; }
        if (data.AsSpan(end).IndexOfAnyExcept(" ,\t\r\n".AsSpan()) >= 0 || matches.Count > 700_000) throw new InvalidDataException("Invalid/oversized SVG path.");
        var output = new JsonArray(); int at = 0; char op = '\0'; double x = 0, y = 0, sx = 0, sy = 0; bool open = false;
        double Number() { if (at >= matches.Count || char.IsLetter(matches[at].Value[0])) throw new InvalidDataException("Missing SVG coordinate."); double n = double.Parse(matches[at++].Value, CultureInfo.InvariantCulture); if (!double.IsFinite(n) || n is < -2000 or > 3000) throw new InvalidDataException("SVG coordinate limit."); return n; }
        void Add(string name, params double[] values) { var array = new JsonArray { name }; foreach (double v in values) array.Add(v); output.Add(array); }
        while (at < matches.Count)
        {
            if (char.IsLetter(matches[at].Value[0])) op = matches[at++].Value[0];
            if (op == '\0') throw new InvalidDataException("SVG path command required.");
            char upper = char.ToUpperInvariant(op); bool relative = char.IsLower(op);
            if (upper == 'Z') { if (!open) throw new InvalidDataException("SVG close without contour."); Add("Z"); x = sx; y = sy; open = false; op = '\0'; continue; }
            if (upper != 'M' && !open || upper == 'M' && open) throw new InvalidDataException("SVG contour must start with M and close with Z.");
            double a = Number(), b = upper is 'H' or 'V' ? 0 : Number(), dx = relative ? x : 0, dy = relative ? y : 0;
            if (upper is 'M' or 'L') { x = a + dx; y = b + dy; Add(upper.ToString(), x, y); if (upper == 'M') { sx = x; sy = y; open = true; op = relative ? 'l' : 'L'; } }
            else if (upper == 'H') { x = a + dx; Add("L", x, y); }
            else if (upper == 'V') { y = a + dy; Add("L", x, y); }
            else if (upper == 'C') { double c = Number() + dx, d = Number() + dy, ex = Number() + dx, ey = Number() + dy; Add("C", a + dx, b + dy, c, d, ex, ey); x = ex; y = ey; }
            else if (upper == 'Q') { double ex = Number() + dx, ey = Number() + dy, qx = a + dx, qy = b + dy; Add("C", x + (qx - x) * 2 / 3, y + (qy - y) * 2 / 3, ex + (qx - ex) * 2 / 3, ey + (qy - ey) * 2 / 3, ex, ey); x = ex; y = ey; }
            else throw new InvalidDataException("Unsupported SVG path command.");
            if (output.Count > 100_000) throw new InvalidDataException("SVG path command limit.");
        }
        if (open || output.Count == 0) throw new InvalidDataException("SVG contours must close.");
        return output;
    }
}
