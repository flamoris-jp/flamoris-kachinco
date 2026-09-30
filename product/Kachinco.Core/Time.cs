using System.Numerics;
using Kachinco.Native;

namespace Kachinco.Core;

public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public bool IsValid => TimelineTime.Native.IsValidFrameRate(Numerator, Denominator);

    public static FrameRate Create(int numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        var gcd = (int)BigInteger.GreatestCommonDivisor(numerator, denominator);
        var rate = new FrameRate(numerator / gcd, denominator / gcd);
        if (!rate.IsValid) throw new ArgumentOutOfRangeException(nameof(numerator), "FPS must be between 1 and 240.");
        return rate;
    }
}

public static class TimelineTime
{
    public const long TicksPerSecond = 35_280_000;

    // Decimal seconds are an input/display adapter, never persistent authority.
    public static long SecondsToTicks(decimal seconds)
    {
        if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        return checked((long)decimal.Round(checked(seconds * TicksPerSecond), 0, MidpointRounding.AwayFromZero));
    }

    public static long RoundHalfUp(BigInteger numerator, BigInteger denominator)
    {
        if (numerator < 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        return checked((long)((2 * numerator + denominator) / (2 * denominator)));
    }

    // One process-wide stateless ABI context, held by SafeHandle until process exit.
    private static readonly Lazy<NativeRuntime> runtime = new(NativeRuntime.Create);
    internal static NativeRuntime Native => runtime.Value;
    private static long Convert(Func<long> operation, string parameter)
    {
        try { return operation(); }
        catch (NativeRuntimeException ex) when (ex.Status == NativeStatus.InvalidArgument)
        { throw new ArgumentOutOfRangeException(parameter); }
        catch (NativeRuntimeException ex) when (ex.Status == NativeStatus.Overflow)
        { throw new OverflowException(ex.Message, ex); }
    }
    public static long FrameToTicks(long frameIndex, FrameRate fps) =>
        Convert(() => Native.FrameToTicks(frameIndex, fps.Numerator, fps.Denominator), nameof(frameIndex));
    public static long TicksToFrame(long tick, FrameRate fps) =>
        Convert(() => Native.TicksToFrame(tick, fps.Numerator, fps.Denominator), nameof(tick));
    public static long FrameCount(long durationTicks, FrameRate fps) =>
        Convert(() => Native.FrameCount(durationTicks, fps.Numerator, fps.Denominator), nameof(durationTicks));
    public static long SampleToTicks(long sampleIndex, int sampleRate) =>
        Convert(() => Native.SampleToTicks(sampleIndex, sampleRate), nameof(sampleIndex));
    public static long SampleCount(long durationTicks, int sampleRate) =>
        Convert(() => Native.SampleCount(durationTicks, sampleRate), nameof(durationTicks));

    public static bool ValidRange(long start, long duration, long limit = long.MaxValue) =>
        start >= 0 && duration > 0 && start <= limit && duration <= limit - start;

    public static bool Contains(long start, long duration, long tick) =>
        tick >= start && tick - start < duration;
}
