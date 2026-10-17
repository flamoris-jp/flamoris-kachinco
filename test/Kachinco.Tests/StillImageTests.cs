using System.Diagnostics;
using System.Text.Json.Nodes;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class StillImageTests
{
    [TestMethod]
    public async Task CrossfadePixelsSurviveRealMp4Export()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-crossfade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string red = Path.Combine(dir, "red.png"), blue = Path.Combine(dir, "blue.png");
            foreach (var item in new[] { (red, "red"), (blue, "blue") })
                await Run(["-v", "error", "-f", "lavfi", "-i", "color=" + item.Item2 + ":s=16x16", "-frames:v", "1", "-threads", "1", item.Item1]);
            var f = new Fixture(); var first = Fixture.Id(120); var second = Fixture.Id(121); var incoming = Fixture.Id(122);
            long frame = TimelineTime.FrameToTicks(1, new(30, 1));
            Assert.IsTrue(f.Edit(new DeleteClip(f.SequenceId, f.ClipId), new DeleteClip(f.SequenceId, f.AudioClipId),
                new DeleteCaption(f.SequenceId, f.CaptionId),
                new RegisterMedia(new(first, "red", red, MediaKind.Image, Fixture.T)),
                new RegisterMedia(new(second, "blue", blue, MediaKind.Image, Fixture.T)),
                new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(f.ClipId, first, 0, 0, frame)),
                new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(incoming, second, frame, 0, 3 * frame)),
                new SetSequenceDuration(f.SequenceId, 4 * frame),
                new ApplyCrossfade(f.SequenceId, f.ClipId, incoming, 2 * frame, Fixture.Id(123), Fixture.Id(124))).Success);
            using var decoder = new FfmpegMediaDecoder();
            var renderer = new SharedFrameRenderer(decoder);
            using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
            var mid = await renderer.RenderAsync(f.Project, evaluator.Evaluate(2 * frame).Value!, 2, default);
            Assert.IsTrue(mid.Success);
            int center = (540 * 1920 + 960) * 4;
            Assert.IsTrue(mid.Value!.Rgba8[center] is > 110 and < 145);
            Assert.IsTrue(mid.Value.Rgba8[center + 2] is > 110 and < 145);
            string output = Path.Combine(dir, "fade.mp4");
            var exported = await new SnapshotExportService(renderer, new SharedAudioRenderer(decoder), new FfmpegEncodingBackend(), new(null, VideoEncoderPreference.Cpu))
                .ExportAsync(f.Session.GetProject(), new(Guid.NewGuid(), f.SequenceId, output, ExportPreset.YoutubeH264AacMp4), null, default);
            Assert.AreEqual(ExportStage.Completed, exported.Stage, string.Join(";", exported.Diagnostics.Select(d => d.Message)));
            string extracted = Path.Combine(dir, "mid.png");
            await Run(["-v", "error", "-i", output, "-vf", "select=eq(n\\,2)", "-frames:v", "1", "-threads", "1", extracted]);
            var decoded = await decoder.VideoAsync(extracted, 0, 1920, 1080, default);
            Assert.IsTrue(decoded[center] is > 100 and < 155);
            Assert.IsTrue(decoded[center + 2] is > 100 and < 155);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ImageClipsHaveOrdinaryDurationHistoryPersistenceAndSourceZero()
    {
        var f = new Fixture(); var imageId = Fixture.Id(120);
        Assert.IsTrue(f.Edit(new RegisterMedia(new(imageId, "image", "image.png", MediaKind.Image, Fixture.T)),
            new DeleteClip(f.SequenceId, f.ClipId), new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(f.ClipId, imageId, 0, 0, 8 * Fixture.T))).Success);
        Assert.AreEqual(4, JsonNode.Parse(ProjectJson.Serialize(f.Project).Value!)!["schemaVersion"]!.GetValue<int>());
        using var evaluator = TimelineEvaluator.Create(f.Project, f.SequenceId).Value!;
        Assert.AreEqual(0, evaluator.Evaluate(7 * Fixture.T).Value!.VideoLayers[0].SourceTicks);
        Assert.IsTrue(f.Edit(new SplitClip(f.SequenceId, f.ClipId, 2 * Fixture.T, Fixture.Id(121))).Success);
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId, f.ClipId, 0, 0, 3 * Fixture.T)).Success);
        var loaded = ProjectJson.Deserialize(ProjectJson.Serialize(f.Project).Value!); Assert.IsTrue(loaded.Success);
        Assert.AreEqual(ProjectJson.Serialize(f.Project).Value, ProjectJson.Serialize(loaded.Value!).Value);
        Assert.IsTrue(f.Session.Undo().Success); Assert.IsTrue(f.Session.Redo().Success);
        Assert.IsFalse(f.Edit(new InsertClip(f.SequenceId, f.AudioTrackId, Fixture.Clip(Fixture.Id(122), imageId, 0, 0, Fixture.T))).Success);
        var placed = TimelineEditPlanner.Place(f.Project, f.SequenceId, imageId, f.VideoTrackId, Fixture.Id(123), 7 * Fixture.T);
        Assert.IsFalse(placed.Success); // Ordinary overlap policy is unchanged.
        Assert.IsFalse(TimelineEditPlanner.Trim(f.Project, f.SequenceId, f.ClipId, TrimEdge.End, 4 * Fixture.T).Success); // Existing right clip overlap.
    }
    [TestMethod]
    [DataRow("png")] [DataRow("jpg")] [DataRow("webp")]
    public async Task RealStillProbeAndDecodeAreTimeIndependentAndForwardCompatible(string extension)
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-image-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "image." + extension);
            await Run(["-v", "error", "-f", "lavfi", "-i", "color=red:s=16x16", "-frames:v", "1", "-threads", "1", path]);
            var probe = await new FfprobeMediaProbe().ProbeAsync(path); Assert.IsTrue(probe.Success, string.Join(";", probe.Diagnostics));
            Assert.AreEqual(MediaKind.Image, probe.Value!.Kind); Assert.AreEqual(5 * Fixture.T, probe.Value.DurationTicks);
            using var decoder = new FfmpegMediaDecoder();
            var initial = await decoder.VideoAsync(path, 0, 32, 32, default);
            var later = await decoder.VideoAsync(path, 100 * Fixture.T, 32, 32, default);
            CollectionAssert.AreEqual(initial.ToArray(), later.ToArray());
            using var forward = new FfmpegForwardDecoder();
            CollectionAssert.AreEqual(initial.ToArray(), (await forward.VideoAsync(path, 100 * Fixture.T, 32, 32, default)).ToArray());
            Assert.AreEqual(0, forward.ProcessStarts); // Still images never open moving video streams.
            var renamed = Path.Combine(dir, "wrong.mov"); File.Copy(path, renamed);
            Assert.IsFalse((await new FfprobeMediaProbe().ProbeAsync(renamed)).Success);
        }
        finally { Directory.Delete(dir, true); }
    }
    [TestMethod]
    public async Task RealImagePreviewAndExportShareAnimatedFramePixels()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kachinco-image-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "image.png");
            await Run(["-v", "error", "-f", "lavfi", "-i", "color=red:s=16x16", "-frames:v", "1", "-threads", "1", path]);
            var f = new Fixture(); var image = Fixture.Id(120); long duration = Fixture.T / 10;
            Assert.IsTrue(f.Edit(new DeleteClip(f.SequenceId, f.ClipId), new DeleteClip(f.SequenceId, f.AudioClipId), new DeleteCaption(f.SequenceId, f.CaptionId),
                new RegisterMedia(new(image, "image", path, MediaKind.Image, duration)),
                new InsertClip(f.SequenceId, f.VideoTrackId, Fixture.Clip(f.ClipId, image, 0, 0, duration)),
                new SetSequenceDuration(f.SequenceId, duration), new SetClipPropertyCurve(f.SequenceId, f.ClipId,
                    new(VisualProperty.X, [new(Fixture.Id(100), 0, 0), new(Fixture.Id(101), duration, 960)]))).Success);
            using var decoder = new FfmpegMediaDecoder(); var renderer = new SharedFrameRenderer(decoder);
            var plan = ExportPlanner.Create(f.Project, f.SequenceId).Value!;
            using var evaluator = plan.Evaluator;
            var middle = plan.EvaluateFrame(1).Value!;
            Assert.AreEqual(320, middle.VideoLayers[0].Appearance.Transform.X, 1e-9);
            var preview = await renderer.RenderAsync(f.Project, middle, 1, default);
            Assert.IsTrue(preview.Success);
            var exportFrame = await new PreviewService(renderer).RenderAsync(plan, 1);
            CollectionAssert.AreEqual(preview.Value!.Rgba8.ToArray(), exportFrame.Value!.Rgba8.ToArray());
            string output = Path.Combine(dir, "output.mp4");
            var exported = await new SnapshotExportService(renderer, new SharedAudioRenderer(decoder), new FfmpegEncodingBackend(), new(null))
                .ExportAsync(f.Session.GetProject(), new(Guid.NewGuid(), f.SequenceId, output, ExportPreset.YoutubeH264AacMp4), null, default);
            Assert.AreEqual(ExportStage.Completed, exported.Stage, string.Join(";", exported.Diagnostics));
        }
        finally { Directory.Delete(dir, true); }
    }
    internal static async Task Run(string[] arguments)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await error);
    }
}
