using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class MediaRelinkTests
{
    [TestMethod]
    public async Task MissingProjectReopensAndRelinkPreservesAssetClipAndPlacementIdentity()
    {
        var f = new Fixture();
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string projectPath = Path.Combine(directory, "proof.fkproj");
        try
        {
            var store = new ProjectFileStore();
            Assert.IsTrue((await store.SaveAsync(projectPath, f.Project)).Success);
            var loaded = (await store.LoadAsync(projectPath)).Value!;
            Assert.AreEqual(2, MediaReferenceResolver.Inspect(loaded, projectPath).Count(x => !x.IsAvailable));

            var before = f.VideoClip;
            var service = new MediaRelinkService(new StubProbe(new(Path.Combine(directory, "replacement.mov"),
                MediaKind.Mov, 12 * Fixture.T, null, null, 1920, 1080, new(30, 1), ["h264"])));
            var prepared = await service.PrepareAsync(f.Project, f.MovId, Path.Combine(directory, "replacement.mov"));
            Assert.IsTrue(prepared.Success, string.Join(";", prepared.Diagnostics));
            Assert.IsTrue(f.Edit(prepared.Value!).Success);
            Assert.AreEqual(f.MovId, f.Project.Assets.First(x => x.Id == f.MovId).Id);
            var after = f.VideoClip;
            Assert.AreEqual(before.Id, after.Id); Assert.AreEqual(before.StartTicks, after.StartTicks);
            Assert.AreEqual(before.SourceInTicks, after.SourceInTicks); Assert.AreEqual(before.DurationTicks, after.DurationTicks);
            Assert.IsTrue(f.Session.Undo().Success); Assert.AreEqual("input.mov", f.Project.Assets.First(x => x.Id == f.MovId).SourcePath);
            Assert.IsTrue(f.Session.Redo().Success); Assert.AreEqual(Path.Combine(directory, "replacement.mov"), f.Project.Assets.First(x => x.Id == f.MovId).SourcePath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task IncompatibleKindAndTooShortReplacementAreRejectedBeforeMutation()
    {
        var f = new Fixture(); var before = f.Session.GetProject();
        var wrongKind = new MediaRelinkService(new StubProbe(new(Path.GetFullPath("wrong.wav"), MediaKind.Wav,
            20 * Fixture.T, 48000, 2, null, null, null, ["pcm_s16le"])));
        Assert.AreEqual("INCOMPATIBLE_RELINK", (await wrongKind.PrepareAsync(f.Project, f.MovId, "wrong.wav")).Diagnostics[0].Code);

        var tooShort = new MediaRelinkService(new StubProbe(new(Path.GetFullPath("short.mov"), MediaKind.Mov,
            2 * Fixture.T, null, null, 1920, 1080, new(30, 1), ["h264"])));
        Assert.AreEqual("INCOMPATIBLE_RELINK", (await tooShort.PrepareAsync(f.Project, f.MovId, "short.mov")).Diagnostics[0].Code);
        Assert.AreEqual(before, f.Session.GetProject());
    }

    [TestMethod]
    public void RelativeMediaResolvesOnlyAgainstProjectDirectory()
    {
        var f = new Fixture();
        Assert.AreEqual("MEDIA_REFERENCE_UNRESOLVED", MediaReferenceResolver.Inspect(f.Project, null)[0].Diagnostics[0].Code);
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "input.mov"), [0]);
            var inspected = MediaReferenceResolver.Inspect(f.Project, Path.Combine(directory, "project.fkproj"));
            Assert.IsTrue(inspected.First(x => x.MediaAssetId == f.MovId).IsAvailable);
            Assert.AreEqual("MEDIA_MISSING", inspected.First(x => x.MediaAssetId == f.WavId).Diagnostics[0].Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class StubProbe(MediaProbeInfo value) : IMediaProbe
    {
        public Task<Result<MediaProbeInfo>> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MediaProbeInfo>.Ok(value));
    }
}
