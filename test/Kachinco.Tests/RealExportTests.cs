using System.Diagnostics;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class RealExportTests
{
    [TestMethod]
    public async Task RealMovWavExportBothPresetsAndProbeFrameCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kachinco-export-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var mov = Path.Combine(dir, "source.mov"); var wav = Path.Combine(dir, "source.wav");
            await Run("ffmpeg", ["-v","error","-f","lavfi","-i","color=c=red:s=32x32:r=30:d=0.2","-c:v","qtrle",mov]);
            await Run("ffmpeg", ["-v","error","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=0.2","-c:a","pcm_s16le",wav]);
            foreach (var settings in new[] { SequenceSettings.Landscape, SequenceSettings.Portrait })
            {
                var f = new Fixture();
                Assert.IsTrue(f.Edit(new DeleteCaption(f.SequenceId, f.CaptionId),
                    new TrimClip(f.SequenceId, f.ClipId, 0, 0, Fixture.T / 10),
                    new TrimClip(f.SequenceId, f.AudioClipId, 0, 0, Fixture.T / 10),
                    new RelinkMedia(f.MovId, mov, Fixture.T / 5), new RelinkMedia(f.WavId, wav, Fixture.T / 5, 48000, 1),
                    new SetSequenceDuration(f.SequenceId, Fixture.T / 10)).Success);
                // Test fixture setup uses a validated replacement snapshot; production edits remain commands.
                var project = f.Project with { Sequences = [f.Project.Sequences[0] with { Settings = settings }] };
                Assert.IsTrue(f.Session.ReplaceProject(project).Success);
                var decoder = new FfmpegMediaDecoder();
                var renderer = new SharedFrameRenderer(decoder);
                var evaluated = TimelineEvaluator.Create(project, f.SequenceId).Value!.Evaluate(0).Value!;
                var preview = await renderer.RenderAsync(project, evaluated, 0, default);
                Assert.IsTrue(preview.Success);
                int center = ((settings.Height / 2) * settings.Width + settings.Width / 2) * 4;
                Assert.IsTrue(preview.Value!.Rgba8[center] > 200);
                var output = Path.Combine(dir, settings.Width + ".mp4");
                var result = await new SnapshotExportService(renderer, new SharedAudioRenderer(decoder), new FfmpegEncodingBackend(), new(null))
                    .ExportAsync(f.Session.GetProject(), new(Guid.NewGuid(), f.SequenceId, output, ExportPreset.YoutubeH264AacMp4), null, default);
                Assert.AreEqual(ExportStage.Completed, result.Stage, string.Join(";", result.Diagnostics.Select(d => d.Message)));
                var probe = await Run("ffprobe", ["-v","error","-count_frames","-show_entries","stream=codec_name,width,height,nb_read_frames,sample_rate,channels","-of","json",output]);
                using var doc = JsonDocument.Parse(probe);
                var streams = doc.RootElement.GetProperty("streams");
                Assert.AreEqual("h264", streams[0].GetProperty("codec_name").GetString());
                Assert.AreEqual(settings.Width, streams[0].GetProperty("width").GetInt32());
                Assert.AreEqual("3", streams[0].GetProperty("nb_read_frames").GetString());
                Assert.AreEqual("aac", streams[1].GetProperty("codec_name").GetString());
                Assert.AreEqual("48000", streams[1].GetProperty("sample_rate").GetString());
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task CancellationPreservesExistingOutputAndRemovesTemporaryMedia()
    {
        var folder=Path.Combine(Path.GetTempPath(),"kachinco-cancel-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var output=Path.Combine(folder,"existing.mp4");await File.WriteAllTextAsync(output,"keep-existing");
            using var token=new CancellationTokenSource();
            var request=new EncodingRequest(Guid.NewGuid(),output,ExportPreset.YoutubeH264AacMp4,SequenceSettings.Landscape,2,48000,2,3200);
            var result=await new FfmpegEncodingBackend().EncodeAsync(request,new OneFrameSource(),new(null),new CancelOnFrame(token),token.Token);
            Assert.AreEqual(ExportStage.Cancelled,result.Stage);
            Assert.AreEqual("keep-existing",await File.ReadAllTextAsync(output));
            Assert.AreEqual(1,Directory.GetFileSystemEntries(folder).Length);
        }
        finally { Directory.Delete(folder,true); }
    }
    [TestMethod]
    public async Task InvalidRenderedSequenceBecomesEncodingFailureAndCleansTemporaryMedia()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kachinco-invalid-encode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var request = new EncodingRequest(Guid.NewGuid(), Path.Combine(folder, "output.mp4"), ExportPreset.YoutubeH264AacMp4,
                SequenceSettings.Landscape, 1, 48000, 2, 1600);
            var result = await new FfmpegEncodingBackend().EncodeAsync(request, new InvalidFrameSource(), new(null), null, default);
            Assert.AreEqual(ExportStage.Failed, result.Stage);
            Assert.AreEqual("ENCODING_FAILED", result.Diagnostics[0].Code);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(folder).Length);
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class CancelOnFrame(CancellationTokenSource token) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) { if(value.FramesCompleted>0) token.Cancel(); } }
    private sealed class OneFrameSource : IRenderedMediaSource
    {
        public async IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var bytes=new byte[1920*1080*4];
            yield return new(0,0,1920,1080,System.Collections.Immutable.ImmutableArray.CreateRange(bytes));
            await Task.Yield();cancellationToken.ThrowIfCancellationRequested();
        }
        public IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync(CancellationToken cancellationToken) => throw new AssertFailedException("Cancelled export must not read audio.");
    }
    private sealed class InvalidFrameSource : IRenderedMediaSource
    {
        public async IAsyncEnumerable<RenderedVideoFrame> ReadVideoAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new(0, 0, 1920, 1080, System.Collections.Immutable.ImmutableArray.Create<byte>(0));
            await Task.Yield();
        }
        public IAsyncEnumerable<RenderedAudioBlock> ReadAudioAsync(CancellationToken cancellationToken) =>
            throw new AssertFailedException("Invalid video must stop before audio rendering.");
    }

    private static async Task<string> Run(string executable, string[] args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } finally { if (!process.HasExited) process.Kill(true); }
        Assert.AreEqual(0, process.ExitCode, await stderr);
        return await stdout;
    }
}
