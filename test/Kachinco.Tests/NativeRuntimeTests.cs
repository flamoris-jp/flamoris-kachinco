using System.Runtime.InteropServices;
using Kachinco.Core;
using Kachinco.Tests.Oracles;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class NativeRuntimeTests
{
    [TestMethod]
    public void VersionLayoutAndTransportPreserveExactValues()
    {
        using var runtime = NativeRuntime.Create();
        var info = runtime.GetInfo();
        Assert.AreEqual(NativeRuntime.AbiVersion, info.AbiVersion);
        Assert.AreEqual(TimelineTime.TicksPerSecond, info.TicksPerSecond);
        Assert.AreEqual(24, Marshal.SizeOf<NativeRuntimeInfo>());
        Assert.AreEqual(32, Marshal.SizeOf<NativeMediaValue>());
        Assert.AreEqual((IntPtr)16, Marshal.OffsetOf<NativeMediaValue>(nameof(NativeMediaValue.Width)));
        var value = new NativeMediaValue { DurationTicks = 9_007_199_254_740_993, SourceInTicks = 35_280_000,
            Width = 1080, Height = 1920, FpsNumerator = 30000, FpsDenominator = 1001 };
        Assert.AreEqual(value, runtime.RoundTrip(value));
    }

    // Expected values come from the frozen BigInteger implementation, never the native adapter.
    private static void Parity(Func<long> product, Func<long> native)
    {
        long expected;
        try { expected = product(); }
        catch (OverflowException)
        {
            Assert.AreEqual(NativeStatus.Overflow, Assert.ThrowsExactly<NativeRuntimeException>(() => native()).Status);
            return;
        }
        catch (ArgumentOutOfRangeException)
        {
            Assert.AreEqual(NativeStatus.InvalidArgument, Assert.ThrowsExactly<NativeRuntimeException>(() => native()).Status);
            return;
        }
        Assert.AreEqual(expected, native());
    }

    [TestMethod]
    public void CanonicalTimeMatchesFrozenOracleAtBoundariesAndSeededInputs()
    {
        using var runtime = NativeRuntime.Create();
        FrameRate[] rates = [new(1, 1), new(24, 1), new(30, 1), new(240, 1), new(24000, 1001),
            new(30000, 1001), new(60000, 1001), new(73, 3), new(int.MaxValue, int.MaxValue - 1),
            new(60, 2), new(0, 1), new(1, 0), new(241, 1), new(1, 2), new(-1, 1)];
        long[] boundaries = [-1, 0, 1, 2, 1_176_000, 35_280_000, 9_007_199_254_740_993, long.MaxValue];
        var random = new Random(31032);
        var values = boundaries.Concat(Enumerable.Range(0, 200).Select(_ => random.NextInt64(0, long.MaxValue))).ToArray();
        foreach (var fps in rates)
        {
            Assert.AreEqual(new OracleFrameRate(fps.Numerator, fps.Denominator).IsValid, fps.IsValid);
            foreach (long value in values)
            {
                Parity(() => ManagedTimelineTimeOracle.FrameToTicks(value, new OracleFrameRate(fps.Numerator, fps.Denominator)), () => runtime.FrameToTicks(value, fps.Numerator, fps.Denominator));
                Parity(() => ManagedTimelineTimeOracle.FrameCount(value, new OracleFrameRate(fps.Numerator, fps.Denominator)), () => runtime.FrameCount(value, fps.Numerator, fps.Denominator));
            }
        }
        foreach (int rate in new[] { -1, 0, 1, 44100, 48000, 35280000, 70560000, int.MaxValue })
            foreach (long value in values)
            {
                Parity(() => ManagedTimelineTimeOracle.SampleToTicks(value, rate), () => runtime.SampleToTicks(value, rate));
                Parity(() => ManagedTimelineTimeOracle.SampleCount(value, rate), () => runtime.SampleCount(value, rate));
            }
    }

    [TestMethod]
    public void HalfOpenFrameBoundariesMatchProduct()
    {
        using var runtime = NativeRuntime.Create();
        foreach (var fps in new[] { new FrameRate(30, 1), new FrameRate(30000, 1001), new FrameRate(73, 3) })
            for (long index = 1; index <= 300; ++index)
                foreach (long duration in new[] { ManagedTimelineTimeOracle.FrameToTicks(index, new OracleFrameRate(fps.Numerator, fps.Denominator)) - 1,
                    ManagedTimelineTimeOracle.FrameToTicks(index, new OracleFrameRate(fps.Numerator, fps.Denominator)), ManagedTimelineTimeOracle.FrameToTicks(index, new OracleFrameRate(fps.Numerator, fps.Denominator)) + 1 })
                    Parity(() => ManagedTimelineTimeOracle.FrameCount(duration, new OracleFrameRate(fps.Numerator, fps.Denominator)), () => runtime.FrameCount(duration, fps.Numerator, fps.Denominator));
    }

    [TestMethod]
    public void ManagedFacadePreservesCanonicalValuesAndExceptionTypes()
    {
        static (long Value, Type? Error) Outcome(Func<long> call)
        { try { return (call(), null); } catch (Exception ex) { return (0, ex.GetType()); } }
        foreach (var fps in new[] { new FrameRate(30, 1), new FrameRate(30000, 1001), new FrameRate(60, 2), new FrameRate(0, 1) })
            foreach (long v in new[] { -1L, 0, 1, 588000, 35280000, long.MaxValue })
            {
                var old = new OracleFrameRate(fps.Numerator, fps.Denominator);
                Assert.AreEqual(Outcome(() => ManagedTimelineTimeOracle.FrameToTicks(v, old)), Outcome(() => TimelineTime.FrameToTicks(v, fps)));
                Assert.AreEqual(Outcome(() => ManagedTimelineTimeOracle.FrameCount(v, old)), Outcome(() => TimelineTime.FrameCount(v, fps)));
                Assert.AreEqual(Outcome(() => {
                    if (v < 0 || !old.IsValid) throw new ArgumentOutOfRangeException();
                    return ManagedTimelineTimeOracle.RoundHalfUp((System.Numerics.BigInteger)v * old.Numerator,
                        (System.Numerics.BigInteger)ManagedTimelineTimeOracle.TicksPerSecond * old.Denominator);
                }), Outcome(() => TimelineTime.TicksToFrame(v, fps)));
                foreach (int rate in new[] { 0, 44100, 48000, int.MaxValue })
                {
                    Assert.AreEqual(Outcome(() => ManagedTimelineTimeOracle.SampleToTicks(v, rate)), Outcome(() => TimelineTime.SampleToTicks(v, rate)));
                    Assert.AreEqual(Outcome(() => ManagedTimelineTimeOracle.SampleCount(v, rate)), Outcome(() => TimelineTime.SampleCount(v, rate)));
                }
            }
    }

    [TestMethod]
    public void RepeatedDisposeAndConcurrentCallsHaveSafeLifetime()
    {
        for (int i = 0; i < 1000; ++i)
        {
            var runtime = NativeRuntime.Create();
            Assert.AreEqual(35280000L, runtime.SampleToTicks(48000, 48000));
            runtime.Dispose(); runtime.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => runtime.GetInfo());
        }
        for (int i = 0; i < 100; ++i)
        {
            using var runtime = NativeRuntime.Create();
            Parallel.Invoke(() => { for (int j = 0; j < 100; ++j)
                try { Assert.AreEqual(35280000L, runtime.FrameToTicks(30, 30, 1)); }
                catch (ObjectDisposedException) { break; } }, runtime.Dispose);
        }
    }

    [TestMethod]
    public void ErrorsAreStableAndDoNotPoisonTheNextCall()
    {
        using var runtime = NativeRuntime.Create();
        var error = Assert.ThrowsExactly<NativeRuntimeException>(() => runtime.FrameToTicks(-1, 30, 1));
        Assert.AreEqual("NATIVE_INVALID_ARGUMENT", error.Message);
        Assert.AreEqual("NATIVE_ABI_MISMATCH", NativeRuntime.StatusMessage(NativeStatus.AbiMismatch));
        Assert.AreEqual("NATIVE_UNKNOWN_STATUS", NativeRuntime.StatusMessage((NativeStatus)999));
        Assert.AreEqual(35280000L, runtime.FrameToTicks(30, 30, 1));
    }
}
