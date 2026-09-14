using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public static class SrtCodec
{
    private static readonly Regex Timing = new(@"^(\d{2,}):([0-5]\d):([0-5]\d),(\d{3}) --> (\d{2,}):([0-5]\d):([0-5]\d),(\d{3})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static Result<ImmutableArray<Caption>> Parse(string text)
    {
        if (text is null || text.Length > 4 * 1024 * 1024) return Fail("SRT exceeds the text limit.");
        var output = ImmutableArray.CreateBuilder<Caption>();
        try
        {
            foreach (var block in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Trim().Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Trim('\n').Split('\n');
                if (lines.Length < 3 || !int.TryParse(lines[0], out var index) || index <= 0) return Fail("Expected cue number, timestamp and text.");
                var match = Timing.Match(lines[1].Trim());
                if (!match.Success) return Fail("Expected HH:MM:SS,mmm --> HH:MM:SS,mmm.");
                long start = Read(match, 1), end = Read(match, 5);
                var content = string.Join('\n', lines.Skip(2));
                if (end <= start || string.IsNullOrWhiteSpace(content) || content.Length > 10000 || output.Count >= 10000) return Fail("Invalid cue range or text size.");
                output.Add(new(Guid.NewGuid(), start, end - start, content));
            }
            return Result<ImmutableArray<Caption>>.Ok(output.ToImmutable());
        }
        catch (Exception e) when (e is OverflowException or FormatException or RegexMatchTimeoutException) { return Fail("Invalid SRT time."); }
    }
    public static Result<string> Write(IEnumerable<Caption> captions)
    {
        var text = new StringBuilder(); int index = 0;
        foreach (var caption in captions.Where(x => x.Enabled).OrderBy(x => x.StartTicks).ThenBy(x => x.Id.ToString("N"), StringComparer.Ordinal))
        {
            if (!TimelineTime.ValidRange(caption.StartTicks, caption.DurationTicks) || string.IsNullOrWhiteSpace(caption.Text) || caption.Text.Contains("\n\n"))
                return Result<string>.Fail(Diagnostic.Error("SRT_INVALID_CAPTION", "Caption cannot be represented as a valid SRT cue.", caption.Id));
            long start = Milliseconds(caption.StartTicks), end = Milliseconds(caption.StartTicks + caption.DurationTicks);
            if (end <= start) return Result<string>.Fail(Diagnostic.Error("SRT_PRECISION_LOSS", "Caption is shorter than SRT millisecond precision.", caption.Id));
            text.Append(++index).Append('\n').Append(Format(start)).Append(" --> ").Append(Format(end)).Append('\n')
                .Append(caption.Text.Replace("\r\n", "\n").Replace('\r', '\n')).Append("\n\n");
        }
        return Result<string>.Ok(text.ToString());
    }
    private static long Read(Match m, int i)
    {
        long hours = long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        long ms = checked(((hours * 60 + int.Parse(m.Groups[i+1].Value, CultureInfo.InvariantCulture)) * 60 + int.Parse(m.Groups[i+2].Value, CultureInfo.InvariantCulture)) * 1000 + int.Parse(m.Groups[i+3].Value, CultureInfo.InvariantCulture));
        return TimelineTime.SecondsToTicks(ms / 1000m);
    }
    private static long Milliseconds(long ticks) => TimelineTime.RoundHalfUp((System.Numerics.BigInteger)ticks * 1000, TimelineTime.TicksPerSecond);
    private static string Format(long ms) => $"{ms / 3600000:D2}:{ms / 60000 % 60:D2}:{ms / 1000 % 60:D2},{ms % 1000:D3}";
    private static Result<ImmutableArray<Caption>> Fail(string message) => Result<ImmutableArray<Caption>>.Fail(Diagnostic.Error("SRT_INVALID", message));
}
