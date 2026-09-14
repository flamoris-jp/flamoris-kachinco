using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class PreviewCacheTests
{
    [TestMethod]
    public void LruHasHardByteAndEntryBoundsAndRefreshesRecency()
    {
        var cache = new PreviewCache<string>(10, 2);
        cache.Put("a", "A", 5); cache.Put("b", "B", 5); Assert.IsTrue(cache.TryGet("a", out _));
        cache.Put("c", "C", 5); Assert.IsFalse(cache.TryGet("b", out _)); Assert.IsTrue(cache.TryGet("a", out _));
        cache.Put("big", "oversize", 11); Assert.AreEqual(10L, cache.Statistics.Bytes);
        for (int i = 0; i < 10000; i++) cache.Put(i.ToString(), "", 0);
        Assert.IsTrue(cache.Statistics.Entries <= 2); Assert.IsTrue(cache.Statistics.Bytes <= 10);
        cache.Clear(); Assert.AreEqual(0L, cache.Statistics.Bytes);
    }
    [TestMethod]
    public void AudioEditPreservesVideoAndUnaffectedAudioRanges()
    {
        var f = new Fixture(); var a = InteractivePreviewTests.Context(f);
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, f.AudioClipId, true, ClipAppearance.Default, new(.5, false))).Success);
        var b = InteractivePreviewTests.Context(f);
        Assert.AreEqual(a.VideoKey(2 * Fixture.T, PreviewQuality.Full), b.VideoKey(2 * Fixture.T, PreviewQuality.Full));
        Assert.AreEqual(a.AudioKey(0, 4800), b.AudioKey(0, 4800));
        Assert.AreNotEqual(a.AudioKey(2 * 48000, 4800), b.AudioKey(2 * 48000, 4800));
        Assert.AreNotEqual(a.VideoKey(0, PreviewQuality.Full), a.VideoKey(0, PreviewQuality.Quarter));
        Assert.AreEqual(a.VideoKey(0, PreviewQuality.Half), InteractivePreviewTests.Context(f).VideoKey(0, PreviewQuality.Half));
    }
    [TestMethod]
    public void LocalVideoEditRelinkAndSourceReplacementInvalidateDependencies()
    {
        var f = new Fixture(); var a = InteractivePreviewTests.Context(f);
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, Fixture.T, 4 * Fixture.T)).Success);
        var b = InteractivePreviewTests.Context(f);
        Assert.AreEqual(a.VideoKey(0, PreviewQuality.Half), b.VideoKey(0, PreviewQuality.Half));
        Assert.AreNotEqual(a.VideoKey(5 * Fixture.T, PreviewQuality.Half), b.VideoKey(5 * Fixture.T, PreviewQuality.Half));
        Assert.IsTrue(f.Edit(new RelinkMedia(f.MovId, "other.mov", 10 * Fixture.T)).Success);
        var c = InteractivePreviewTests.Context(f);
        Assert.AreNotEqual(b.VideoKey(0, PreviewQuality.Half), c.VideoKey(0, PreviewQuality.Half));
        Assert.AreEqual(b.AudioKey(48000, 4800), c.AudioKey(48000, 4800));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mov");
        try
        {
            File.WriteAllText(path, "original"); Assert.IsTrue(f.Edit(new RelinkMedia(f.MovId, path, 10 * Fixture.T)).Success);
            var before = InteractivePreviewTests.Context(f); string key = before.VideoKey(0, PreviewQuality.Half);
            File.WriteAllText(path, "regenerated longer source"); var after = InteractivePreviewTests.Context(f);
            Assert.AreNotEqual(key, after.VideoKey(0, PreviewQuality.Half));
            Assert.AreNotEqual(before.WindowKey(0, Fixture.T), after.WindowKey(0, Fixture.T));
        }
        finally { File.Delete(path); }
    }
}
