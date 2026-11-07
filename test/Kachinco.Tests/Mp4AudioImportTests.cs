using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class Mp4AudioImportTests
{
    [TestMethod]
    public async Task RealMp4ExtractsIndependentWavAndPreservesCommittedBytesForRedo()
    {
        string root = Path.Combine(Path.GetTempPath(), "kachinco-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "日本語 source.mp4");
            await FixtureMp4(source, true);
            var original = await File.ReadAllBytesAsync(source);
            var probe = new FfprobeMediaProbe();
            var video = await probe.ProbeAsync(source);
            Assert.IsTrue(video.Success);
            var importer = new Mp4AudioImport(Path.Combine(root, "audio"), probe);
            using var prepared = await importer.PrepareAsync(video.Value!);
            Assert.IsNotNull(prepared);
            Assert.AreEqual(MediaKind.Wav, prepared.Asset.Kind);
            Assert.AreEqual(48000, prepared.Asset.SampleRate);
            Assert.AreEqual(2, prepared.Asset.Channels);
            Assert.IsTrue(prepared.Asset.DurationTicks > video.Value!.DurationTicks, "Audio tail must not be truncated to the video duration.");
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(source));
            using var decoder = new FfmpegMediaDecoder();
            var pcm = await decoder.AudioAsync(prepared.Asset.SourcePath, 0, 4800, 48000, 2, default);
            Assert.IsTrue(pcm.Any(s => Math.Abs(s) > .01));
            using var session = new EditorSession();
            var batch = EditorStartup.Import(session.GetProject(), video.Value!.ToMediaAsset(Guid.NewGuid()), Guid.NewGuid(), "MP4");
            Assert.IsTrue(session.Execute(batch with { Commands = batch.Commands.Add(new RegisterMedia(prepared.Asset)) }).Success);
            prepared.Keep();
            Assert.AreEqual(2, session.GetProject().Project!.Assets.Length);
            var saved = ProjectJson.Serialize(session.GetProject().Project!).Value;
            Assert.IsTrue(ProjectJson.Deserialize(saved!).Success);
            Assert.IsTrue(session.Undo().Success);
            Assert.IsTrue(File.Exists(prepared.Asset.SourcePath));
            Assert.IsTrue(session.Redo().Success);
            Assert.AreEqual(saved, ProjectJson.Serialize(session.GetProject().Project!).Value);
            // Each preparation owns unique bytes; disposing an uncommitted one cannot affect the first.
            var discarded = await importer.PrepareAsync(video.Value!);
            Assert.IsNotNull(discarded);
            Assert.AreNotEqual(prepared.Asset.SourcePath, discarded.Asset.SourcePath);
            discarded.Dispose();
            Assert.IsFalse(File.Exists(discarded.Asset.SourcePath));
            Assert.IsTrue(File.Exists(prepared.Asset.SourcePath));
            var failing = new Mp4AudioImport(Path.Combine(root, "failed"), probe, "kachinco-missing-ffmpeg");
            await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => failing.PrepareAsync(video.Value!));
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(root, "failed")).Length);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => importer.PrepareAsync(video.Value!, cancelled.Token));
            await File.WriteAllTextAsync(source, "corrupt MP4 after probe");
            var corrupt = new Mp4AudioImport(Path.Combine(root, "corrupt"), probe);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => corrupt.PrepareAsync(video.Value!));
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(root, "corrupt")).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SilentMp4DoesNotCreateAudioFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "kachinco-silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "silent.mp4");
            await FixtureMp4(source, false);
            var probe = new FfprobeMediaProbe();
            var video = await probe.ProbeAsync(source);
            Assert.IsTrue(video.Success);
            Assert.IsNull(await new Mp4AudioImport(Path.Combine(root, "audio"), probe).PrepareAsync(video.Value!));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "audio")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task FixtureMp4(string path, bool audio)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        var args = new List<string> { "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=blue:s=32x32:r=24:d=1" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.25", "-ac", "2", "-c:a", "aac"]);
        args.AddRange(["-c:v", "mpeg4", "-y", path]);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
    }
}
