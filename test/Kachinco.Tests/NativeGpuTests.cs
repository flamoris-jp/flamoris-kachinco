using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class NativeGpuTests
{
    [TestMethod]
    public void OptionalAdapterHasDefinedHostSupportAndRejectsInvalidBudget()
    {
        Assert.IsFalse(NativeGpuCompositor.TryCreate(out var invalid, out var invalidReason, 0));
        Assert.IsNull(invalid);
        StringAssert.Contains(invalidReason, "budget");
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsFalse(NativeGpuCompositor.TryCreate(out var unavailable, out var reason));
            Assert.IsNull(unavailable);
            StringAssert.Contains(reason, "unavailable");
        }
    }

    [TestMethod]
    public void WindowsWarpInteropPreservesBytesAndDisposesSafely()
    {
        if (!OperatingSystem.IsWindows()) return; // Native Linux unsupported contract is tested above.
        Assert.IsTrue(NativeGpuCompositor.TryCreate(out var compositor, out var reason, 4096, forceWarp: true), reason);
        using var gpu = compositor!;
        Assert.ThrowsExactly<InvalidOperationException>(() => gpu.Read());
        gpu.Begin(2, 1);
        gpu.Composite([255, 0, 0, 255, 0, 200, 17, 255], new(0, 0, 1, 1, 0, 1, 0));
        CollectionAssert.AreEqual(new byte[] { 255, 0, 0, 255, 0, 200, 17, 255 }, gpu.Read());
        Assert.AreEqual(112UL, gpu.AllocatedBytes);
        Assert.ThrowsExactly<InvalidDataException>(() => gpu.Composite(new byte[4], new(0, 0, 1, 1, 0, 1, 0)));
        gpu.Reset();
        Assert.AreEqual(80UL, gpu.AllocatedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => gpu.Read());
        gpu.Begin(1, 1);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 255 }, gpu.Read());
        gpu.Dispose(); gpu.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => gpu.Begin(1, 1));
    }
}
