using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private InteractivePreview playback = null!;
    private InteractivePreviewSource previewSource = null!;
    private PreviewContext? previewContext;
    private RenderedVideoFrame? displayedFrame;
    private System.Windows.Media.Imaging.WriteableBitmap? previewBitmap;
    private bool clockUpdate;
    private string? previewFailureSignature;

    private void InitializeProduction()
    {
        previewSource = new(new WindowsCaptionRasterizer(Dispatcher), logger: logger, backendPreference: PreviewBackendPreference.Auto);
        playback = new(previewSource, () => new WindowsPreviewAudioOutput(), logger);
        InitializeMonitoring();
        playback.Changed += (_, _) => RefreshPlaybackFeedback();
        CompositionTarget.Rendering += PlaybackRendering;
        Closed += async (_, _) =>
        {
            ShutdownMcp();
            CompositionTarget.Rendering -= PlaybackRendering;
            Timeline.DisposeVisualizations();
            playback.Dispose();
            await playback.Completion;
            previewSource.Dispose();
            session.Dispose();
        };
    }
    private void RefreshInteractiveContext()
    {
        var snapshot = session.GetProject();
        if (snapshot.Project is null || selectedSequenceId is not { } id)
        { previewContext = null; playback.SetContext(null); return; }
        if (previewContext?.Snapshot.Revision == snapshot.Revision && previewContext.Sequence.Id == id && previewContext.ProjectPath == filename) return;
        var created = PreviewContext.Create(snapshot, id, filename);
        if (!created.Success)
        {
            LogFailure("preview", "Preview context creation failed", created.Diagnostics,
                new Dictionary<string, object?> { ["sequenceId"] = id });
            playback.SetContext(null); Status.Text = string.Join(" / ", created.Diagnostics.Select(d => d.Message)); return;
        }
        previewContext = created.Value; playback.SetContext(previewContext);
    }
    private void PlaybackRendering(object? sender, EventArgs e)
    {
        if (playback.State is not (InteractivePreviewState.Playing or InteractivePreviewState.Buffering)) return;
        clockUpdate = true;
        try { Timeline.SetPlaybackCursorTicks(playback.ReadPositionTicks()); }
        finally { clockUpdate = false; }
    }
    private void SeekPreview()
    {
        if (!clockUpdate && !refreshing && previewContext is not null) playback.Scrub(Timeline.PlayheadTicks);
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSequenceId is null) { Status.Text = EditorText.SequenceGuidance; return; }
        if (playback.State is InteractivePreviewState.Playing or InteractivePreviewState.Buffering) playback.Pause();
        else { Timeline.BeginPlaybackFollow(); playback.Play(); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        playback.Stop();
        clockUpdate = true;
        try { Timeline.SetCursorTicks(playback.PositionTicks); } finally { clockUpdate = false; }
    }
    private void GoToStart_Click(object sender, RoutedEventArgs e)
    {
        SeekTo(0);
        Timeline.ShowStart();
    }
    private void SeekTo(long tick)
    {
        clockUpdate = true;
        try { Timeline.SetCursorTicks(tick); } finally { clockUpdate = false; }
        playback.Scrub(tick);
    }
    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => StepFrame(1);
    private void StepFrame(int direction)
    {
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        playback.Pause();
        var fps = sequence.Settings.FrameRate;
        long frame = TimelineTime.TicksToFrame(playback.ReadPositionTicks(), fps);
        frame = Math.Clamp(frame + direction, 0, Math.Max(0, TimelineTime.FrameCount(sequence.DurationTicks, fps) - 1));
        SeekTo(TimelineTime.FrameToTicks(frame, fps));
    }
    private void PreparePreview_Click(object sender, RoutedEventArgs e) => playback.Scrub(Timeline.PlayheadTicks);
    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (playback is null) return;
        playback.SetQuality(PreviewQualityBox.SelectedIndex switch { 0 => PreviewQuality.Full, 2 => PreviewQuality.Quarter, _ => PreviewQuality.Half });
    }
    private void PreviewBackend_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (playback is null) return;
        playback.SetBackendPreference(PreviewBackendBox.SelectedIndex switch
        { 1 => PreviewBackendPreference.Cpu, 2 => PreviewBackendPreference.D3D11, _ => PreviewBackendPreference.Auto });
    }
    private void RefreshPlaybackFeedback()
    {
        if (playback is null || PlaybackStatus is null) return;
        if (playback.State == InteractivePreviewState.Failed)
        {
            var signature = $"{selectedSequenceId}:{session.GetProject().Revision}:{Timeline.PlayheadTicks}:{playback.Error}";
            if (!string.Equals(signature, previewFailureSignature, StringComparison.Ordinal))
            {
                previewFailureSignature = signature;
                logger.Error("preview", "Interactive preview failed", properties: ProjectContext(new Dictionary<string, object?>
                {
                    ["sequenceId"] = selectedSequenceId,
                    ["playheadTicks"] = Timeline.PlayheadTicks,
                    ["quality"] = playback.Quality.ToString(),
                    ["reason"] = playback.Error,
                }));
            }
        }
        else previewFailureSignature = null;
        string label = playback.State switch
        {
            InteractivePreviewState.Scrubbing => EditorText.Choose("フレームを取得中", "Scrubbing"),
            InteractivePreviewState.Buffering => EditorText.Choose("再生バッファを準備中", "Buffering"),
            InteractivePreviewState.Playing => EditorText.Playing, InteractivePreviewState.Paused => EditorText.Paused,
            InteractivePreviewState.Failed => EditorText.Failed, _ => EditorText.Stopped
        };
        PlaybackStatus.Text = label;
        var backend = previewSource.BackendDiagnostics;
        PreviewBackendStatus.Text = $"{backend.Active} · {backend.AllocatedBytes / (1024d * 1024d):F0} MiB";
        var decode = previewSource.DecodeDiagnostics;
        PreviewBackendStatus.ToolTip = EditorText.Choose("プレビュー合成: ", "Preview composition: ") + backend.Active +
            EditorText.Choose("\nCPUメモリのRGBAをWPFで表示します。D3D11合成は読戻しを使用します。", "\nWPF presents CPU RGBA. D3D11 composition uses readback.") +
            (backend.FallbackReason is { } reason ? "\n" + reason : "") +
            (decode is null ? "" : "\nDecode: " + decode.ActiveBackend + (decode.FallbackReason is { } fallback ? "\n" + fallback : ""));
        PlaybackDetail.Text = playback.Error is { } error ? label + "\n" + error : label;
        if (playback.DroppedVideoFrames > 0) PlaybackStatus.Text += EditorText.Choose($" · 映像スキップ {playback.DroppedVideoFrames}（1/4で軽減）", $" · Video skipped {playback.DroppedVideoFrames} (try 1/4)");
        PlaybackOverlay.Visibility = playback.State is InteractivePreviewState.Scrubbing or InteractivePreviewState.Buffering or InteractivePreviewState.Failed ? Visibility.Visible : Visibility.Collapsed;
        PlaybackProgress.Visibility = playback.State is InteractivePreviewState.Scrubbing or InteractivePreviewState.Buffering ? Visibility.Visible : Visibility.Collapsed;
        PlaybackProgress.IsIndeterminate = true;
        PlayButton.Content = playback.State is InteractivePreviewState.Playing or InteractivePreviewState.Buffering ? "Ⅱ" : "▶";
        PlayButton.IsEnabled = PrepareButton.IsEnabled = selectedSequenceId is not null;
        if (!ReferenceEquals(displayedFrame, playback.Frame))
        {
            displayedFrame = playback.Frame;
            if (displayedFrame is null) PreviewImage.Source = null;
            else
            {
                var f = displayedFrame;
                if (previewBitmap is null || previewBitmap.PixelWidth != f.Width || previewBitmap.PixelHeight != f.Height)
                    previewBitmap = new(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
                long started = Stopwatch.GetTimestamp();
                var bytes = ImmutableCollectionsMarshal.AsArray(playback.Presentation!.Bgra8)!;
                previewBitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), bytes, f.Width * 4, 0);
                PreviewImage.Source = previewBitmap;
                playback.RecordPresentation(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        PreviewInfo.Visibility = PreviewImage.Source is null && selectedSequenceId is not null && playback.State != InteractivePreviewState.Failed ? Visibility.Visible : Visibility.Collapsed;
        if (playback.State == InteractivePreviewState.Stopped && previewContext is not null)
        {
            clockUpdate = true;
            try { Timeline.SetCursorTicks(playback.PositionTicks); } finally { clockUpdate = false; }
        }
    }
    private PreviewBackendPreference exportComposition = PreviewBackendPreference.Auto;
    private VideoEncoderPreference exportEncoder = VideoEncoderPreference.Auto;
    private void ExportComposition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem selected) return;
        exportComposition = Enum.Parse<PreviewBackendPreference>((string)selected.Tag);
        foreach (MenuItem item in ExportCompositionMenu.Items) item.IsChecked = item == selected;
    }
    private void ExportEncoder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem selected) return;
        exportEncoder = Enum.Parse<VideoEncoderPreference>((string)selected.Tag);
        foreach (MenuItem item in ExportEncoderMenu.Items) item.IsChecked = item == selected;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
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
        var status = new TextBlock { Text = "描画を準備しています…", Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Content = "キャンセル", Margin = new Thickness(16) };
        var indicator = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(16, 0, 16, 8) };
        var elapsed = new TextBlock { Margin = new Thickness(16, 0, 16, 0) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => elapsed.Text = EditorText.Choose("経過時間: ", "Elapsed: ") + clock.Elapsed.ToString(@"hh\:mm\:ss");
        var panel = new StackPanel(); panel.Children.Add(status); panel.Children.Add(indicator); panel.Children.Add(elapsed); panel.Children.Add(cancel);
        var progressWindow = new Window { Owner = this, Title = "動画を書き出しています", Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        bool done = false;
        cancel.Click += (_, _) =>
        {
            if (done) { progressWindow.Close(); return; }
            token.Cancel(); cancel.IsEnabled = false;
            status.Text = EditorText.Choose("キャンセルしています…", "Cancelling…");
        };
        progressWindow.Closing += (_, e) => { if (!done) { token.Cancel(); e.Cancel = true; } };
        var progress = new Progress<ExportProgress>(p =>
        {
            if (done || token.IsCancellationRequested) return;
            status.Text = p.Detail switch
            {
                "audio" => EditorText.Choose("音声を処理しています…", "Processing audio…"),
                "mux" => EditorText.Choose("音声をエンコードし、映像との結合・MP4の最適化を行っています…", "Encoding audio, combining video and optimizing MP4…"),
                "save" => EditorText.Choose("ファイルを保存しています…", "Saving file…"),
                _ => p.Stage switch
                {
                    ExportStage.Planning => EditorText.Choose("描画を準備しています…", "Preparing render…"),
                    ExportStage.Completed => EditorText.Choose("保存を確認しています…", "Checking saved output…"),
                    _ => EditorText.Choose("映像を処理しています: ", "Processing video: ") + $"{p.FramesCompleted} / {p.TotalFrames} " + EditorText.Choose("フレーム", "frames")
                }
            };
        });
        using var decoder = new FfmpegMediaDecoder();
        using var video = new ExportFrameRenderer(decoder, filename, new WindowsCaptionRasterizer(Dispatcher), exportComposition, logger);
        var service = new SnapshotExportService(video,
            new SharedAudioRenderer(decoder, filename), new FfmpegEncodingBackend(), new(null, exportEncoder), filename);
        busy = true; IsEnabled = false; progressWindow.Show(); timer.Start();
        try
        {
            var result = await Task.Run(() => service.ExportAsync(snapshot,
                new(Guid.NewGuid(), sequenceId, path, ExportPreset.YoutubeH264AacMp4, snapshot.Revision), progress, token.Token));
            if (result.Stage == ExportStage.Failed)
                LogFailure("render", "Video export failed", result.Diagnostics,
                    new Dictionary<string, object?> { ["sequenceId"] = sequenceId, ["stage"] = result.Stage.ToString() });
            else if (result.Stage == ExportStage.Completed)
                logger.Info("render", "Video export completed", ProjectContext(new Dictionary<string, object?>
                {
                    ["sequenceId"] = sequenceId,
                    ["stage"] = result.Stage.ToString(),
                    ["acceleration"] = result.Metrics,
                    ["diagnostics"] = result.Diagnostics,
                }));
            Status.Text = result.Stage == ExportStage.Completed ? $"書き出しが完了しました。 {result.Metrics?.VideoEncoder} / D3D11: {result.Metrics?.Composition?.GpuFrames ?? 0} frames" : result.Stage == ExportStage.Cancelled ? "キャンセルしました。" :
                string.Join(" / ", result.Diagnostics.Select(x => x.Message));
            status.Text = result.Stage == ExportStage.Completed
                ? EditorText.Choose("動画の書き出しが完了しました。\n", "Video export completed.\n") + result.OutputPath
                : result.Stage == ExportStage.Cancelled ? EditorText.Choose("キャンセルしました。", "Export cancelled.")
                : EditorText.Choose("書き出しに失敗しました。\n", "Export failed.\n") + string.Join(" / ", result.Diagnostics.Select(x => x.Message));
            return result;
        }
        catch (Exception exception)
        {
            logger.Error("render", "Video export failed unexpectedly", exception, ProjectContext(new Dictionary<string, object?>
            {
                ["sequenceId"] = sequenceId,
            }));
            status.Text = Status.Text = EditorText.Choose("書き出しに失敗しました。ログを確認してください。", "Export failed. Check the log.");
            return null;
        }
        finally
        {
            done = true; timer.Stop(); clock.Stop();
            progressWindow.Title = EditorText.Choose("書き出し結果", "Export result");
            elapsed.Text = EditorText.Choose("経過時間: ", "Elapsed: ") + clock.Elapsed.ToString(@"hh\:mm\:ss");
            indicator.IsIndeterminate = false; indicator.Visibility = Visibility.Collapsed;
            cancel.Content = EditorText.Choose("閉じる", "Close"); cancel.IsEnabled = true;
            busy = false; IsEnabled = true;
            // Keep the final result and saved path visible until acknowledged.
            // This is modeless: the editor and automated callers remain usable.
        }
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
        using var operation = BeginHumanOperation();
        if (selectedSequenceId is not { } id) return;
        new AuthoringWindow(session, id, Timeline.PlayheadTicks) { Owner = this }.ShowDialog();
        Refresh("Clapper / Recipe 編集を終了しました。");
    }

    private async void ImportSrt_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("document", "SRT import failed", properties: ProjectContext(new Dictionary<string, object?>
            {
                ["sequenceId"] = sequence.Id,
                ["diagnosticCode"] = "SRT_READ_FAILED",
                ["errorType"] = ex.GetType().Name,
            }));
            Status.Text = ex.Message;
        }
    }
    private async void ExportSrt_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        var result = SrtCodec.Write(sequence.Tracks.Where(t => t.Enabled).SelectMany(t => t.Captions));
        if (!result.Success) { ShowErrors(result.Diagnostics); return; }
        var picker = new SaveFileDialog { Filter = "SRT (*.srt)|*.srt", DefaultExt = ".srt", FileName = "captions.srt" };
        if (picker.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(picker.FileName, result.Value); Status.Text = "SRTを書き出しました。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("document", "SRT export failed", properties: ProjectContext(new Dictionary<string, object?>
            {
                ["sequenceId"] = sequence.Id,
                ["diagnosticCode"] = "SRT_WRITE_FAILED",
                ["errorType"] = ex.GetType().Name,
            }));
            Status.Text = ex.Message;
        }
    }
    private void EditCaptions_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        new CaptionEditorWindow(session, sequence.Id, () => Refresh()) { Owner = this }.ShowDialog();
        Refresh("字幕編集を終了しました。");
    }
}
