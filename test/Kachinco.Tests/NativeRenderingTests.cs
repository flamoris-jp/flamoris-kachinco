using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kachinco.Tests;

[TestClass]
public sealed class NativeRenderingTests
{
    [TestMethod]
    public void SeededPixelsTransformsAndBlendModesMatchFrozenManagedOracle()
    {
        var random = new Random(34001);
        for (int iteration = 0; iteration < 400; ++iteration)
        {
            int width = random.Next(1, 90), height = random.Next(1, 70);
            var source = new byte[width * height * 4]; var backdrop = new byte[source.Length];
            random.NextBytes(source); random.NextBytes(backdrop);
            if (iteration % 5 == 0) for (int i = 3; i < source.Length; i += 4) source[i] = 255;
            if (iteration % 7 == 0) for (int i = 3; i < source.Length; i += 4) source[i] = 0;
            var appearance = iteration % 3 == 0 ? ClipAppearance.Default : new ClipAppearance(
                new(random.NextDouble()*40-20, random.NextDouble()*40-20, .1+random.NextDouble()*3,
                    .1+random.NextDouble()*3, random.NextDouble()*720-360), random.NextDouble(), (BlendMode)(iteration%2));
            var expected = backdrop.ToArray(); var actual = backdrop.ToArray(); var pixels = source.ToImmutableArray();
            ManagedRenderOracle.Composite(expected, pixels, width, height, appearance);
            SharedFrameRenderer.Composite(actual, pixels, width, height, appearance);
            CollectionAssert.AreEqual(expected, actual, $"Raster case {iteration}");
        }
    }
    [TestMethod]
    public void SeededScalarBlendAndPcmSummationPreserveRoundingAndFinalClipping()
    {
        var random = new Random(34002);
        for (int n = 0; n < 1000; ++n)
        {
            Rgba Next() => new(random.NextDouble(), random.NextDouble(), random.NextDouble(), random.NextDouble());
            var back = Next(); var front = Next(); double opacity = random.NextDouble(); var mode = (BlendMode)(n%2);
            var expected = ManagedRenderOracle.Blend(back, front, mode, opacity);
            var actual = BlendReference.Composite(back, front, mode, opacity);
            Assert.AreEqual(expected, actual);
        }
        var reference = new double[9600]; var mix = new double[reference.Length];
        for (int n=0; n<30; ++n)
        {
            int count = random.Next(1, 4000), offset = random.Next(reference.Length-count); double gain=random.NextDouble()*16;
            var samples = Enumerable.Range(0,count).Select(_ => (float)(random.NextDouble()*2-1)).ToArray();
            for (int i=0;i<count;++i) reference[offset+i]+=samples[i]*gain;
            NativeComposition.Mix(mix,samples,offset,gain);
        }
        CollectionAssert.AreEqual(reference, mix);
        CollectionAssert.AreEqual(reference.Select(x=>(float)Math.Clamp(x,-1d,1d)).ToArray(), NativeComposition.Finish(mix));
        Assert.ThrowsExactly<IOException>(()=>NativeComposition.Mix(mix,new[]{float.NaN},0,1));
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>SharedFrameRenderer.Composite(new byte[4],[0,0,0,255],1,1,ClipAppearance.Default,cancellation.Token));
    }
}
