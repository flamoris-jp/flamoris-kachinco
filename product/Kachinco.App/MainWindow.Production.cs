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
    private PreviewPlayback playback = null!;
    private bool clockUpdate;

    private void InitializeProduction()
    {
        playback = new(() => new WindowsPreviewPlayer(), RenderPreviewAsync);
        playback.Changed += (_, _) => RefreshPlaybackFeedback();
        CompositionTarget.Rendering += PlaybackRendering;
        Closed += (_, _) =>
        {
            mcpLifetime?.Cancel();
            foreach (var job in exportJobs.Values) job.Cancellation.Cancel();
            CompositionTarget.Rendering -= PlaybackRendering;
            Timeline.DisposeVisualizations();
            playback.Dispose();
        };
    }
    private void InvalidatePreview() => playback.Invalidate();
    private void InvalidateChangedPreview()
    {
        if (playback.Revision >= 0 && !playback.Matches(session.GetProject().Revision, selectedSequenceId)) playback.Invalidate();
    }
    private void PlaybackRendering(object? sender, EventArgs e)
    {
        if (playback.State != PreviewState.Playing || !playback.Matches(session.GetProject().Revision, selectedSequenceId)) return;
        clockUpdate = true;
        try { Timeline.SetCursorTicks(playback.ReadPositionTicks()); }
        finally { clockUpdate = false; }
    }
    private void SeekPreview()
    {
        if (!clockUpdate && playback.Matches(session.GetProject().Revision, selectedSequenceId)) playback.Seek(Timeline.PlayheadTicks);
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (playback.IsPreparing) return;
        var snapshot = session.GetProject();
        if (selectedSequenceId is not { } sequenceId) { Status.Text = EditorText.SequenceGuidance; return; }
        if (playback.Matches(snapshot.Revision, sequenceId) && playback.Player is not null) playback.Toggle();
        else await playback.PrepareAsync(snapshot, sequenceId, Timeline.PlayheadTicks, true);
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { playback.Stop(); Timeline.SetCursorTicks(0); }
    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => StepFrame(1);
    private void StepFrame(int direction)
    {
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null || playback.IsPreparing) return;
        playback.Pause();
        var fps = sequence.Settings.FrameRate;
        long frame = TimelineTime.RoundHalfUp((System.Numerics.BigInteger)Timeline.PlayheadTicks * fps.Numerator,
            (System.Numerics.BigInteger)TimelineTime.TicksPerSecond * fps.Denominator);
        frame = Math.Clamp(frame + direction, 0, Math.Max(0, TimelineTime.FrameCount(sequence.DurationTicks, fps) - 1));
        Timeline.SetCursorTicks(TimelineTime.FrameToTicks(frame, fps));
    }
    private async void PreparePreview_Click(object sender, RoutedEventArgs e)
    {
        if (playback.IsPreparing || selectedSequenceId is not { } sequenceId) return;
        await playback.PrepareAsync(session.GetProject(), sequenceId, Timeline.PlayheadTicks, false);
    }
    private Task<ExportResult> RenderPreviewAsync(ProjectSnapshot snapshot, Guid sequenceId, string path,
        IProgress<ExportProgress> progress, CancellationToken token)
    {
        // Resolve paths against the captured project location. UI remains responsive/cancellable.
        var projectPath = filename;
        var decoder = new FfmpegMediaDecoder();
        var service = new SnapshotExportService(new SharedFrameRenderer(decoder, projectPath, new WindowsCaptionRasterizer(Dispatcher)),
            new SharedAudioRenderer(decoder, projectPath), new FfmpegEncodingBackend(), new(null), projectPath);
        return Task.Run(() => service.ExportAsync(snapshot,
            new(Guid.NewGuid(), sequenceId, path, ExportPreset.YoutubeH264AacMp4, snapshot.Revision), progress, token), token);
    }
    private void RefreshPlaybackFeedback()
    {
        if (playback is null || PlaybackStatus is null) return;
        string label = playback.State switch
        {
            PreviewState.Preparing => EditorText.Preparing, PreviewState.Rendering => EditorText.Rendering,
            PreviewState.Playing => EditorText.Playing, PreviewState.Paused => EditorText.Paused,
            PreviewState.Failed => EditorText.Failed, _ => EditorText.Stopped
        };
        PlaybackStatus.Text = label;
        PlaybackDetail.Text = playback.Error is { } error ? label + "\n" + error : label;
        if (playback.Progress is { } progress)
            PlaybackDetail.Text += progress.Stage == ExportStage.Encoding ? EditorText.Choose(" — 音声・映像をまとめています", " — Encoding audio and video") :
                EditorText.Choose($" — 映像 {progress.FramesCompleted}/{progress.TotalFrames} フレーム · 音声 {progress.AudioSamplesCompleted / 48000m:0.0} 秒",
                    $" — Video {progress.FramesCompleted}/{progress.TotalFrames} frames · Audio {progress.AudioSamplesCompleted / 48000m:0.0} s");
        PlaybackOverlay.Visibility = playback.IsPreparing || playback.State == PreviewState.Failed ? Visibility.Visible : Visibility.Collapsed;
        PlaybackProgress.Visibility = playback.IsPreparing ? Visibility.Visible : Visibility.Collapsed;
        PlaybackProgress.IsIndeterminate = playback.Progress is not { TotalFrames: > 0 };
        if (playback.Progress is { TotalFrames: > 0 } p) PlaybackProgress.Value = 100d * p.FramesCompleted / p.TotalFrames;
        PlayButton.Content = playback.State == PreviewState.Playing ? "Ⅱ" : "▶";
        PlayButton.IsEnabled = PrepareButton.IsEnabled = selectedSequenceId is not null && !playback.IsPreparing;
        PreviewImage.Source = (playback.Player as WindowsPreviewPlayer)?.Image;
        PreviewInfo.Visibility = PreviewImage.Source is null && selectedSequenceId is not null ? Visibility.Visible : Visibility.Collapsed;
        if (playback.State == PreviewState.Stopped && playback.Player is not null)
        {
            clockUpdate = true;
            try { Timeline.SetCursorTicks(playback.ReadPositionTicks()); }
            finally { clockUpdate = false; }
        }
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
        playback.Pause();
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

    private void RefreshClapperOverlay()
    {
        if (ClapperCanvas is null) return;
        ClapperCanvas.Children.Clear();
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(s=>s.Id==selectedSequenceId);
        if(sequence is null) return;
        ClapperCanvas.Width=sequence.Settings.Width; ClapperCanvas.Height=sequence.Settings.Height;
        foreach(var clapper in sequence.Clappers.Where(c=>c.Geometry is not null && TimelineTime.Contains(c.StartTicks,c.DurationTicks,Timeline.PlayheadTicks)))
        {
            var g=clapper.Geometry!;
            System.Windows.Shapes.Shape shape = g.Kind == ClapperGeometryKind.Rectangle ?
                new System.Windows.Shapes.Rectangle { Width=g.Width, Height=g.Height } : new System.Windows.Shapes.Ellipse { Width=12, Height=12 };
            shape.Stroke=Brushes.Gold;shape.StrokeThickness=3;
            Canvas.SetLeft(shape,g.X);Canvas.SetTop(shape,g.Y);ClapperCanvas.Children.Add(shape);
            var label=new TextBlock{Text=clapper.Name,Foreground=Brushes.Gold,FontSize=28,Background=Brushes.Black};
            Canvas.SetLeft(label,g.X);Canvas.SetTop(label,Math.Max(0,g.Y-36));ClapperCanvas.Children.Add(label);
        }
    }

    private void Authoring_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSequenceId is not { } id) return;
        new AuthoringWindow(session, id, Timeline.PlayheadTicks) { Owner = this }.ShowDialog();
        Refresh("Clapper / Recipe 編集を終了しました。");
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
