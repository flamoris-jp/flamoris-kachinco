using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Kachinco.Core;
using Kachinco.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kachinco.Tests;
[TestClass]
public sealed class NativeTimelineTests
{
    [TestMethod]
    public void SeededSnapshotEvaluationMatchesFrozenManagedOrderingRangesAndParameters()
    {
        var random=new Random(34003);
        Guid Id() { var b=new byte[16];random.NextBytes(b);return new(b); }
        for(int iteration=0;iteration<40;++iteration)
        {
            var f=new Fixture(); var seq=f.Project.Sequences.Single();
            var tracks=Enumerable.Range(0,9).Select(t=>
            {
                var kind=(TrackKind)(t%3);
                var clips=kind==TrackKind.Subtitle ? ImmutableArray<Clip>.Empty : Enumerable.Range(0,8).Select(i=>
                {
                    long start=random.Next(0,6)*Fixture.T, duration=random.Next(1,3)*Fixture.T;
                    return new Clip(Id(),kind==TrackKind.Video ? f.MovId : f.WavId,start,Fixture.T,duration,random.Next(3)!=0,
                        new(new(random.Next(-100,100),random.Next(-100,100),.5+random.NextDouble(),.5+random.NextDouble(),random.Next(-180,180)),
                            random.NextDouble(),(BlendMode)random.Next(2)),new(random.NextDouble()*16,random.Next(3)==0));
                }).ToImmutableArray();
                var captions=kind!=TrackKind.Subtitle ? ImmutableArray<Caption>.Empty : Enumerable.Range(0,6)
                    .Select(i=>new Caption(Id(),random.Next(0,6)*Fixture.T,Fixture.T,"caption"+i,random.Next(3)!=0)).ToImmutableArray();
                return new Track(Id(),"track"+t,kind,random.Next(4)!=0,clips,captions);
            }).ToImmutableArray();
            var project=f.Project with {Sequences=[seq with {Tracks=tracks}]};
            var result=TimelineEvaluator.Create(project,seq.Id); Assert.IsTrue(result.Success,string.Join(" / ",result.Diagnostics));
            using var actual=result.Value!; var expected=ManagedTimelineOracle.Create(project,seq.Id).Value!;
            for(int n=0;n<100;++n)
            {
                long tick=n<16 ? (n/2)*Fixture.T+(n%2) : random.NextInt64(8*Fixture.T);
                var a=actual.Evaluate(tick).Value!;var e=expected.Evaluate(tick).Value!;
                CollectionAssert.AreEqual(e.VideoLayers.ToArray(),a.VideoLayers.ToArray());
                CollectionAssert.AreEqual(e.Audio.ToArray(),a.Audio.ToArray());
                CollectionAssert.AreEqual(e.Captions.ToArray(),a.Captions.ToArray());
                long duration=random.NextInt64(1,8*Fixture.T-tick+1);
                CollectionAssert.AreEqual(expected.EvaluateAudioRange(tick,duration).Value.ToArray(),actual.EvaluateAudioRange(tick,duration).Value.ToArray());
            }
        }
    }
    [TestMethod]
    public void NativeLayoutLifetimeAndClipLocalParameterSeamAreExplicit()
    {
        Assert.AreEqual(128,Marshal.SizeOf<NativeEvaluationItem>());Assert.AreEqual(96,Marshal.SizeOf<NativeEvaluationResult>());
        Assert.AreEqual(3d,NativeTimeline.Parameter([],10,3));
        NativeParameterPoint[] points=[new(10,0),new(20,1),new(30,0)];
        Assert.AreEqual(0d,NativeTimeline.Parameter(points,0,7));Assert.AreEqual(.5d,NativeTimeline.Parameter(points,15,7));
        Assert.AreEqual(1d,NativeTimeline.Parameter(points,20,7));Assert.AreEqual(0d,NativeTimeline.Parameter(points,100,7));
        Assert.ThrowsExactly<IOException>(()=>NativeTimeline.Parameter([new(1,0),new(1,1)],1,0));
        var timeline=new NativeTimeline(100,[]);timeline.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(()=>timeline.Evaluate(0));
    }
    [TestMethod]
    public void PlaybackPolicyOwnsSampleClockBoundsAndLatestGeneration()
    {
        using var p=new NativePlayback();var request=p.Request(8*Fixture.T,30000,1001,Fixture.T,true);
        Assert.AreEqual(48000L,request.StartSample);
        long queued=0;
        for(int i=0;i<5;++i){var a=p.Audio(request.Generation,queued);Assert.AreEqual(4800,a.Count);queued+=a.Count;}
        Assert.AreEqual(0,p.Audio(request.Generation,queued).Count);
        Assert.AreEqual(Fixture.T+Fixture.T/10,p.Clock(request.Generation,4800));
        var step=p.Video(request.Generation,4800,0,-1);Assert.IsTrue(step.VideoTick>=Fixture.T);
        Assert.AreEqual(0,p.Video(request.Generation,4800,3,2*Fixture.T).Present);
        Assert.AreEqual(1,p.Video(request.Generation,4800,1,Fixture.T).Present);
        var frozen=p.Request(8*Fixture.T,30,1,Fixture.T+Fixture.T/10,false);
        Assert.IsFalse(p.Accept(request.Generation));Assert.IsTrue(p.Accept(frozen.Generation));
        Assert.AreEqual(frozen.Position,p.Clock(frozen.Generation,99999));
        var end=p.Request(8*Fixture.T,30,1,8*Fixture.T,false);
        Assert.AreEqual(TimelineTime.FrameToTicks(239,new(30,1)),end.RenderTick);Assert.AreEqual(8*Fixture.T,end.Position);
        p.Cancel();Assert.IsFalse(p.Accept(end.Generation));
        foreach(long tick in new[]{0L,1L,Fixture.T,Fixture.T+1,long.MaxValue/100})
            Assert.AreEqual(checked((long)(((System.Numerics.BigInteger)tick*48000+Fixture.T-1)/Fixture.T)),NativePlayback.FirstSample(tick));
    }
}
