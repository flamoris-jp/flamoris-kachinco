using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;

internal static class PreviewPixelBench
{
    public static void Run(string output)
    {
        var results = new List<object>();
        foreach (int divisor in new[] {1,2,4}) {
         int w=1920/divisor,h=1080/divisor;
         byte[] data=new byte[w*h*4]; new Random(53).NextBytes(data);
         var pixels=ImmutableCollectionsMarshal.AsImmutableArray(data);
         byte[] Legacy(){ var bytes=pixels.ToArray(); for(int i=0;i<bytes.Length;i+=4)(bytes[i],bytes[i+2])=(bytes[i+2],bytes[i]); return bytes; }
         byte[] Prepared() { var output = new byte[data.Length]; Kachinco.Native.NativeComposition.RgbaToBgra(data, output); return output; }
         if (!Legacy().SequenceEqual(Prepared()))throw new Exception("Pixel mismatch");
         foreach(var (name,work) in new (string,Func<byte[]> )[]{("legacy_managed_copy_and_swap",Legacy),("native_preparation_off_dispatcher",Prepared)}) {
          for(int i=0;i<20;i++)work();
          var times=new double[100];long alloc=GC.GetAllocatedBytesForCurrentThread();
          for(int i=0;i<times.Length;i++){long start=Stopwatch.GetTimestamp();var bytes=work();times[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;GC.KeepAlive(bytes);}
          long allocated=GC.GetAllocatedBytesForCurrentThread()-alloc;Array.Sort(times);
          results.Add(new{quality=divisor==1?"Full":divisor==2?"Half":"Quarter",name,width=w,height=h,meanMs=times.Average(),p95Ms=times[94],allocatedBytesPerFrame=allocated/100});
         }
        }
        string json = JsonSerializer.Serialize(new{environment=RuntimeInformation.OSDescription,runtime=RuntimeInformation.FrameworkDescription,processors=Environment.ProcessorCount,fixture="Seeded RGBA32 bytes; 20 warmups + 100 measured frames; identical pixels verified",note="Pixel preparation microbenchmark only. No WPF WritePixels, decoder, device playback or physical A/V claim. Both preparations allocate one output; new UI uses its already-prepared immutable output without another full-frame allocation.",results},new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(output, json); Console.WriteLine(json);
    }
}
