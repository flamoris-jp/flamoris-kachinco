using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;

namespace Kachinco.Cli;

internal static class KineticCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length == 0) throw new ArgumentException("Missing kinetai subcommand.");
        string command = args[0]; bool creates = command == "new";
        if (!creates && args.Length < 2) throw new ArgumentException("Composition/manifest/project path required.");
        string? input = creates ? null : args[1];
        string[] allowed = command switch {
            "new" => ["text", "output", "width", "height", "duration"],
            "import-glyph" => ["output", "width", "height", "untimed-duration"],
            "validate" => [], "frame" => ["time", "output"], "export" => ["output"],
            "script" => ["script", "output"], "add" => ["composition", "output", "time", "sequence"],
            _ => throw new ArgumentException("Unknown kinetai command.") };
        var options = Program.Options.Parse(args[(creates ? 1 : 2)..], allowed);
        string? output = command == "validate" ? null : options.Require("output");
        if (output is not null) CliOperations.NewOutput(output);
        if (command == "add")
        {
            var doc = KineticComposition.Read(options.Require("composition"));
            using var host = new HeadlessSession(input!, output, writable: true); await host.LoadAsync(token);
            var snapshot = host.Session.GetProject(); var project = snapshot.Project!;
            var sequence = options.Get("sequence") is { } id ? project.Sequences.Single(s => s.Id == Guid.Parse(id)) :
                project.Sequences.Length == 1 ? project.Sequences[0] : throw new ArgumentException("Specify --sequence UUID.");
            long start = Seconds(options.Get("time") ?? "0"), duration = KineticComposition.Duration(doc);
            if (start < 0) throw new ArgumentException("Insertion time must be nonnegative.");
            var asset = new MediaAsset(Guid.NewGuid(), Path.GetFileNameWithoutExtension(options.Require("composition")), options.Require("composition"),
                MediaKind.KineticText, duration) { TextComposition = doc.ToJsonString() };
            Guid track = Guid.NewGuid(); var edits = new List<EditCommand> { new RegisterMedia(asset), new AddTrack(sequence.Id, track, "KineticTypo", TrackKind.Video) };
            if (checked(start + duration) > sequence.DurationTicks) edits.Add(new SetSequenceDuration(sequence.Id, checked(start + duration)));
            edits.Add(new InsertClip(sequence.Id, track, new(Guid.NewGuid(), asset.Id, start, 0, duration, true, ClipAppearance.Default, AudioProperties.Default)));
            var result = host.Session.Execute(new([.. edits], snapshot.Revision), token);
            if (!result.Success) throw new InvalidDataException(string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            host.Save(); Write(new { output, assetId = asset.Id, trackId = track }); return 0;
        }
        JsonObject document;
        if (command is "new" or "import-glyph")
        {
            var basis = KineticComposition.New(int.Parse(options.Get("width") ?? "1280", CultureInfo.InvariantCulture), int.Parse(options.Get("height") ?? "720", CultureInfo.InvariantCulture));
            if (command == "import-glyph") document = GlyphBundleImporter.Read(input!, basis,
                options.Get("untimed-duration") is { } time ? Seconds(time) : null);
            else
            {
                document = basis; long duration = Seconds(options.Get("duration") ?? "5");
                document["durationTicks"] = duration.ToString(CultureInfo.InvariantCulture);
                var clip = document["clips"]![0]!; clip["text"] = options.Require("text"); clip["durationTicks"] = duration.ToString(CultureInfo.InvariantCulture);
                clip["x"] = document["width"]!.GetValue<int>() * .1; clip["y"] = document["height"]!.GetValue<int>() * .35;
                clip["fontSize"] = Math.Min(64, document["height"]!.GetValue<int>() * .2); clip["program"] = KineticComposition.Preset("none");
                KineticComposition.Validate(document);
            }
        }
        else document = KineticComposition.Read(input!);
        if (command == "validate") { Write(new { valid = true, version = document["version"]!.GetValue<int>(), clips = document["clips"]!.AsArray().Count }); return 0; }
        if (command == "script")
        {
            string source = await Program.ReadBoundedAsync(options.Require("script"), 65536, token);
            var program = await new KineticProgramCompiler().CompileAsync(source, token);
            foreach (var clip in document["clips"]!.AsArray()) { clip!["source"] = source; clip["program"] = program.DeepClone(); }
            KineticComposition.Validate(document);
        }
        if (command == "frame")
        {
            if (!Path.GetExtension(output!).Equals(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Kinetai frame output must be .png.");
            long tick = Seconds(options.Require("time")); if (tick < 0 || tick >= KineticComposition.Duration(document)) throw new ArgumentException("Frame is outside composition.");
            int width = document["width"]!.GetValue<int>(), height = document["height"]!.GetValue<int>();
            token.ThrowIfCancellationRequested(); using var renderer = new NativeKineticRenderer();
            var pixels = renderer.Render(document.ToJsonString(), tick, width, height);
            var frame = new RenderedVideoFrame(0, tick, width, height, ImmutableArray.CreateRange(pixels.Rgba));
            var bytes = await new FfmpegImageEncoder().EncodeAsync(frame, width, height, "png", 90, token);
            await CliOperations.PublishAsync(output!, bytes, token); Write(new { output, renderer = renderer.Info }); return 0;
        }
        if (command == "export") { string manifest = await KineticPair.ExportAsync(document, output!, token: token); Write(new { manifest }); return 0; }
        await CliOperations.PublishAsync(output!, Encoding.UTF8.GetBytes(KineticComposition.Validate(document).ToJsonString(new() { WriteIndented = true })), token);
        Write(new { output, clips = document["clips"]!.AsArray().Count }); return 0;
    }
    private static long Seconds(string text) => TimelineTime.SecondsToTicks(decimal.Parse(text, CultureInfo.InvariantCulture));
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
