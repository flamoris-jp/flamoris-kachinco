using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class NativeMediaTests
{
    [TestMethod]
    public void NativeDecodedBuffersRejectPartialAndNonFiniteSamplesAndPadTail()
    {
        byte[] rgba = [1, 2, 3, 4];
        CollectionAssert.AreEqual(rgba, NativeDecodedMedia.Rgba(rgba, 1, 1));
        Assert.ThrowsExactly<EndOfStreamException>(() => NativeDecodedMedia.Rgba([], 1, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => NativeDecodedMedia.Rgba([1], 1, 1));
        byte[] stereo = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(stereo, .5f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(stereo.AsSpan(4), -.25f);
        CollectionAssert.AreEqual(new[] { .5f, -.25f, 0f, 0f }, NativeDecodedMedia.Pcm(stereo, 2, 2));
        Assert.ThrowsExactly<InvalidDataException>(() => NativeDecodedMedia.Pcm([0, 0, 0, 0], 1, 2));
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(stereo, float.NaN);
        Assert.ThrowsExactly<InvalidDataException>(() => NativeDecodedMedia.Pcm(stereo, 1, 2));
    }
    [TestMethod]
    public void NativeCacheMatchesExistingLruIncludingReplacementAndZeroSize()
    {
        var product = new PresentationObjectCache<byte[]>(10, 2);
        using var native = new NativeByteCache(10, 2);
        var random = new Random(31033);
        for (int i = 0; i < 10000; ++i)
        {
            string key = random.Next(5).ToString();
            if (i % 3 == 0)
            {
                Assert.AreEqual(product.TryGet(key, out var expected), native.TryGet(key, out var actual));
                if (expected is not null) CollectionAssert.AreEqual(expected, actual);
            }
            else
            {
                var bytes = new byte[random.Next(14)]; random.NextBytes(bytes);
                product.Put(key, bytes, bytes.Length); native.Put(key, bytes, bytes.Length);
            }
            if (i % 31 == 0) { product.Clear(); native.Clear(); }
            var a = product.Statistics; var b = native.Statistics;
            Assert.AreEqual(a.Bytes, b.Bytes); Assert.AreEqual((long)a.Entries, b.Entries);
            Assert.AreEqual(a.Hits, b.Hits); Assert.AreEqual(a.Misses, b.Misses); Assert.AreEqual(a.Evictions, b.Evictions);
        }
    }
    [TestMethod]
    public async Task NativeProcessPreservesUnicodeQuotedArgumentsAndBothPipes()
    {
        string python = OperatingSystem.IsWindows() ? "python" : "python3";
        string argument = "愛乃's space \"quoted\" \\ ending\\";
        using var process = NativeMediaProcess.Start(python,
            ["-c", "import sys;sys.stdout.buffer.write(sys.argv[1].encode('utf-8'));sys.stderr.write('stderr');sys.exit(37)", argument]);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        Assert.AreEqual(argument, await stdout); Assert.AreEqual("stderr", await stderr); Assert.AreEqual(37, process.ExitCode);
    }
    [TestMethod]
    public async Task NativeCancellationUnblocksReaderAndProcessFailureDoesNotReturnAHandle()
    {
        Assert.ThrowsExactly<Win32Exception>(() => NativeMediaProcess.Start("missing-kachinco-31033", []));
        Assert.ThrowsExactly<ArgumentException>(() => NativeMediaProcess.Start("python", ["bad\0argument"]));
        string python = OperatingSystem.IsWindows() ? "python" : "python3";
        using var process = NativeMediaProcess.Start(python, ["-c", "import time;time.sleep(60)"]);
        var reading = Task.Run(() => process.StandardOutput.ReadToEnd());
        await Task.Delay(30); var at = Stopwatch.StartNew(); process.Kill();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(deadline.Token);
        Assert.IsTrue(at.Elapsed < TimeSpan.FromSeconds(5));
    }
    [TestMethod]
    public async Task NativePipesDrainBeyondOsCapacityWithoutDeadlock()
    {
        string python = OperatingSystem.IsWindows() ? "python" : "python3";
        using var process = NativeMediaProcess.Start(python,
            ["-c", "import sys;sys.stdout.write('o'*1048576);sys.stderr.write('e'*1048576)"]);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        Assert.AreEqual(1048576, (await stdout).Length); Assert.AreEqual(1048576, (await stderr).Length);
    }
    [TestMethod]
    public async Task BoundedOutputBlocksDrainPacketsPreserveShortEofAndCancelTheOwnedWriter()
    {
        string python = OperatingSystem.IsWindows() ? "python" : "python3";
        using (var process = NativeMediaProcess.Start(python,
            ["-c", "import sys;sys.stdout.buffer.write(b'o'*1048576);sys.stderr.write('e'*1048576)"]))
        {
            var stderr = process.StandardError.ReadToEndAsync();
            var bytes = new byte[1048584];
            int read = await process.ReadOutputBlockAsync(bytes).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1048576, read); Assert.IsTrue(bytes.AsSpan(0, read).ToArray().All(b => b == (byte)'o'));
            Assert.AreEqual(0, bytes[^1]); Assert.AreEqual(1048576, (await stderr).Length);
            await process.WaitForExitAsync(); Assert.AreEqual(0, process.ExitCode);
        }
        using (var process = NativeMediaProcess.Start(python, ["-c", "import time;time.sleep(60)"]))
        {
            using var cancellation = new CancellationTokenSource();
            var blocked = process.ReadOutputBlockAsync(new byte[8], cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await blocked.WaitAsync(TimeSpan.FromSeconds(5)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
        }
    }
}
