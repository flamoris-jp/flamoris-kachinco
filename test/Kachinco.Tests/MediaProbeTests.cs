using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class MediaProbeTests
{
    [TestMethod]
    [DataRow("mov")]
    [DataRow("mp4")]
    public void MovMetadataUsesCanonicalTicksAndKeepsTechnicalProbeDataTransient(string extension)
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"30000/1001","duration":"1.25"},{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"duration":"1.25"}],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"1.25"}}
            """;
        var result = MediaProbeParser.Parse(Path.GetFullPath("shot." + extension), json);
        Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
        Assert.AreEqual(TimelineTime.SecondsToTicks(1.25m), result.Value!.DurationTicks);
        Assert.AreEqual(MediaKind.Mov, result.Value.Kind);
        Assert.AreEqual(1920, result.Value.Width); Assert.AreEqual(1080, result.Value.Height);
        Assert.AreEqual(new FrameRate(30000, 1001), result.Value.FrameRate);
        Assert.AreEqual(48000, result.Value.SampleRate); Assert.AreEqual(2, result.Value.Channels);
        var asset = result.Value.ToMediaAsset(Fixture.Id(20));
        Assert.AreEqual(Fixture.Id(20), asset.Id); Assert.AreEqual(result.Value.DurationTicks, asset.DurationTicks);
    }

    [TestMethod]
    [DataRow("mov")]
    [DataRow("mp4")]
    public void MovDurationUsesTheDecodedVideoStreamInsteadOfALongerAudioOrContainerTail(string extension)
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"30/1","duration":"6.000000"},{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2,"duration":"6.040000"}],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"6.040000"}}
            """;

        var result = MediaProbeParser.Parse(Path.GetFullPath("shot." + extension), json);

        Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
        Assert.AreEqual(6 * TimelineTime.TicksPerSecond, result.Value!.DurationTicks,
            "MOV placement must stop at the video stream Kachinco actually decodes, not an ignored embedded-audio tail.");
    }

    [TestMethod]
    [DataRow("mov")]
    [DataRow("mp4")]
    public void MovWithMissingFirstVideoDurationFallsBackToContainerInsteadOfAnotherVideoStream(string extension)
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"30/1"},{"codec_type":"video","codec_name":"mjpeg","duration":"12.0"}],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"6.04"}}
            """;

        var result = MediaProbeParser.Parse(Path.GetFullPath("multi-video." + extension), json);

        Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
        Assert.AreEqual(TimelineTime.SecondsToTicks(6.04m), result.Value!.DurationTicks,
            "The decoder selects 0:v:0; a later video stream cannot supply its duration.");
        Assert.AreEqual(1920, result.Value.Width);
    }

    [TestMethod]
    public void WavWithMissingFirstAudioDurationFallsBackToContainerInsteadOfAnotherAudioStream()
    {
        const string json = """
            {"streams":[{"codec_type":"audio","codec_name":"pcm_s16le","sample_rate":"48000","channels":2},{"codec_type":"audio","codec_name":"pcm_s16le","duration":"12.0"}],"format":{"format_name":"wav","duration":"6.04"}}
            """;

        var result = MediaProbeParser.Parse(Path.GetFullPath("multi-audio.wav"), json);

        Assert.IsTrue(result.Success, string.Join(";", result.Diagnostics));
        Assert.AreEqual(TimelineTime.SecondsToTicks(6.04m), result.Value!.DurationTicks,
            "The decoder selects 0:a:0; a later audio stream cannot supply its duration.");
        Assert.AreEqual(48000, result.Value.SampleRate);
    }

    [TestMethod]
    public void WavMetadataAndMalformedSourcesReturnStructuredDiagnostics()
    {
        var wav = MediaProbeParser.Parse(Path.GetFullPath("voice.wav"),
            "{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"pcm_s16le\",\"sample_rate\":\"44100\",\"channels\":1}],\"format\":{\"format_name\":\"wav\",\"duration\":\"0.5\"}}");
        Assert.IsTrue(wav.Success); Assert.AreEqual(MediaKind.Wav, wav.Value!.Kind);
        Assert.AreEqual(TimelineTime.TicksPerSecond / 2, wav.Value.DurationTicks);
        Assert.AreEqual(44100, wav.Value.SampleRate); Assert.AreEqual(1, wav.Value.Channels);

        Assert.AreEqual("MEDIA_KIND_MISMATCH", MediaProbeParser.Parse(Path.GetFullPath("fake.mov"),
            "{\"streams\":[{\"codec_type\":\"audio\"}],\"format\":{\"format_name\":\"wav\",\"duration\":\"1\"}}").Diagnostics[0].Code);
        Assert.AreEqual("MEDIA_PROBE_INVALID", MediaProbeParser.Parse(Path.GetFullPath("bad.wav"), "not-json").Diagnostics[0].Code);
        Assert.AreEqual("UNSUPPORTED_MEDIA_SOURCE", MediaProbeParser.Parse(Path.GetFullPath("image.png"), "{}").Diagnostics[0].Code);
    }

    [TestMethod]
    public async Task RealMovAndWavAreProbedWithoutManualMetadata()
    {
        if (!await ExecutableWorks("ffmpeg", "-version") || !await ExecutableWorks("ffprobe", "-version"))
            Assert.Inconclusive("FFmpeg/ffprobe are not installed on this runner.");
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string wav = Path.Combine(directory, "fixture.wav");
            string mov = Path.Combine(directory, "fixture.mov");
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "0.25", "-c:a", "pcm_s16le", wav);
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=160x90:r=30", "-t", "0.25", "-c:v", "mpeg4", "-an", mov);
            var probe = new FfprobeMediaProbe();
            var wavResult = await probe.ProbeAsync(wav);
            var movResult = await probe.ProbeAsync(mov);
            Assert.IsTrue(wavResult.Success, string.Join(";", wavResult.Diagnostics));
            Assert.IsTrue(movResult.Success, string.Join(";", movResult.Diagnostics));
            Assert.AreEqual(MediaKind.Wav, wavResult.Value!.Kind);
            Assert.AreEqual(48000, wavResult.Value.SampleRate); Assert.AreEqual(2, wavResult.Value.Channels);
            Assert.AreEqual(MediaKind.Mov, movResult.Value!.Kind);
            Assert.AreEqual(160, movResult.Value.Width); Assert.AreEqual(90, movResult.Value.Height);
            Assert.IsTrue(wavResult.Value.DurationTicks > 0); Assert.IsTrue(movResult.Value.DurationTicks > 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<bool> ExecutableWorks(string executable, params string[] arguments)
    {
        try { return await Run(executable, arguments) == 0; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static async Task<int> Run(string executable, params string[] arguments)
    {
        using var process = new Process { StartInfo = new()
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) Assert.Fail($"{executable} failed: {error.Result}");
        return process.ExitCode;
    }
}
