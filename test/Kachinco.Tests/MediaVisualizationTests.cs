using System.Collections.Immutable;
using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class MediaVisualizationTests
{
    [TestMethod]
    public void PeaksPreserveSilencePolarityAndSourceTrimAlignment()
    {
        var accumulator = new WaveformAccumulator(8, 4);
        foreach (float sample in new float[] {0,0,-.25f,.1f,0,0,1,-.5f}) accumulator.Add(sample);
        var peaks = accumulator.Complete();
        CollectionAssert.AreEqual(new float[] {0,.25f,0,1}, peaks.ToArray());
        CollectionAssert.AreEqual(new float[] {0,1}, WaveformProjection.Crop(peaks, 8 * Fixture.T, 4 * Fixture.T, 4 * Fixture.T, 2).ToArray());
        Assert.AreEqual(1f, WaveformProjection.Crop(peaks, 8 * Fixture.T, 0, 8 * Fixture.T, 1)[0]);
        Assert.ThrowsExactly<InvalidDataException>(() => accumulator.Add(0));
        Assert.ThrowsExactly<InvalidDataException>(() => new WaveformAccumulator(1,1).Add(float.NaN));
    }
    [TestMethod]
    public async Task RealMovPosterAndWavPeaksAreRecognizableWithoutMutatingProject()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kachinco-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mov = Path.Combine(directory, "red.mov"), wav = Path.Combine(directory, "tone.wav");
            await Ffmpeg(["-v","error","-f","lavfi","-i","color=c=red:s=32x32:r=30:d=0.2","-c:v","qtrle",mov]);
            await Ffmpeg(["-v","error","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=0.2","-c:a","pcm_s16le",wav]);
            var probe = new FfprobeMediaProbe();
            var video = (await probe.ProbeAsync(mov)).Value!.ToMediaAsset(Guid.NewGuid());
            var audio = (await probe.ProbeAsync(wav)).Value!.ToMediaAsset(Guid.NewGuid());
            var session = new EditorSession();
            Assert.IsTrue(session.Execute(EditorStartup.Import(session.GetProject(), video, Guid.NewGuid(), "fixture")).Success);
            Assert.IsTrue(session.Execute(EditorStartup.Import(session.GetProject(), audio, Guid.NewGuid(), "fixture")).Success);
            string before = ProjectJson.Serialize(session.GetProject().Project!).Value!;
            var service = new MediaVisualizationService();
            var poster = await service.GenerateAsync(video, mov, default);
            int center = (poster.Height / 2 * poster.Width + poster.Width / 2) * 4;
            Assert.IsTrue(poster.Rgba[center] > 200 && poster.Rgba[center + 1] < 40);
            Assert.AreEqual(160 * 90 * 4, poster.Rgba.Length);
            var waveform = await service.GenerateAsync(audio, wav, default);
            Assert.AreEqual(2048, waveform.Peaks.Length);
            Assert.IsTrue(waveform.Peaks.Max() > .1f && waveform.Peaks.Max() < .2f);
            Assert.AreEqual(before, ProjectJson.Serialize(session.GetProject().Project!).Value);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.GenerateAsync(video, mov, cancelled.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GenerateAsync(audio, mov, default));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task Ffmpeg(string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
    }
}
