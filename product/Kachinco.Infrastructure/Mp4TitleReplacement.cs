using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed class PreparedMp4Title
{
    internal PreparedMp4Title(string source, Mp4Inspection inspection, byte[] preview, long length, DateTime modified)
    { SourcePath = source; Inspection = inspection; PreviewPng = preview; SourceLength = length; SourceModified = modified; }
    public string SourcePath { get; }
    public int Width => Inspection.Width;
    public int Height => Inspection.Height;
    public FrameRate FrameRate => Inspection.Fps;
    public int FrameCount => Inspection.Video.Length;
    public int ReencodedFrames => Inspection.OpeningFrames;
    public int CopiedFrames => FrameCount - ReencodedFrames;
    public bool HasAudio => Inspection.Audio.Length != 0;
    public ReadOnlyMemory<byte> PreviewPng { get; }
    internal Mp4Inspection Inspection { get; }
    internal long SourceLength { get; }
    internal DateTime SourceModified { get; }
}
public sealed record Mp4TitleReplacementResult(string OutputPath, int ReencodedFrames, int CopiedFrames, double ElapsedMilliseconds);
internal sealed record Mp4Packet(long Pts, long Dts, long Duration, long Position, int Size, bool Key, string Hash);
internal sealed record Mp4Inspection(int Width, int Height, FrameRate Fps, int Timescale, long Step, int Reorder,
    int OpeningFrames, Mp4Packet[] Video, Mp4Packet[] Audio, string AudioTimebase);

// Finishes an existing media file; deliberately has no EditorSession or timeline renderer.
public sealed class Mp4TitleReplacement(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] InputOptions = ["-protocol_whitelist", "file,pipe", "-format_whitelist", "mov"];
    public async Task<PreparedMp4Title> PrepareAsync(string sourcePath, string imagePath, CancellationToken token = default)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!Path.GetExtension(source).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an H.264 MP4 file.");
        using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new FileInfo(source);
        long length = info.Length; DateTime modified = info.LastWriteTimeUtc;
        var inspection = await InspectAsync(source, token);
        inspection = inspection with { OpeningFrames = FindOpeningFrames(inspection, sourceLock) };
        var image = await new FfprobeMediaProbe(ffprobe).ProbeAsync(imagePath, token);
        token.ThrowIfCancellationRequested();
        if (!image.Success || image.Value!.Kind != MediaKind.Image || image.Value.Width is not > 0 || image.Value.Height is not > 0 ||
            (long)image.Value.Width * image.Value.Height > 33554432)
            throw new InvalidDataException("Choose a supported PNG/JPEG/WebP image (up to 32 megapixels).");
        var preview = await RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-f", "lavfi", "-i", $"color=c=black:s={inspection.Width}x{inspection.Height}",
            "-protocol_whitelist", "file,pipe", "-format_whitelist", "png_pipe,jpeg_pipe,webp_pipe,image2", "-i", Path.GetFullPath(imagePath),
            "-filter_complex", $"[1:v]scale={inspection.Width}:{inspection.Height}:force_original_aspect_ratio=decrease,format=rgba[image];[0:v][image]overlay=(W-w)/2:(H-h)/2:shortest=1,setsar=1,format=rgb24[v]",
            "-map", "[v]", "-frames:v", "1", "-threads", "1", "-f", "image2pipe", "-c:v", "png", "pipe:1"], 64 * 1024 * 1024, token);
        token.ThrowIfCancellationRequested();
        return new(source, inspection, preview, length, modified);
    }

    public async Task<Mp4TitleReplacementResult> ReplaceAsync(PreparedMp4Title prepared, string outputPath,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        string output = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase) || File.Exists(output) || Directory.Exists(output))
            throw new InvalidDataException("Choose a new MP4 destination. Existing files are never overwritten.");
        using var sourceLock = new FileStream(prepared.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceInfo = new FileInfo(prepared.SourcePath);
        if (sourceInfo.Length != prepared.SourceLength || sourceInfo.LastWriteTimeUtc != prepared.SourceModified)
            throw new InvalidDataException("The source MP4 changed. Select it again before saving.");
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        string work = Path.Combine(Path.GetDirectoryName(output)!, ".kachinco-title-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var m = prepared.Inspection;
            string image = Path.Combine(work, "title.png"), head = Path.Combine(work, "head.mp4"), tail = Path.Combine(work, "tail.mp4");
            string video = head, final = Path.Combine(work, "complete.mp4");
            await File.WriteAllBytesAsync(image, prepared.PreviewPng.ToArray(), token);
            progress?.Report($"encode:{m.OpeningFrames}:{prepared.FrameCount}");
            // The image is already fitted; overlay it only on displayed frame zero.
            await RunAsync(ffmpeg, ["-v", "error", "-xerror", "-nostdin", "-y", .. InputOptions, "-noautorotate", "-i", prepared.SourcePath,
                "-loop", "1", "-framerate", $"{m.Fps.Numerator}/{m.Fps.Denominator}", "-protocol_whitelist", "file,pipe", "-i", image,
                "-filter_complex", "[0:v:0][1:v:0]overlay=enable='eq(n,0)':shortest=1[v]", "-map", "[v]", "-an", "-sn", "-dn",
                "-frames:v", m.OpeningFrames.ToString(Invariant), "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "fast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-bf", (m.Reorder == 0 ? "0" : m.Reorder == 1 ? "1" : "3"),
                "-x264-params", "open-gop=0", "-video_track_timescale", m.Timescale.ToString(Invariant), head], 4096, token);
            // Encoders can report the same reorder depth but use different DTS lead-in
            // (reported NVENC input: three frames; x264 opening: two). Match the actual clock
            // before concatenating; retain every presentation timestamp explicitly.
            var opening = await InspectAsync(head, token);
            long dtsOffset = checked(m.Video[0].Dts - opening.Video[0].Dts);
            if (dtsOffset != 0)
            {
                string aligned = Path.Combine(work, "head-aligned.mp4");
                await RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", .. InputOptions, "-i", head,
                    "-map", "0:v:0", "-c:v", "copy", "-bsf:v", $"setts=pts=PTS:dts=DTS+({dtsOffset.ToString(Invariant)})",
                    "-avoid_negative_ts", "disabled", "-video_track_timescale", m.Timescale.ToString(Invariant), aligned], 4096, token);
                head = aligned; video = aligned;
            }
            if (prepared.CopiedFrames > 0)
            {
                progress?.Report($"copy:{prepared.CopiedFrames}");
                string boundary = ((decimal)m.OpeningFrames * m.Fps.Denominator / m.Fps.Numerator).ToString("0.000000000", Invariant);
                await RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", .. InputOptions, "-ss", boundary, "-i", prepared.SourcePath,
                    "-map", "0:v:0", "-an", "-c:v", "copy", "-avoid_negative_ts", "disabled", tail], 4096, token);
                string list = Path.Combine(work, "join.ffconcat");
                await File.WriteAllTextAsync(list, $"ffconcat version 1.0\nfile '{Path.GetFileName(head)}'\nduration {boundary}\nfile 'tail.mp4'\n", Encoding.ASCII, token);
                video = Path.Combine(work, "joined.mp4");
                await RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "1",
                    "-i", list, "-map", "0:v:0", "-c:v", "copy", "-video_track_timescale", m.Timescale.ToString(Invariant), video], 4096, token);
            }
            await RunAsync(ffmpeg, ["-v", "error", "-nostdin", "-y", .. InputOptions, "-i", video,
                .. InputOptions, "-i", prepared.SourcePath, "-map", "0:v:0", "-map", "1:a:0?", "-map_metadata", "1",
                "-map_chapters", "-1", "-c", "copy", "-video_track_timescale", m.Timescale.ToString(Invariant), "-movflags", "+faststart", final], 4096, token);
            progress?.Report("verify");
            var actual = await InspectAsync(final, token);
            Verify(m, actual);
            // Decode through the splice, without decoding/rendering the copied body of the movie.
            await RunAsync(ffmpeg, ["-v", "error", "-xerror", "-nostdin", .. InputOptions, "-i", final,
                "-map", "0:v:0", "-an", "-frames:v", Math.Min(prepared.FrameCount, m.OpeningFrames + 16).ToString(Invariant), "-f", "null", "-"], 4096, token);
            token.ThrowIfCancellationRequested();
            sourceInfo.Refresh();
            if (sourceInfo.Length != prepared.SourceLength || sourceInfo.LastWriteTimeUtc != prepared.SourceModified)
                throw new InvalidDataException("The source MP4 changed during processing. No output was published.");
            File.Move(final, output); // No overwrite, even if a destination appeared during encoding.
            return new(output, m.OpeningFrames, prepared.CopiedFrames, clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<Mp4Inspection> InspectAsync(string path, CancellationToken token)
    {
        byte[] json = await RunAsync(ffprobe, ["-v", "error", .. InputOptions, "-show_streams", "-show_packets", "-show_data_hash", "sha256",
            "-show_entries", "stream=index,codec_type,codec_name,codec_tag_string,width,height,pix_fmt,field_order,sample_aspect_ratio,has_b_frames,nal_length_size,r_frame_rate,avg_frame_rate,time_base,start_pts,duration_ts,nb_frames:stream_side_data=rotation:packet=stream_index,pts,dts,duration,pos,size,flags,data_hash",
            "-of", "json", path], 64 * 1024 * 1024, token);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Where(s => Text(s, "codec_type") == "video").ToArray();
        var audio = streams.Where(s => Text(s, "codec_type") == "audio").ToArray();
        if (video.Length != 1 || audio.Length > 1 || streams.Length != video.Length + audio.Length || audio.Any(a => Text(a, "codec_name") != "aac"))
            throw new InvalidDataException("Supported MP4: one H.264 video and optional one AAC audio stream, without other tracks.");
        var v = video[0];
        int w = checked((int)Number(v, "width")), h = checked((int)Number(v, "height"));
        if (Text(v, "codec_name") != "h264" || Text(v, "codec_tag_string") != "avc1" || Text(v, "pix_fmt") != "yuv420p" ||
            Text(v, "field_order") is not ("progressive" or "unknown") || Text(v, "sample_aspect_ratio") is not ("1:1" or "N/A" or "") ||
            Text(v, "nal_length_size") != "4" || Number(v, "start_pts") != 0 || w < 2 || h < 2 || w % 2 != 0 || h % 2 != 0 || w > 4096 || h > 4096 ||
            v.TryGetProperty("side_data_list", out var side) && side.EnumerateArray().Any(x => x.TryGetProperty("rotation", out _)))
            throw new InvalidDataException("Supported video: zero-start progressive H.264/AVC, square-pixel yuv420p, no rotation, even dimensions up to 4096.");
        var fpsParts = Text(v, "r_frame_rate").Split('/');
        if (fpsParts.Length != 2 || !int.TryParse(fpsParts[0], out int numerator) || !int.TryParse(fpsParts[1], out int denominator) ||
            numerator <= 0 || denominator <= 0 || (decimal)numerator / denominator > 120)
            throw new InvalidDataException("Unsupported video frame rate.");
        var fps = FrameRate.Create(numerator, denominator);
        var tb = Text(v, "time_base").Split('/');
        if (tb.Length != 2 || tb[0] != "1" || !int.TryParse(tb[1], out int scale) || scale <= 0 || (long)scale * fps.Denominator % fps.Numerator != 0)
            throw new InvalidDataException("Unsupported video timescale.");
        long step = (long)scale * fps.Denominator / fps.Numerator;
        var all = document.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        Mp4Packet[] Packets(int index) => all.Where(p => Number(p, "stream_index") == index).Select(p => new Mp4Packet(Number(p, "pts"), Number(p, "dts"),
            Number(p, "duration"), Number(p, "pos"), checked((int)Number(p, "size")), Text(p, "flags").Contains('K'), Text(p, "data_hash"))).ToArray();
        var vp = Packets(checked((int)Number(v, "index")));
        if (vp.Length == 0 || Number(v, "nb_frames") != vp.Length)
            throw new InvalidDataException("MP4 frame count does not match its video packet count.");
        // MP4 movie/edit durations are commonly rounded to milliseconds (NVENC exports
        // can differ by five ticks at 1/15360). Packet PTS/durations remain authoritative.
        if (Math.Abs(Number(v, "duration_ts") - checked(vp.Length * step)) > Math.Max(1, (scale + 999L) / 1000))
            throw new InvalidDataException("MP4 duration summary is outside the permitted millisecond rounding tolerance.");
        int durationMismatch = Array.FindIndex(vp, p => p.Duration != step);
        if (durationMismatch >= 0)
            throw new InvalidDataException($"Video packet {durationMismatch} has duration {vp[durationMismatch].Duration}; expected {step} ticks.");
        var displayed = vp.OrderBy(p => p.Pts).ToArray();
        if (!displayed.Select(p => p.Pts).SequenceEqual(Enumerable.Range(0, vp.Length).Select(i => i * step)))
            throw new InvalidDataException("Video presentation times contain a missing, duplicate or off-grid frame.");
        if (vp.Zip(vp.Skip(1)).Any(pair => pair.First.Dts >= pair.Second.Dts))
            throw new InvalidDataException("Video decode timestamps are not strictly increasing.");
        int reorder = checked((int)Number(v, "has_b_frames"));
        if (reorder is < 0 or > 2) throw new InvalidDataException("Unsupported decoder reorder delay.");
        return new(w, h, fps, scale, step, reorder, vp.Length, vp, audio.Length == 0 ? [] : Packets(checked((int)Number(audio[0], "index"))),
            audio.Length == 0 ? "" : Text(audio[0], "time_base"));
    }
    private static int FindOpeningFrames(Mp4Inspection m, FileStream source)
    {
        foreach (var packet in m.Video.Where(p => p.Key && p.Pts > m.Reorder * m.Step).OrderBy(p => p.Pts))
        {
            int count = checked((int)(packet.Pts / m.Step));
            int decodeIndex = Array.IndexOf(m.Video, packet);
            if (decodeIndex != count || m.Video.Take(count).Any(p => p.Pts >= packet.Pts) || m.Video.Skip(count).Any(p => p.Pts < packet.Pts)) continue;
            if (IsIdr(source, packet)) return count;
        }
        return m.Video.Length;
    }
    private static bool IsIdr(FileStream source, Mp4Packet packet)
    {
        if (packet.Position < 0 || packet.Size <= 0 || packet.Position > source.Length - packet.Size) throw new InvalidDataException("Invalid MP4 packet extent.");
        long end = packet.Position + packet.Size;
        source.Position = packet.Position;
        Span<byte> prefix = stackalloc byte[5];
        bool idr = false;
        while (source.Position < end)
        {
            if (end - source.Position < 5) return false;
            source.ReadExactly(prefix);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (size < 1 || size - 1 > end - source.Position) return false;
            int kind = prefix[4] & 31;
            if (kind == 5) idr = true;
            else if (kind is 1 or 2 or 3 or 4) return false;
            source.Position += size - 1;
        }
        return idr;
    }
    private static void Verify(Mp4Inspection expected, Mp4Inspection actual)
    {
        if ((expected.Width, expected.Height, expected.Fps, expected.Timescale) != (actual.Width, actual.Height, actual.Fps, actual.Timescale) ||
            !expected.Video.Select(p => (p.Pts, p.Duration)).OrderBy(p => p.Pts).SequenceEqual(actual.Video.Select(p => (p.Pts, p.Duration)).OrderBy(p => p.Pts)) ||
            !expected.Video.Skip(expected.OpeningFrames).Select(p => (p.Pts, p.Dts, p.Duration))
                .SequenceEqual(actual.Video.Skip(expected.OpeningFrames).Select(p => (p.Pts, p.Dts, p.Duration))) ||
            expected.AudioTimebase != actual.AudioTimebase ||
            !expected.Audio.Select(p => (p.Pts, p.Dts, p.Duration, p.Hash)).SequenceEqual(actual.Audio.Select(p => (p.Pts, p.Dts, p.Duration, p.Hash))))
            throw new InvalidDataException("The MP4 join could not preserve frame timing/count or original audio. No output was published.");
    }
    private static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";
    private static long Number(JsonElement e, string key) => long.TryParse(Text(e, key), NumberStyles.Integer, Invariant, out long value)
        ? value : throw new InvalidDataException("Missing MP4 metadata: " + key);

    private static async Task<byte[]> RunAsync(string executable, string[] args, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var info = MediaProcess.StartInfo(executable, args); info.RedirectStandardOutput = true;
        using var process = new Process { StartInfo = info };
        process.Start();
        using var cancel = deadline.Token.Register(() => MediaProcess.Kill(process));
        var error = MediaProcess.DrainErrorAsync(process.StandardError, CancellationToken.None);
        try
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536]; int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("MP4 inspection exceeds the supported size limit.");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(deadline.Token);
            string detail = await error;
            if (process.ExitCode != 0) throw new InvalidDataException("MP4 processing failed: " + detail);
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("MP4 processing exceeded its time limit."); }
        finally
        {
            MediaProcess.Kill(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await error;
        }
    }
}
