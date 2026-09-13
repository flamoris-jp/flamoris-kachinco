using System.Numerics;
using Kachinco.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class TimeTests
{
    [TestMethod]
    public void DecimalSecondsUseCanonicalConversionAndRejectOverflow()
    {
        Assert.AreEqual(Fixture.T, TimelineTime.SecondsToTicks(1m));
        Assert.AreEqual(0L, TimelineTime.SecondsToTicks(0.000000001m));
        Assert.ThrowsExactly<OverflowException>(() => TimelineTime.SecondsToTicks(decimal.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TimelineTime.SecondsToTicks(-1));
    }
    [TestMethod]
    public void FrameRateReducesAndRejectsInvalidInputs()
    {
        Assert.AreEqual(new FrameRate(30000, 1001), FrameRate.Create(60000, 2002));
        Assert.IsFalse(new FrameRate(0, 1).IsValid);
        Assert.IsFalse(new FrameRate(60, 2).IsValid);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FrameRate.Create(1, 0));
    }
    [TestMethod]
    public void RoundingIsHalfUpAndUsesWideIntermediates()
    {
        Assert.AreEqual(1L, TimelineTime.RoundHalfUp(1, 2));
        Assert.AreEqual(1L, TimelineTime.RoundHalfUp(1, 1));
        Assert.AreEqual(0L, TimelineTime.RoundHalfUp(49, 100));
        Assert.AreEqual(long.MaxValue, TimelineTime.RoundHalfUp((BigInteger)long.MaxValue * 1000, 1000));
        Assert.ThrowsExactly<OverflowException>(() => TimelineTime.RoundHalfUp((BigInteger)long.MaxValue + 1, 1));
    }
    [TestMethod]
    [DataRow(24, 1)] [DataRow(30, 1)] [DataRow(60, 1)]
    [DataRow(24000, 1001)] [DataRow(30000, 1001)] [DataRow(60000, 1001)]
    public void CommonFramesAreExactWithoutAccumulatedDrift(int numerator, int denominator)
    {
        var fps = new FrameRate(numerator, denominator);
        long frame = 1_000_000;
        Assert.AreEqual((long)((BigInteger)frame * Fixture.T * denominator / numerator), TimelineTime.FrameToTicks(frame, fps));
        Assert.AreEqual(TimelineTime.FrameToTicks(1, fps) * frame, TimelineTime.FrameToTicks(frame, fps));
    }
    [TestMethod]
    [DataRow(44100)] [DataRow(48000)]
    public void AudioSamplesShareExactTickAuthority(int rate)
    {
        Assert.AreEqual(Fixture.T / rate, TimelineTime.SampleToTicks(1, rate));
        Assert.AreEqual(3600 * Fixture.T, TimelineTime.SampleToTicks(3600L * rate, rate));
        Assert.AreEqual(8L * rate, TimelineTime.SampleCount(8 * Fixture.T, rate));
    }
    [TestMethod]
    public void ExclusiveFrameCountMatchesRoundedFrameTicksEvenForUnusualRationals()
    {
        foreach (var fps in new[] { new FrameRate(30, 1), new FrameRate(30000, 1001), new FrameRate(73, 3) })
            for (long index = 1; index <= 300; index++)
            {
                long tick = TimelineTime.FrameToTicks(index, fps);
                foreach (long duration in new[] { tick - 1, tick, tick + 1 })
                {
                    long count = TimelineTime.FrameCount(duration, fps);
                    Assert.IsTrue(TimelineTime.FrameToTicks(count - 1, fps) < duration);
                    Assert.IsTrue(TimelineTime.FrameToTicks(count, fps) >= duration);
                }
            }
        Assert.AreEqual(0L, TimelineTime.FrameCount(0, new(30, 1)));
        Assert.AreEqual(1L, TimelineTime.FrameCount(1, new(30, 1)));
    }
    [TestMethod]
    public void RangesRejectOverflowAndUseExclusiveEnd()
    {
        Assert.IsFalse(TimelineTime.ValidRange(long.MaxValue, 1));
        Assert.IsFalse(TimelineTime.ValidRange(-1, 1));
        Assert.IsFalse(TimelineTime.ValidRange(0, 0));
        Assert.IsTrue(TimelineTime.ValidRange(0, long.MaxValue));
        Assert.IsTrue(TimelineTime.Contains(10, 5, 10));
        Assert.IsFalse(TimelineTime.Contains(10, 5, 15));
    }
}
