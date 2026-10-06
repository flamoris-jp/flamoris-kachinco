using System.Collections.Concurrent;
using System.Collections.Immutable;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kachinco.Tests;

[TestClass]
public sealed class InteractivePreviewTests
{
    [TestMethod]
    public void MonitoringGainChangesQueuedDeviceWithoutChangingClockPCMOrProjectAndSurvivesResume()
    {
        using var pump = new Pump(); var f = new Fixture(); var snapshot = f.Session.GetProject();
        var source = new Source(); var devices = new List<Device>();
        using var p = new InteractivePreview(source, () => { var d = new Device(); devices.Add(d); return d; });
        p.SetMonitoringGain(.35); p.SetContext(Context(f)); p.Play(); pump.Until(() => p.State == InteractivePreviewState.Playing);
        var device = devices.Single(); Assert.AreEqual(.35, device.MonitoringGain);
        device.Advance(4800); long tick = p.ReadPositionTicks(), queued = device.QueuedFrames;
        int audioRequests = source.Audio.Count;
        p.SetMonitoringGain(0); Assert.AreEqual(0d, device.MonitoringGain);
        Assert.AreEqual(InteractivePreviewState.Playing, p.State); Assert.AreEqual(tick, p.ReadPositionTicks());
        Assert.AreEqual(queued, device.QueuedFrames); Assert.AreEqual(audioRequests, source.Audio.Count);
        Assert.AreEqual(snapshot, f.Session.GetProject());
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -.01, 1.01 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => p.SetMonitoringGain(invalid));
        p.Pause(); pump.Until(() => p.Completion.IsCompleted); p.SetMonitoringGain(.8);
        p.Play(); pump.Until(() => devices.Count == 2 && p.State == InteractivePreviewState.Playing);
        Assert.AreEqual(.8, devices[1].MonitoringGain); p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }
    [TestMethod]
    public void ScrubMailboxIsBoundedLatestWinsAndNeverMutatesProject()
    {
        using var pump = new Pump(); var f = new Fixture(); var snapshot = f.Session.GetProject();
        var source = new Source { Hold = true }; using var p = new InteractivePreview(source, () => new Device());
        p.SetContext(Context(f)); var first = source.Pending!;
        for (int i = 1; i <= 1000; i++) p.Scrub(i * 100);
        Assert.AreEqual(1, source.Video.Count); Assert.IsTrue(source.Token.IsCancellationRequested);
        Assert.IsTrue(p.HasPendingRequest); Assert.IsNull(p.Frame);
        source.Hold = false; first.SetResult(Frame(0)); pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(2, source.Video.Count); Assert.AreEqual(100000L, p.Frame!.Tick);
        Assert.AreEqual(snapshot, f.Session.GetProject());
        source.Fail = true; p.Scrub(Fixture.T); pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, p.State); Assert.IsNull(p.Frame);
        StringAssert.Contains(p.Error!, "missing media");
    }
    [TestMethod]
    public void CurrentPositionStartupIsIndependentOfDurationAndUsesOnlyConsumedPcm()
    {
        using var pump = new Pump();
        foreach (decimal duration in new[] { 8m, 219.6m })
        {
            var f = new Fixture(); Assert.IsTrue(f.Edit(new SetSequenceDuration(f.SequenceId, TimelineTime.SecondsToTicks(duration))).Success);
            var source = new Source(); var device = new Device(); using var p = new InteractivePreview(source, () => device = new Device());
            p.SetContext(Context(f)); p.Scrub(4 * Fixture.T); source.Video.Clear(); source.Audio.Clear();
            p.Play(); pump.Until(() => p.State == InteractivePreviewState.Playing);
            Assert.IsTrue(source.Video.All(t => t >= 4 * Fixture.T));
            Assert.IsTrue(source.Video.Count <= 1 + InteractivePreview.ForwardVideoFrames);
            Assert.IsTrue(source.Audio.All(t => t >= 4 * 48000)); Assert.IsTrue(device.QueuedFrames <= 24000);
            Assert.AreEqual(4 * Fixture.T, p.ReadPositionTicks()); pump.Drain(); Assert.AreEqual(4 * Fixture.T, p.ReadPositionTicks());
            device.Advance(4800); Assert.AreEqual(TimelineTime.SecondsToTicks(4.1m), p.ReadPositionTicks());
            p.Pause(); Assert.AreEqual(InteractivePreviewState.Paused, p.State); Assert.IsFalse(device.Running);
            var pausedDevice = device; pump.Until(() => p.Completion.IsCompleted);
            Assert.AreEqual(p.PositionTicks, p.Frame!.Tick); Assert.IsTrue(pausedDevice.Disposed);
            p.Play(); pump.Until(() => p.State == InteractivePreviewState.Playing);
            p.Scrub(6 * Fixture.T); Assert.AreEqual(6 * Fixture.T, p.ReadPositionTicks(), "Old device cannot overwrite a new seek.");
            pump.Until(() => p.Completion.IsCompleted); Assert.AreEqual(6 * Fixture.T, p.Frame!.Tick); Assert.IsTrue(device.Disposed);
            p.Stop(); pump.Until(() => p.Completion.IsCompleted); Assert.AreEqual(InteractivePreviewState.Stopped, p.State); Assert.AreEqual(0L, p.Frame!.Tick);
        }
    }
    [TestMethod]
    public void PreparationPauseAndEditRejectOldCallbacksAndKeepUnaffectedPlayback()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source(); var devices = new List<Device>();
        using var p = new InteractivePreview(source, () => { var d = new Device(); devices.Add(d); return d; });
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted);
        source.Hold = true; p.Play(); var late = source.Pending!;
        p.Pause(); source.Hold = false; late.SetResult(Frame(0)); pump.Until(() => p.State == InteractivePreviewState.Paused);
        pump.Until(() => p.Completion.IsCompleted);
        Assert.IsFalse(devices[0].Running); Assert.IsTrue(devices[0].Disposed);
        p.Play(); pump.Until(() => p.State == InteractivePreviewState.Playing); Assert.IsTrue(devices[1].Running);
        Assert.IsTrue(f.Edit(new MoveClip(f.SequenceId, f.AudioClipId, f.AudioTrackId, 2 * Fixture.T)).Success);
        p.SetContext(Context(f)); Assert.AreEqual(2, devices.Count, "Edit outside queued window preserves transport.");
        Assert.IsTrue(f.Edit(new SetClipProperties(f.SequenceId, f.ClipId, true, ClipAppearance.Default with { Opacity = .5 }, AudioProperties.Default)).Success);
        p.SetContext(Context(f)); pump.Until(() => devices.Count == 3 && p.State == InteractivePreviewState.Playing);
        Assert.IsTrue(devices[1].Disposed); p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }
    [TestMethod]
    public void DeviceAndAudioFailuresClearViewerAndDisposeTransport()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source();
        using var p = new InteractivePreview(source, () => throw new IOException("audio device unavailable"));
        p.SetContext(Context(f)); p.Play(); pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, p.State); Assert.IsNull(p.Frame); StringAssert.Contains(p.Error!, "audio device unavailable");
        var device = new Device(); source.AudioFail = true;
        using var a = new InteractivePreview(source, () => device); a.SetContext(Context(f)); a.Play(); pump.Until(() => a.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, a.State); Assert.IsNull(a.Frame); Assert.IsTrue(device.Disposed); StringAssert.Contains(a.Error!, "PCM failed");
    }
    [TestMethod]
    public void UnderrunFreezesSampleClockAndRefillsWithoutUnboundedVideoWork()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source(); var device = new Device();
        using var p = new InteractivePreview(source, () => device); p.SetContext(Context(f)); p.Play();
        pump.Until(() => p.State == InteractivePreviewState.Playing); source.AudioHold = true;
        device.Advance(24000); pump.Until(() => source.PendingAudio is not null);
        Assert.AreEqual(InteractivePreviewState.Buffering, p.State); Assert.IsFalse(device.Running);
        long tick = p.ReadPositionTicks(); device.Advance(48000); Assert.AreEqual(tick, p.ReadPositionTicks());
        Assert.AreEqual(1L, p.Underruns);
        source.AudioHold = false; source.PendingAudio!.SetResult(source.HeldAudio!);
        pump.Until(() => p.State == InteractivePreviewState.Playing);
        Assert.IsTrue(device.QueuedFrames <= 24000); Assert.IsTrue(p.DroppedVideoFrames > 0);
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }
    [TestMethod]
    public void EndCursorDisplaysTheLastCanonicalFrameAndPlayRestartsAtZero()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source(); var device = new Device();
        using var p = new InteractivePreview(source, () => device); p.SetContext(Context(f)); p.Scrub(8 * Fixture.T);
        pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(TimelineTime.FrameToTicks(239, new(30, 1)), p.Frame!.Tick);
        Assert.AreEqual(8 * Fixture.T, p.PositionTicks);
        source.Video.Clear(); p.Play(); pump.Until(() => p.State == InteractivePreviewState.Playing);
        Assert.AreEqual(0L, source.Video[0]); p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }
    [TestMethod]
    public void SampleMappingRoundsForwardWithoutInventingASecondTimebase()
    {
        foreach (long tick in new[] { 0L, 1L, Fixture.T, Fixture.T + 1, 219 * Fixture.T })
        {
            long sample = InteractivePreview.FirstSample(tick); long actual = TimelineTime.SampleToTicks(sample, 48000);
            Assert.IsTrue(actual >= tick && actual - tick < 735);
        }
    }

    [TestMethod]
    public void PlaybackContinuesAcrossTheExactBoundaryBetweenDifferentVideoAssets()
    {
        using var pump = new Pump();
        var fixture = new Fixture();
        var secondAsset = Fixture.Id(30); var secondClip = Fixture.Id(31);
        Assert.IsTrue(fixture.Edit(
            new TrimClip(fixture.SequenceId, fixture.ClipId, 0, Fixture.T, Fixture.T),
            new RegisterMedia(new(secondAsset, "second", "second.mov", MediaKind.Mov, 10 * Fixture.T)),
            new InsertClip(fixture.SequenceId, fixture.VideoTrackId,
                Fixture.Clip(secondClip, secondAsset, Fixture.T, 2 * Fixture.T, 7 * Fixture.T))).Success);
        var source = new Source(); var device = new Device();
        using var preview = new InteractivePreview(source, () => device);
        preview.SetContext(Context(fixture)); preview.Scrub(Fixture.T / 2); pump.Until(() => preview.Completion.IsCompleted);
        source.Contributors.Clear(); preview.Play(); pump.Until(() => preview.State == InteractivePreviewState.Playing);

        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!source.Contributors.Any(x => x.Tick >= Fixture.T && x.ClipId == secondClip))
        {
            device.Advance(1600); pump.Drain(); Thread.Sleep(1);
            if (DateTime.UtcNow > limit) Assert.Fail("Playback did not cross the adjacent clip boundary.");
        }

        Assert.AreNotEqual(InteractivePreviewState.Failed, preview.State);
        Assert.IsTrue(source.Contributors.Any(x => x.Tick < Fixture.T && x.ClipId == fixture.ClipId));
        Assert.IsTrue(source.Contributors.Any(x => x.Tick >= Fixture.T && x.ClipId == secondClip && x.MediaAssetId == secondAsset));
        preview.Dispose(); pump.Until(() => preview.Completion.IsCompleted);
    }
    [TestMethod]
    public void PauseAtClipBoundaryRendersFrozenSampleTimeAndRejectsLateOlderFrames()
    {
        using var pump = new Pump(); var f = new Fixture(); var second = Fixture.Id(30); var clip = Fixture.Id(31);
        Assert.IsTrue(f.Edit(new TrimClip(f.SequenceId,f.ClipId,0,Fixture.T,Fixture.T),
            new RegisterMedia(new(second,"second","second.mov",MediaKind.Mov,10*Fixture.T)),
            new InsertClip(f.SequenceId,f.VideoTrackId,Fixture.Clip(clip,second,Fixture.T,0,7*Fixture.T))).Success);
        var source = new Source(); var devices = new List<Device>();
        using var p = new InteractivePreview(source,()=>{var d=new Device();devices.Add(d);return d;});
        p.SetContext(Context(f)); p.Scrub(9*Fixture.T/10); pump.Until(()=>p.Completion.IsCompleted);
        p.Play(); pump.Until(()=>p.State==InteractivePreviewState.Playing);
        source.Hold=true; devices[^1].Advance(9600); pump.Until(()=>source.Pending is not null);
        var late=source.Pending!; p.Pause(); long frozen=p.PositionTicks;
        Assert.AreEqual(11*Fixture.T/10,frozen); Assert.IsFalse(devices[^1].Running);
        source.Hold=false; late.SetResult(Frame(Fixture.T/2)); pump.Until(()=>p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Paused,p.State); Assert.AreEqual(frozen,p.Frame!.Tick);
        Assert.AreEqual(clip,source.Contributors.Last().ClipId); Assert.IsTrue(devices[^1].Disposed);
        for(int i=0;i<5;++i)
        {
            p.Play(); pump.Until(()=>p.State==InteractivePreviewState.Playing); devices[^1].Advance(1600);
            p.Pause(); frozen=p.PositionTicks; pump.Until(()=>p.Completion.IsCompleted);
            Assert.AreEqual(frozen,p.Frame!.Tick); Assert.IsTrue(devices[^1].Disposed);
        }
        p.Scrub(79*Fixture.T/10); pump.Until(()=>p.Completion.IsCompleted); p.Play();
        pump.Until(()=>p.State==InteractivePreviewState.Playing); devices[^1].Advance(4800);
        pump.Until(()=>p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Stopped,p.State); Assert.AreEqual(8*Fixture.T,p.PositionTicks);
        Assert.AreEqual(TimelineTime.FrameToTicks(239,new(30,1)),p.Frame!.Tick);
    }
    internal static PreviewContext Context(Fixture f) => PreviewContext.Create(f.Session.GetProject(), f.SequenceId).Value!;
    [TestMethod]
    public void ReadyVideoPresentsWhileTheNextDecodeIsBlockedAndProducerSlotsAreBounded()
    {
        using var pump = new Pump(); var f = new Fixture(); var device = new Device();
        long oneFrame = TimelineTime.FrameToTicks(1, new(30, 1));
        var source = new Source { HoldFromTick = 2 * oneFrame };
        using var p = new InteractivePreview(source, () => device);
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted);
        p.Play(); pump.Until(() => source.Pending is not null);
        Assert.AreEqual(1, p.ReadyVideoFrames);
        var blocked = source.Pending!;
        device.Advance(1600);
        pump.Until(() => p.Frame?.Tick == oneFrame);
        Assert.IsFalse(blocked.Task.IsCompleted, "Presentation must not await the producer's next decode.");
        Assert.AreEqual(oneFrame, p.ReadPositionTicks());
        Assert.IsTrue(device.Running);
        source.HoldFromTick = long.MaxValue; blocked.SetResult(Frame(2 * oneFrame));
        pump.Until(() => p.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        Assert.IsTrue(p.MaximumVideoFrames <= InteractivePreview.ForwardVideoFrames);
        int requests = source.Video.Count;
        for (int i = 0; i < 20; i++) { pump.Drain(); Thread.Sleep(1); }
        Assert.AreEqual(requests, source.Video.Count, "Full ready queue must apply producer backpressure.");
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }

    [TestMethod]
    public void QualitySwitchJoinsBlockedProducerAndOnlyPublishesTheNewSession()
    {
        using var pump = new Pump(); var f = new Fixture(); var devices = new List<Device>();
        long oneFrame = TimelineTime.FrameToTicks(1, new(30, 1));
        var source = new Source { HoldFromTick = 2 * oneFrame };
        using var p = new InteractivePreview(source, () => { var d = new Device(); devices.Add(d); return d; });
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted);
        p.Play(); pump.Until(() => source.Pending is not null);
        var old = source.Pending!; devices[0].Advance(1600);
        p.SetQuality(PreviewQuality.Quarter);
        Assert.IsFalse(devices[0].Running); Assert.IsTrue(source.Token.IsCancellationRequested);
        Assert.AreEqual(1, devices.Count, "New session cannot race a cancelled producer.");
        source.HoldFromTick = long.MaxValue; old.SetResult(Frame(0));
        pump.Until(() => devices.Count == 2 && p.State == InteractivePreviewState.Playing);
        Assert.IsTrue(devices[0].Disposed); Assert.AreEqual(oneFrame, p.Frame!.Tick);
        Assert.AreEqual(PreviewQuality.Quarter, source.LastQuality);
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }

    [TestMethod]
    public void BackendChangesJoinOldWorkPreserveRapidSwitchPlayAndRespectNewerScrub()
    {
        using var pump = new Pump(); var f = new Fixture(); var snapshot = f.Session.GetProject();
        var devices = new List<Device>();
        var source = new Source { HoldFromTick = TimelineTime.FrameToTicks(2, new(30, 1)) };
        using var p = new InteractivePreview(source, () => { var device = new Device(); devices.Add(device); return device; });
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted); p.Play();
        pump.Until(() => source.Pending is not null);
        var oldFrame = source.Pending!;
        p.SetBackendPreference(PreviewBackendPreference.D3D11);
        p.SetBackendPreference(PreviewBackendPreference.Auto);
        p.SetBackendPreference(PreviewBackendPreference.Cpu);
        Assert.IsTrue(source.Token.IsCancellationRequested); Assert.IsFalse(devices[0].Running);
        Assert.AreEqual(0, source.Backends.Count, "Backend selection must wait for the old producer to join.");
        source.HoldFromTick = long.MaxValue; oldFrame.SetResult(Frame(0));
        pump.Until(() => devices.Count == 2 && p.State == InteractivePreviewState.Playing);
        CollectionAssert.AreEqual(new[] { PreviewBackendPreference.Cpu }, source.Backends);
        Assert.IsTrue(devices[0].Disposed);
        p.SetBackendPreference(PreviewBackendPreference.D3D11);
        p.Scrub(6 * Fixture.T);
        pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Paused, p.State); Assert.AreEqual(6 * Fixture.T, p.Frame!.Tick);
        Assert.AreEqual(PreviewBackendPreference.D3D11, source.Backends.Last());
        Assert.AreEqual(snapshot, f.Session.GetProject());
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }

    [TestMethod]
    public void SlowBackendSelectionCannotOverrideNewerCpuChoiceAndScrub()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source { HoldBackend = true };
        using var p = new InteractivePreview(source, () => new Device());
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted);
        p.SetBackendPreference(PreviewBackendPreference.D3D11);
        Assert.IsNotNull(source.PendingBackend);
        p.SetBackendPreference(PreviewBackendPreference.Cpu); p.Scrub(5 * Fixture.T);
        Assert.IsFalse(p.Completion.IsCompleted);
        source.HoldBackend = false; source.PendingBackend.SetResult();
        pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(PreviewBackendPreference.Cpu, source.Backends.Last());
        Assert.AreEqual(InteractivePreviewState.Paused, p.State); Assert.AreEqual(5 * Fixture.T, p.Frame!.Tick);
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }

    [TestMethod]
    public void NewBackendChoiceDuringSlowSelectionPreservesRequestedPlayBeforeDeviceOpens()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source { HoldBackend = true };
        var devices = new List<Device>();
        using var p = new InteractivePreview(source, () => { var device = new Device(); devices.Add(device); return device; });
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted);
        p.SetBackendPreference(PreviewBackendPreference.D3D11); p.Play();
        Assert.IsNotNull(source.PendingBackend); Assert.AreEqual(0, devices.Count);
        p.SetBackendPreference(PreviewBackendPreference.Cpu);
        source.HoldBackend = false; source.PendingBackend!.SetResult();
        pump.Until(() => p.State == InteractivePreviewState.Playing);
        Assert.AreEqual(PreviewBackendPreference.Cpu, source.Backends.Last()); Assert.AreEqual(1, devices.Count);
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted);
    }

    [TestMethod]
    public void ForwardProducerFailureClearsPresentationAndJoinsAudio()
    {
        using var pump = new Pump(); var f = new Fixture(); var device = new Device();
        var source = new Source { FailFromTick = TimelineTime.FrameToTicks(2, new(30, 1)) };
        using var p = new InteractivePreview(source, () => device);
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted); p.Play();
        pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, p.State);
        Assert.IsNull(p.Frame); Assert.IsNull(p.Presentation); Assert.IsTrue(device.Disposed);
        StringAssert.Contains(p.Error!, "missing media");
    }

    [TestMethod]
    public void LateReadyFramesCoalesceToTheNewestDueFrameWithoutAStalePresentationBurst()
    {
        using var pump = new Pump(); var f = new Fixture(); var source = new Source(); var device = new Device();
        var devices = new List<Device>();
        using var p = new InteractivePreview(source, () => { var next = new Device(); devices.Add(next); return next; });
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted); p.Play();
        pump.Until(() => p.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames); device = devices[0];
        var presented = new List<long>(); p.Changed += (_, _) => { if (p.Frame is { } frame) presented.Add(frame.Tick); };
        source.Hold = true; device.Advance(4800);
        pump.Until(() => p.Frame?.Tick == Fixture.T / 10 && source.Pending is not null);
        Assert.AreEqual(2L, p.DroppedVideoFrames);
        Assert.IsTrue(presented.All(t => t == Fixture.T / 10), "Only newest due frame reaches Dispatcher.");
        var held = source.Pending!; p.SetQuality(PreviewQuality.Quarter);
        source.Hold = false; held.SetResult(Frame(0));
        pump.Until(() => p.State == InteractivePreviewState.Playing && p.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        Assert.AreEqual(Fixture.T / 10, p.Frame!.Tick); Assert.IsTrue(device.Disposed);
        Assert.AreEqual(2L, p.DroppedVideoFrames, "Internal quality restart preserves the explicit playback result.");
        p.Dispose(); pump.Until(() => p.Completion.IsCompleted); Assert.IsTrue(p.Presentation is null);
    }
    [TestMethod]
    public void ProducerFailurePausesAudioBeforeJoiningAnUncancellablePeer()
    {
        using var pump = new Pump(); var f = new Fixture(); var device = new Device();
        var source = new Source { HoldFromTick = TimelineTime.FrameToTicks(4, new(30, 1)) };
        using var p = new InteractivePreview(source, () => device);
        p.SetContext(Context(f)); pump.Until(() => p.Completion.IsCompleted); p.Play();
        pump.Until(() => p.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        source.AudioHold = true; device.Advance(4800);
        pump.Until(() => source.Pending is not null && source.PendingAudio is not null);
        source.Pending!.SetResult(Result<RenderedVideoFrame>.Fail(Diagnostic.Error("MISSING", "missing media")));
        pump.Until(() => !device.Running);
        Assert.IsFalse(p.Completion.IsCompleted, "Teardown must still join the held audio request.");
        Assert.IsFalse(device.Disposed, "Device lifetime ends only after its producer joins.");
        source.AudioHold = false; source.PendingAudio!.SetResult(source.HeldAudio!);
        pump.Until(() => p.Completion.IsCompleted);
        Assert.AreEqual(InteractivePreviewState.Failed, p.State);
        Assert.IsTrue(device.Disposed); Assert.AreEqual(0, p.ReadyVideoFrames);
        Assert.IsNull(p.Presentation); StringAssert.Contains(p.Error!, "missing media");
    }

    private static Result<RenderedVideoFrame> Frame(long tick) => Result<RenderedVideoFrame>.Ok(new(0, tick, 1, 1, [1, 2, 3, 255]));

    [TestMethod]
    public void ExplicitPlayClearsSkipsImmediatelyWhilePauseStopAndQualityRetainTheResult()
    {
        using var pump = new Pump(); var fixture = new Fixture(); var source = new Source();
        var devices = new List<Device>();
        using var preview = new InteractivePreview(source, () => { var device = new Device(); devices.Add(device); return device; });
        preview.SetContext(Context(fixture)); pump.Until(() => preview.Completion.IsCompleted);
        preview.Play(); pump.Until(() => preview.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        source.Hold = true; devices[0].Advance(4800);
        pump.Until(() => preview.DroppedVideoFrames > 0 && source.Pending is not null);
        long result = preview.DroppedVideoFrames;
        var old = source.Pending!;
        preview.Pause(); Assert.AreEqual(result, preview.DroppedVideoFrames);
        source.Hold = false; old.SetResult(Frame(0)); pump.Until(() => preview.Completion.IsCompleted);
        preview.SetQuality(PreviewQuality.Quarter); pump.Until(() => preview.Completion.IsCompleted);
        Assert.AreEqual(result, preview.DroppedVideoFrames);
        preview.Stop(); pump.Until(() => preview.Completion.IsCompleted);
        Assert.AreEqual(result, preview.DroppedVideoFrames);
        source.Hold = true; preview.Play();
        Assert.AreEqual(0L, preview.DroppedVideoFrames, "Clear happens at the Play intent, before buffering completes.");
        var first = source.Pending!; source.Hold = false; first.SetResult(Frame(0));
        pump.Until(() => preview.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        preview.Dispose(); pump.Until(() => preview.Completion.IsCompleted);
    }

    [TestMethod]
    public void RapidReplayCancelsOldProducersBeforeClearingAndRejectsTheirLateFrames()
    {
        using var pump = new Pump(); var fixture = new Fixture(); var source = new Source();
        var devices = new List<Device>();
        using var preview = new InteractivePreview(source, () => { var device = new Device(); devices.Add(device); return device; });
        preview.SetContext(Context(fixture)); pump.Until(() => preview.Completion.IsCompleted);
        preview.Play(); pump.Until(() => preview.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        source.Hold = true; devices[0].Advance(4800);
        pump.Until(() => preview.DroppedVideoFrames > 0 && source.Pending is not null);
        var late = source.Pending!;
        preview.Play(); Assert.AreEqual(0L, preview.DroppedVideoFrames);
        Assert.IsTrue(source.Token.IsCancellationRequested);
        source.Hold = false; late.SetResult(Frame(0));
        pump.Until(() => devices.Count == 2 && preview.ReadyVideoFrames == InteractivePreview.ForwardVideoFrames);
        Assert.AreEqual(0L, preview.DroppedVideoFrames, "Joined stale work cannot add skips to the new playback.");
        Assert.IsTrue(devices[0].Disposed);
        preview.Dispose(); pump.Until(() => preview.Completion.IsCompleted);
    }
    private sealed class Source : IInteractivePreviewSource
    {
        public readonly List<long> Video = [], Audio = [];
        public readonly List<PreviewBackendPreference> Backends = [];
        public readonly List<(long Tick, Guid ClipId, Guid MediaAssetId, long SourceTick)> Contributors = [];
        public bool Hold, Fail, AudioFail, AudioHold, HoldBackend;
        public TaskCompletionSource? PendingBackend;
        public long HoldFromTick = long.MaxValue, FailFromTick = long.MaxValue;
        public PreviewQuality LastQuality;
        public TaskCompletionSource<Result<RenderedAudioBlock>>? PendingAudio; public Result<RenderedAudioBlock>? HeldAudio; public CancellationToken Token; public TaskCompletionSource<Result<RenderedVideoFrame>>? Pending;
        public ValueTask<Result<RenderedVideoFrame>> FrameAsync(PreviewContext c, long tick, PreviewQuality q, bool forward, CancellationToken token)
        {
            Video.Add(tick); Token = token; LastQuality = q;
            var layer = c.Evaluator.Evaluate(tick).Value!.VideoLayers.LastOrDefault();
            if (layer is not null) Contributors.Add((tick, layer.ClipId, layer.MediaAssetId, layer.SourceTicks));
            if (Hold || forward && tick >= HoldFromTick) { Pending = new(); return new(Pending.Task); } // Deliberately ignores cancellation: stale rejection must still work.
            return ValueTask.FromResult(Fail || forward && tick >= FailFromTick ? Result<RenderedVideoFrame>.Fail(Diagnostic.Error("MISSING", "missing media")) : Frame(tick));
        }
        public ValueTask<Result<RenderedAudioBlock>> AudioAsync(PreviewContext c, long first, int count, CancellationToken token)
        {
            Audio.Add(first);
            if (AudioHold) { PendingAudio = new(); HeldAudio = Result<RenderedAudioBlock>.Ok(new(first, 48000, 2, new float[count * 2].ToImmutableArray())); return new(PendingAudio.Task); }
            return ValueTask.FromResult(AudioFail ? Result<RenderedAudioBlock>.Fail(Diagnostic.Error("AUDIO", "PCM failed")) :
                Result<RenderedAudioBlock>.Ok(new(first, 48000, 2, new float[count * 2].ToImmutableArray())));
        }
        public async ValueTask SelectBackendAsync(PreviewBackendPreference preference, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Backends.Add(preference);
            if (HoldBackend) { PendingBackend = new(); await PendingBackend.Task; } // Deliberately join an uncancellable host callback.
        }
    }
    private sealed class Device : IPreviewAudioOutput
    {
        public long PlayedFrames { get; private set; } public long QueuedFrames => submitted - PlayedFrames;
        private long submitted; public bool Running, Disposed;
        public void Advance(long samples) { if (Running) PlayedFrames = Math.Min(submitted, PlayedFrames + samples); }
        public void Enqueue(RenderedAudioBlock block) { submitted += block.Samples.Length / 2; Assert.IsTrue(QueuedFrames <= 24000); }
        public double MonitoringGain { get; private set; } = 1;
        public void SetMonitoringGain(double gain) => MonitoringGain = gain;
        public void Play() => Running = true; public void Pause() => Running = false;
        public void Dispose() { Running = false; Disposed = true; }
    }
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? previous = Current;
        private readonly ConcurrentQueue<Action> queue = new();
        public Pump() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) => queue.Enqueue(() => callback(state));
        public void Drain() { while (queue.TryDequeue(out var callback)) callback(); }
        public void Until(Func<bool> condition)
        {
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { Drain(); if (DateTime.UtcNow > limit) Assert.Fail("Preview did not reach expected state."); Thread.Sleep(1); }
            Drain();
        }
        public void Dispose() => SetSynchronizationContext(previous);
    }
}
