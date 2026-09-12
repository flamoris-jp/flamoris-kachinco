using System.Numerics;

namespace Kachinco.Core;

public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public bool IsValid => Numerator > 0 && Denominator > 0 &&
        Numerator <= (long)Denominator * 240 && Numerator >= Denominator &&
        BigInteger.GreatestCommonDivisor(Numerator, Denominator) == 1;

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

    public static long RoundHalfUp(BigInteger numerator, BigInteger denominator)
    {
        if (numerator < 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        return checked((long)((2 * numerator + denominator) / (2 * denominator)));
    }

    public static long FrameToTicks(long frameIndex, FrameRate fps)
    {
        if (frameIndex < 0 || !fps.IsValid) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return RoundHalfUp((BigInteger)frameIndex * TicksPerSecond * fps.Denominator, fps.Numerator);
    }

    // Number of n >= 0 with roundHalfUp(n * T * den / num) < duration.
    public static long FrameCount(long durationTicks, FrameRate fps)
    {
        if (durationTicks < 0 || !fps.IsValid) throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (durationTicks == 0) return 0;
        var numerator = ((BigInteger)2 * durationTicks - 1) * fps.Numerator;
        var denominator = (BigInteger)2 * TicksPerSecond * fps.Denominator;
        return checked((long)((numerator + denominator - 1) / denominator));
    }

    public static long SampleToTicks(long sampleIndex, int sampleRate)
    {
        if (sampleIndex < 0 || sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleIndex));
        return RoundHalfUp((BigInteger)sampleIndex * TicksPerSecond, sampleRate);
    }

    public static long SampleCount(long durationTicks, int sampleRate)
    {
        if (durationTicks < 0 || sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (durationTicks == 0) return 0;
        var n = ((BigInteger)2 * durationTicks - 1) * sampleRate;
        var d = (BigInteger)2 * TicksPerSecond;
        return checked((long)((n + d - 1) / d));
    }

    public static bool ValidRange(long start, long duration, long limit = long.MaxValue) =>
        start >= 0 && duration > 0 && start <= limit && duration <= limit - start;

    public static bool Contains(long start, long duration, long tick) =>
        tick >= start && tick - start < duration;
}
