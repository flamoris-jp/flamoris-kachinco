using System.Diagnostics;
using System.Text.Json;
using Kachinco.Core;
using Kachinco.Infrastructure;

if (args is ["--pixels", var pixelOutput]) { PreviewPixelBench.Run(pixelOutput); return; }
if (args is ["--gpu-preview", var gpuDirectory, var gpuOutput])
{ await GpuPreviewBench.Run(gpuDirectory, gpuOutput); return; }

// Same driver can be copied into the pre-cutover checkout. Generated public fixtures only.
if (args.Length == 4 && args[0] == "--preview")
{ await PreviewBench.Run(args[1], args[2], args[3]); return; }
if(args.Length!=3)throw new ArgumentException("fixture directory, output JSON, revision label required");
string dir=Path.GetFullPath(args[0]);Directory.CreateDirectory(dir);
string mov=Path.Combine(dir,"source.mov"),wav=Path.Combine(dir,"source.wav");
if(!File.Exists(mov))await Ffmpeg(["-f","lavfi","-i","testsrc2=s=1920x1080:r=30:d=3","-c:v","libx264","-preset","ultrafast","-g","30","-pix_fmt","yuv420p",mov]);
if(!File.Exists(wav))await Ffmpeg(["-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=3","-c:a","pcm_s16le",wav]);
const long T=TimelineTime.TicksPerSecond;
Guid Id(int n)=>Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
var session=new EditorSession();Guid seq=Id(2),video=Id(3),audio=Id(4),vt=Id(5),at=Id(6);
var commands=new List<EditCommand>{new CreateProject(Id(1),"Benchmark"),new CreateSequence(seq,"Sequence",SequenceSettings.Landscape,220*T),
    new RegisterMedia(new(video,"MOV",mov,MediaKind.Mov,3*T)),new RegisterMedia(new(audio,"WAV",wav,MediaKind.Wav,3*T,48000,1)),
    new AddTrack(seq,vt,"V1",TrackKind.Video),new AddTrack(seq,at,"A1",TrackKind.Audio)};
for(int i=0;i<110;++i){commands.Add(new InsertClip(seq,vt,new(Id(100+i),video,i*2*T,0,2*T,true,ClipAppearance.Default,AudioProperties.Default)));
commands.Add(new InsertClip(seq,at,new(Id(300+i),audio,i*2*T,0,2*T,true,ClipAppearance.Default,AudioProperties.Default)));}
Require(session.Execute(new([..commands])).Success,"fixture commands");
var context=PreviewContext.Create(session.GetProject(),seq).Value!;var results=new List<object>();
using var source=new InteractivePreviewSource();
await Frame(0,false);source.Frames.Clear();
var watch=Stopwatch.StartNew();await Frame(111*T,false);results.Add(new{kind="cold_seek",milliseconds=watch.Elapsed.TotalMilliseconds});
watch.Restart();await Frame(111*T,false);results.Add(new{kind="cached_seek",milliseconds=watch.Elapsed.TotalMilliseconds});
source.Frames.Clear();source.Audio.Clear();watch.Restart();
var first=Frame(110*T,true);var prime=Prime(110*48000);await Task.WhenAll(first,prime);
results.Add(new{kind="startup_frame_and_9600_pcm",milliseconds=watch.Elapsed.TotalMilliseconds});
watch.Restart();for(int i=0;i<12;++i)await Frame(112*T-T/5+TimelineTime.FrameToTicks(i,new(30,1)),true);
results.Add(new{kind="boundary_12_frames",milliseconds=watch.Elapsed.TotalMilliseconds});
watch.Restart();for(int i=0;i<180;++i){await Frame(114*T+TimelineTime.FrameToTicks(i,new(30,1)),true);if(i%3==0)await Pcm(114*48000+i*1600);}
results.Add(new{kind="sequential_180_frames_6_seconds",milliseconds=watch.Elapsed.TotalMilliseconds,cache=source.Frames.Statistics});
// One-second real H.264/AAC export uses the same project domain and production renderer.
var project=session.GetProject().Project!;var sequence=project.Sequences.Single();
var shorter=project with{Sequences=[sequence with{DurationTicks=T,Tracks=[..sequence.Tracks.Select(t=>t with{Clips=[t.Clips[0] with{DurationTicks=T}]})]}]};
var exportSession=new EditorSession();Require(exportSession.ReplaceProject(shorter).Success,"export snapshot");
var output=Path.Combine(dir,"export-"+Guid.NewGuid()+".mp4");
using var videoDecoder=new FfmpegForwardDecoder();using var audioDecoder=new FfmpegForwardDecoder();
var exporter=new SnapshotExportService(new SharedFrameRenderer(videoDecoder),new SharedAudioRenderer(audioDecoder),new FfmpegEncodingBackend(),new(null));
watch.Restart();var exported=await exporter.ExportAsync(exportSession.GetProject(),new(Guid.NewGuid(),seq,output,ExportPreset.YoutubeH264AacMp4),null,default);
Require(exported.Stage==ExportStage.Completed,string.Join(" / ",exported.Diagnostics));
results.Add(new{kind="export_30_frames_48000_stereo_samples",milliseconds=watch.Elapsed.TotalMilliseconds,bytes=new FileInfo(output).Length});File.Delete(output);
var evidence=new{revision=args[2],os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),processors=Environment.ProcessorCount,
    framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,quality="Quarter 480x270; export 1920x1080",fixture="1920x1080 30fps 3s testsrc2 MOV + 48kHz sine WAV; 220s sequence with adjacent 2s clips",note="Real codec wall time; no physical playback/perceptual claim; same driver, fixture directory and host for both revisions",results};
File.WriteAllText(args[1],JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(File.ReadAllText(args[1]));
async Task Frame(long tick,bool forward){var r=await source.FrameAsync(context,tick,PreviewQuality.Quarter,forward,default);Require(r.Success,string.Join(" / ",r.Diagnostics));}
async Task Pcm(long sample){var r=await source.AudioAsync(context,sample,4800,default);Require(r.Success,string.Join(" / ",r.Diagnostics));}
async Task Prime(long sample){await Pcm(sample);await Pcm(sample+4800);}
static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
static async Task Ffmpeg(string[] args){var info=new ProcessStartInfo("ffmpeg"){RedirectStandardError=true};foreach(var a in new[]{"-v","error","-nostdin","-threads","1","-filter_threads","1"}.Concat(args))info.ArgumentList.Add(a);using var p=Process.Start(info)!;var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();Require(p.ExitCode==0,await error);}
