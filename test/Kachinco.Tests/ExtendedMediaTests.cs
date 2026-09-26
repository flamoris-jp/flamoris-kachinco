using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class ExtendedMediaTests
{
    [TestMethod]
    [DataRow("mov", MediaKind.Mov)] [DataRow("MP4", MediaKind.Mov)]
    [DataRow("wav", MediaKind.Wav)] [DataRow("MP3", MediaKind.Wav)] [DataRow("m4a", MediaKind.Wav)]
    public void SupportedReferencesUseExistingKindAndRoundTrip(string extension, MediaKind kind)
    {
        string path = "source." + extension;
        Assert.IsTrue(MediaSourceFormats.TryGetKind(path, out var actual));
        Assert.AreEqual(kind, actual);
        var f = new Fixture(); var asset = new MediaAsset(Fixture.Id(20), "source", path, kind, Fixture.T);
        Assert.IsTrue(f.Edit(new RegisterMedia(asset)).Success);
        string json = ProjectJson.Serialize(f.Project).Value!;
        var loaded = ProjectJson.Deserialize(json);
        Assert.IsTrue(loaded.Success, string.Join(";", loaded.Diagnostics));
        Assert.AreEqual(asset, loaded.Value!.Assets.Single(a => a.Id == asset.Id));
        Assert.IsTrue(f.Session.Undo().Success);
        Assert.IsFalse(f.Project.Assets.Any(a => a.Id == asset.Id));
        Assert.IsTrue(f.Session.Redo().Success);
        Assert.AreEqual(asset, f.Project.Assets.Single(a => a.Id == asset.Id));
    }

    [TestMethod]
    [DataRow("image.png")] [DataRow("movie.avi")] [DataRow("sound.flac")]
    [DataRow("movie.mp4.exe")] [DataRow("no-extension")] [DataRow("")]
    public void UnsupportedReferencesFailWithoutMutation(string path)
    {
        Assert.IsFalse(MediaSourceFormats.TryGetKind(path, out _));
        var f = new Fixture(); var before = f.Session.GetProject();
        Assert.IsFalse(f.Edit(new RegisterMedia(new(Fixture.Id(20), "bad", path, MediaKind.Mov, Fixture.T))).Success);
        Assert.AreEqual(before, f.Session.GetProject());
    }

    [TestMethod]
    public void ContainerAndFirstStreamValidationRejectSpoofedOrIncompleteMetadata()
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"mp3","sample_rate":"48000","channels":2}],"format":{"format_name":"mp3","duration":"1"}}""";
        Assert.AreEqual("MEDIA_KIND_MISMATCH", MediaProbeParser.Parse("fake.mp4", audio).Diagnostics[0].Code);
        Assert.AreEqual("MEDIA_CONTAINER_MISMATCH", MediaProbeParser.Parse("fake.wav", audio).Diagnostics[0].Code);
        Assert.AreEqual("MEDIA_CONTAINER_MISMATCH", MediaProbeParser.Parse("fake.m4a", audio).Diagnostics[0].Code);
        Assert.AreEqual("MEDIA_STREAM_UNSUPPORTED", MediaProbeParser.Parse("bad.mp3", audio.Replace("mp3\",\"sample_rate", "unknown\",\"sample_rate")).Diagnostics[0].Code);
        Assert.AreEqual("MEDIA_STREAM_UNSUPPORTED", MediaProbeParser.Parse("bad.mp3", audio.Replace("\"sample_rate\":\"48000\",", "")).Diagnostics[0].Code);
        Assert.AreEqual("MEDIA_PROBE_INVALID", MediaProbeParser.Parse("bad.mp4", "[]").Diagnostics[0].Code);
        const string laterValid = """{"streams":[{"codec_type":"video","codec_name":"h264"},{"codec_type":"video","codec_name":"h264","width":160,"height":90}],"format":{"format_name":"mov,mp4","duration":"1"}}""";
        Assert.AreEqual("MEDIA_STREAM_UNSUPPORTED", MediaProbeParser.Parse("bad.mp4", laterValid).Diagnostics[0].Code,
            "A later stream cannot supply metadata for the first stream the decoder selects.");
    }

    [TestMethod]
    [DataRow("mp3", "mp3")] [DataRow("m4a", "mov,mp4,m4a,3gp,3g2,mj2")]
    public void AudioWithCoverArtStillUsesFirstAudioStream(string extension, string format)
    {
        string json = $$$"""{"streams":[{"codec_type":"video","codec_name":"mjpeg","width":600,"height":600},{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"duration":"1.25"},{"codec_type":"audio","duration":"99"}],"format":{"format_name":"{{{format}}}","duration":"10"}}""";
        var result = MediaProbeParser.Parse("music." + extension, json);
        Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
        Assert.AreEqual(MediaKind.Wav, result.Value!.Kind);
        Assert.AreEqual(TimelineTime.SecondsToTicks(1.25m), result.Value.DurationTicks);
    }

    [TestMethod]
    [DataRow("mov", "mpeg4", true)] [DataRow("mp4", "mpeg4", true)]
    [DataRow("wav", "pcm_s16le", false)] [DataRow("mp3", "libmp3lame", false)] [DataRow("m4a", "aac", false)]
    public async Task RealFilesProbeDecodeImportPlaceAndRelink(string extension, string codec, bool video)
    {
        string directory = Path.Combine(Path.GetTempPath(), "kachinco-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string path = Path.Combine(directory, "source with spaces." + extension);
            await Generate(path, codec, video, timeout.Token);
            var probe = new FfprobeMediaProbe();
            var result = await probe.ProbeAsync(path, timeout.Token);
            Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
            Assert.AreEqual(video ? MediaKind.Mov : MediaKind.Wav, result.Value!.Kind);
            Assert.IsTrue(result.Value.DurationTicks > 0);
            var decoder = new FfmpegMediaDecoder();
            if (video) Assert.AreEqual(160 * 90 * 4, (await decoder.VideoAsync(path, 0, 160, 90, timeout.Token)).Length);
            else Assert.IsTrue((await decoder.AudioAsync(path, 0, 4800, 48000, 2, timeout.Token)).Any(sample => Math.Abs(sample) > 0.001));

            var f = new Fixture();
            var asset = result.Value.ToMediaAsset(Fixture.Id(20));
            var imported = f.Session.Execute(EditorStartup.Import(f.Session.GetProject(), asset, Guid.NewGuid(), "test"));
            Assert.IsTrue(imported.Success, string.Join(";", imported.Diagnostics));
            var placement = TimelineEditPlanner.PlaceOnNewTrack(f.Project, f.SequenceId, asset.Id,
                Fixture.Id(21), Fixture.Id(22), 0, f.Session.GetProject().Revision);
            Assert.IsTrue(placement.Success, string.Join(";", placement.Diagnostics));
            Assert.IsTrue(f.Session.Execute(placement.Value!).Success);
            var originalClip = f.Project.Sequences[0].Tracks.Single(t => t.Id == Fixture.Id(21)).Clips[0];
            var eval = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!.Evaluate(0).Value!;
            Assert.AreEqual(video, eval.VideoLayers.Any(layer => layer.MediaAssetId == asset.Id));
            Assert.AreEqual(!video, eval.Audio.Any(audio => audio.MediaAssetId == asset.Id));

            string replacement = Path.Combine(directory, video ? "replacement.mov" : "replacement.wav");
            await Generate(replacement, video ? "mpeg4" : "pcm_s16le", video, timeout.Token, "0.6");
            var prepared = await new MediaRelinkService(probe).PrepareAsync(f.Project, asset.Id, replacement, timeout.Token);
            Assert.IsTrue(prepared.Success, string.Join(";", prepared.Diagnostics));
            Assert.IsTrue(f.Edit(prepared.Value!).Success);
            var relinked = f.Project.Assets.Single(a => a.Id == asset.Id);
            Assert.AreEqual(replacement, relinked.SourcePath);
            Assert.AreEqual(originalClip, f.Project.Sequences[0].Tracks.Single(t => t.Id == Fixture.Id(21)).Clips[0]);
            var loaded = ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!);
            Assert.IsTrue(loaded.Success);
            Assert.AreEqual(relinked, loaded.Value!.Assets.Single(a => a.Id == asset.Id));
            Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual(path, f.Project.Assets.Single(a => a.Id == asset.Id).SourcePath);
            Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(replacement, f.Project.Assets.Single(a => a.Id == asset.Id).SourcePath);

            // The selected extension cannot hide incompatible real stream/container data.
            string disguised = Path.Combine(directory, video ? "disguised.wav" : "disguised.mp4");
            File.Copy(path, disguised);
            var rejected = await probe.ProbeAsync(disguised, timeout.Token);
            Assert.IsFalse(rejected.Success);
            Assert.IsTrue(rejected.Diagnostics.Any(d => d.Code is "MEDIA_KIND_MISMATCH" or "MEDIA_CONTAINER_MISMATCH"));
            string invalid = Path.Combine(directory, "invalid." + extension);
            await File.WriteAllTextAsync(invalid, "not a media file", timeout.Token);
            Assert.IsFalse((await probe.ProbeAsync(invalid, timeout.Token)).Success);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Generate(string path, string codec, bool video, CancellationToken token, string duration = "0.3")
    {
        using var process = new Process { StartInfo = new("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true } };
        string[] args = video
            ? ["-v", "error", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", duration, "-c:v", codec, "-c:a", "aac", path]
            : ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", duration, "-c:a", codec, path];
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { Assert.Inconclusive("FFmpeg must be installed for real-media acceptance."); return; }
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.AreEqual(0, process.ExitCode, await stderr);
    }
}
