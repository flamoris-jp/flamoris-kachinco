using System.Diagnostics;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class AudioSeekTests
{
    [TestMethod]
    [DataRow(48000, 1, "mp3")]
    [DataRow(44100, 2, "mp3")]
    [DataRow(48000, 2, "wav")]
    public async Task IndependentAudioBlocksMatchContinuousDecodeWithoutSeekSilence(int sourceRate, int sourceChannels, string extension)
    {
        string root = Path.Combine(Path.GetTempPath(), "kachinco-audio-seek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "continuous." + extension);
            var info = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (string arg in new[] {
                "-v", "error", "-nostdin", "-f", "lavfi", "-i",
                $"aevalsrc=0.2*sin(2*PI*731*t)+0.1*sin(2*PI*137*t):s={sourceRate}:d=6",
                "-ac", sourceChannels.ToString(), "-c:a", extension == "mp3" ? "libmp3lame" : "pcm_s16le",
                "-y", path
            }) info.ArgumentList.Add(arg);
            using (var process = Process.Start(info)!)
            {
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                await Task.WhenAll(error, output);
                Assert.AreEqual(0, process.ExitCode, error.Result);
            }

            using var decoder = new FfmpegMediaDecoder();
            const int rate = 48000, channels = 2, count = 4800;
            var continuous = await decoder.AudioAsync(path, 0, 6 * rate, rate, channels, default);
            // Export's one-second boundaries, preview's 100 ms windows, fractional
            // starts and the padded EOF must all retain the same source samples.
            foreach (decimal seconds in new[] { 0m, .1m, 1m, 2m, 2.123m, 3.999m, 5.975m })
            {
                long tick = TimelineTime.SecondsToTicks(seconds);
                int first = (int)(seconds * rate);
                var block = await decoder.AudioAsync(path, tick, count, rate, channels, default);
                Assert.AreEqual(count * channels, block.Length);
                double difference = 0;
                for (int i = 0; i < block.Length; i++)
                {
                    int source = first * channels + i;
                    float expected = source < continuous.Length ? continuous[source] : 0;
                    difference = Math.Max(difference, Math.Abs(block[i] - expected));
                }
                Assert.IsTrue(difference < .0001,
                    $"{sourceRate} Hz {extension} at {seconds}s: max PCM difference {difference}; seeking must not add silence or shift samples.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
