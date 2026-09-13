using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;

namespace Kachinco.App;

public partial class MainWindow
{
    private readonly MediaPlayer previewPlayer = new();
    private long previewRevision = -1;
    private Guid? previewSequence;
    private string? previewFile;
    private bool playing;
    private bool clockUpdate;

    private void InitializeProduction()
    {
        CompositionTarget.Rendering += PlaybackRendering;
        previewPlayer.MediaEnded += (_, _) => { playing = false; previewPlayer.Pause(); };
        previewPlayer.MediaFailed += (_, e) => { playing = false; Status.Text = "プレビューの再生に失敗しました: " + e.ErrorException.Message; };
        previewPlayer.MediaOpened += (_, _) =>
        {
            if (previewRevision != session.GetProject().Revision) return;
            previewPlayer.Position = TimeSpan.FromSeconds((double)Timeline.PlayheadTicks / TimelineTime.TicksPerSecond);
            PreviewImage.Source = new DrawingImage(new VideoDrawing
            {
                Player = previewPlayer,
                Rect = new Rect(0, 0, Math.Max(1, previewPlayer.NaturalVideoWidth), Math.Max(1, previewPlayer.NaturalVideoHeight))
            });
            PreviewInfo.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) => { CompositionTarget.Rendering -= PlaybackRendering; InvalidatePreview(); };
    }
    private void InvalidatePreview()
    {
        playing = false; previewPlayer.Close(); PreviewImage.Source = null;
        PreviewInfo.Visibility = Visibility.Visible;
        previewRevision = -1; previewSequence = null;
        if (previewFile is not null) try { File.Delete(previewFile); } catch (IOException) { }
        previewFile = null;
    }
    private void InvalidateChangedPreview()
    {
        if (previewRevision >= 0 && (previewRevision != session.GetProject().Revision || previewSequence != selectedSequenceId)) InvalidatePreview();
    }
    private void PlaybackRendering(object? sender, EventArgs e)
    {
        if (!playing || previewRevision != session.GetProject().Revision) return;
        clockUpdate = true;
        try { Timeline.SetCursorTicks(TimelineTime.SecondsToTicks((decimal)previewPlayer.Position.Ticks / TimeSpan.TicksPerSecond)); }
        finally { clockUpdate = false; }
    }
    private void SeekPreview()
    {
        if (!clockUpdate && previewRevision == session.GetProject().Revision)
            previewPlayer.Position = TimeSpan.FromSeconds((double)Timeline.PlayheadTicks / TimelineTime.TicksPerSecond);
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (previewRevision != session.GetProject().Revision) { Status.Text = "先に「プレビュー準備」を押してください。"; return; }
        if (playing) previewPlayer.Pause(); else previewPlayer.Play();
        playing = !playing;
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { playing = false; previewPlayer.Pause(); Timeline.SetCursorTicks(0); }
    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => StepFrame(1);
    private void StepFrame(int direction)
    {
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        playing = false; previewPlayer.Pause();
        var fps = sequence.Settings.FrameRate;
        long frame = TimelineTime.RoundHalfUp((System.Numerics.BigInteger)Timeline.PlayheadTicks * fps.Numerator,
            (System.Numerics.BigInteger)TimelineTime.TicksPerSecond * fps.Denominator);
        frame = Math.Clamp(frame + direction, 0, Math.Max(0, TimelineTime.FrameCount(sequence.DurationTicks, fps) - 1));
        Timeline.SetCursorTicks(TimelineTime.FrameToTicks(frame, fps));
    }
    private async void PreparePreview_Click(object sender, RoutedEventArgs e)
    {
        InvalidatePreview();
        string path = Path.Combine(Path.GetTempPath(), "kachinco-preview-" + Guid.NewGuid().ToString("N") + ".mp4");
        var result = await RenderOutput(path);
        if (result?.Stage != ExportStage.Completed) return;
        previewFile = path; previewRevision = session.GetProject().Revision; previewSequence = selectedSequenceId;
        previewPlayer.Open(new Uri(path));
        Status.Text = "プレビュー準備完了。再生・シークできます。";
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "MP4 (*.mp4)|*.mp4", DefaultExt = ".mp4", FileName = "kachinco.mp4" };
        if (picker.ShowDialog(this) != true) return;
        await RenderOutput(picker.FileName);
    }
    private async Task<ExportResult?> RenderOutput(string path)
    {
        var snapshot = session.GetProject();
        if (snapshot.Project is null || selectedSequenceId is not { } sequenceId) return null;
        playing = false; previewPlayer.Pause();
        using var token = new CancellationTokenSource();
        var status = new TextBlock { Text = "描画を準備しています…", Margin = new Thickness(16) };
        var cancel = new Button { Content = "キャンセル", Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(status); panel.Children.Add(cancel);
        var progressWindow = new Window { Owner = this, Title = "動画を書き出しています", Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        bool done = false;
        cancel.Click += (_, _) => { token.Cancel(); cancel.IsEnabled = false; };
        progressWindow.Closing += (_, e) => { if (!done) { token.Cancel(); e.Cancel = true; } };
        var progress = new Progress<ExportProgress>(p => status.Text = $"{p.Stage}: {p.FramesCompleted} / {p.TotalFrames} フレーム");
        var decoder = new FfmpegMediaDecoder();
        var service = new SnapshotExportService(new SharedFrameRenderer(decoder, filename, new WindowsCaptionRasterizer(Dispatcher)),
            new SharedAudioRenderer(decoder, filename), new FfmpegEncodingBackend(), new(null), filename);
        busy = true; IsEnabled = false; progressWindow.Show();
        try
        {
            var result = await Task.Run(() => service.ExportAsync(snapshot,
                new(Guid.NewGuid(), sequenceId, path, ExportPreset.YoutubeH264AacMp4, snapshot.Revision), progress, token.Token));
            Status.Text = result.Stage == ExportStage.Completed ? "書き出しが完了しました。" : result.Stage == ExportStage.Cancelled ? "キャンセルしました。" :
                string.Join(" / ", result.Diagnostics.Select(x => x.Message));
            return result;
        }
        finally { done = true; progressWindow.Close(); busy = false; IsEnabled = true; }
    }

    private async void ImportSrt_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = session.GetProject();
        var sequence = snapshot.Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        var picker = new OpenFileDialog { Filter = "SRT (*.srt)|*.srt" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) { Status.Text = "SRTが大きすぎます。"; return; }
            var parsed = SrtCodec.Parse(await File.ReadAllTextAsync(picker.FileName));
            if (!parsed.Success) { ShowErrors(parsed.Diagnostics); return; }
            if (parsed.Value.IsEmpty) return;
            var track = sequence.Tracks.FirstOrDefault(x => x.Kind == TrackKind.Subtitle);
            var trackId = track?.Id ?? Guid.NewGuid();
            var commands = new List<EditCommand>();
            long end = parsed.Value.Max(x => checked(x.StartTicks + x.DurationTicks));
            if (end > sequence.DurationTicks) commands.Add(new SetSequenceDuration(sequence.Id, end));
            if (track is null) commands.Add(new AddTrack(sequence.Id, trackId, "字幕", TrackKind.Subtitle));
            commands.AddRange(parsed.Value.Select(c => new AddCaption(sequence.Id, trackId, c)));
            Show(session.Execute(new([.. commands], snapshot.Revision)), "SRTを読み込みました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
    }
    private async void ExportSrt_Click(object sender, RoutedEventArgs e)
    {
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        var result = SrtCodec.Write(sequence.Tracks.Where(t => t.Enabled).SelectMany(t => t.Captions));
        if (!result.Success) { ShowErrors(result.Diagnostics); return; }
        var picker = new SaveFileDialog { Filter = "SRT (*.srt)|*.srt", DefaultExt = ".srt", FileName = "captions.srt" };
        if (picker.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(picker.FileName, result.Value); Status.Text = "SRTを書き出しました。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = ex.Message; }
    }
    private void EditCaptions_Click(object sender, RoutedEventArgs e)
    {
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        new CaptionEditorWindow(session, sequence.Id) { Owner = this }.ShowDialog();
        Refresh("字幕編集を終了しました。");
    }
}
