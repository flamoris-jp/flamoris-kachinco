using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;
using Track = Kachinco.Core.Track;

namespace Kachinco.App;

public partial class MainWindow : Window
{
    private readonly EditorSession session = new();
    private readonly ProjectFileStore files = new();
    private readonly IMediaProbe mediaProbe = new FfprobeMediaProbe();
    private readonly MediaRelinkService relink;
    private string? savedJson;
    private string? filename;
    private Guid? selectedSequenceId;
    private Guid? selectedMediaId;
    private Guid? selectedClipId;
    private bool refreshing;
    private bool busy;
    private Point mediaDragStart;
    private Guid? draggedMediaId;

    public MainWindow()
    {
        InitializeComponent();
        relink = new(mediaProbe);
        InitializeProduction();
        BlendBox.ItemsSource = Enum.GetValues<BlendMode>();
        Refresh("新規プロジェクトを作成するか、保存済みのプロジェクトを開いてください。");
    }

    private void NewLandscape_Click(object sender, RoutedEventArgs e) => NewProject(SequenceSettings.Landscape);
    private void NewPortrait_Click(object sender, RoutedEventArgs e) => NewProject(SequenceSettings.Portrait);
    private void NewProject(SequenceSettings settings)
    {
        if (!ConfirmDiscard()) return;
        session.ReplaceProject(null);
        filename = savedJson = null;
        selectedSequenceId = Guid.NewGuid(); selectedMediaId = selectedClipId = null;
        Apply("新しいプロジェクトを作成しました。",
            new CreateProject(Guid.NewGuid(), "新しいプロジェクト"),
            new CreateSequence(selectedSequenceId.Value, "シーケンス 1", settings, 60 * TimelineTime.TicksPerSecond),
            new AddTrack(selectedSequenceId.Value, Guid.NewGuid(), "音声 1", TrackKind.Audio),
            new AddTrack(selectedSequenceId.Value, Guid.NewGuid(), "映像 1", TrackKind.Video),
            new AddTrack(selectedSequenceId.Value, Guid.NewGuid(), "字幕 1", TrackKind.Subtitle));
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "Kachinco project (*.fkproj)|*.fkproj" };
        if (dialog.ShowDialog(this) != true) return;
        var revision = session.GetProject().Revision;
        SetBusy(true);
        try
        {
            var result = await files.LoadAsync(dialog.FileName);
            if (!result.Success) { ShowErrors(result.Diagnostics); return; }
            var opened = session.ReplaceProject(result.Value!, revision);
            if (!opened.Success) { ShowErrors(opened.Diagnostics); return; }
            filename = dialog.FileName; savedJson = ProjectJson.Serialize(result.Value!).Value;
            selectedSequenceId = selectedMediaId = selectedClipId = null;
            int missing = MediaReferenceResolver.Inspect(result.Value!, filename).Count(x => !x.IsAvailable);
            Refresh(missing == 0 ? "プロジェクトを開きました。" : $"プロジェクトを開きました。見つからない素材: {missing} 件");
        }
        finally { SetBusy(false); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = session.GetProject();
        if (snapshot.Project is null) { Refresh("先にプロジェクトを作成してください。"); return; }
        var dialog = new SaveFileDialog
        {
            Filter = "Kachinco project (*.fkproj)|*.fkproj", DefaultExt = ".fkproj", AddExtension = true,
            FileName = filename is null ? "project.fkproj" : System.IO.Path.GetFileName(filename)
        };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var result = await files.SaveAsync(dialog.FileName, snapshot.Project);
            if (!result.Success) { ShowErrors(result.Diagnostics); return; }
            filename = result.Value; savedJson = ProjectJson.Serialize(snapshot.Project).Value;
            Refresh("プロジェクトを保存しました。");
        }
        finally { SetBusy(false); }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        if (session.GetProject().Project is null) { Refresh("先にプロジェクトを作成してください。"); return; }
        var picker = new OpenFileDialog { Filter = "MOV / WAV (*.mov;*.wav)|*.mov;*.wav" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var probed = await mediaProbe.ProbeAsync(picker.FileName);
            if (!probed.Success) { ShowErrors(probed.Diagnostics); return; }
            var asset = probed.Value!.ToMediaAsset(Guid.NewGuid());
            selectedMediaId = asset.Id; selectedClipId = null;
            Apply($"{asset.Name} を読み込みました。", new RegisterMedia(asset));
        }
        finally { SetBusy(false); }
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaAssetRow row || selectedSequenceId is not { } sequenceId)
        { Refresh("素材とシーケンスを選択してください。"); return; }
        var project = session.GetProject().Project!;
        var sequence = project.Sequences.First(s => s.Id == sequenceId);
        var kind = row.Asset.Kind == MediaKind.Mov ? TrackKind.Video : TrackKind.Audio;
        var track = sequence.Tracks.FirstOrDefault(x => x.Kind == kind);
        if (track is null) { Refresh("互換トラックがありません。"); return; }
        PlaceMedia(row.Asset.Id, track.Id, Timeline.PlayheadTicks);
    }

    private void PlaceMedia(Guid mediaId, Guid trackId, long startTicks)
    {
        var snapshot = session.GetProject();
        if (snapshot.Project is null || selectedSequenceId is not { } sequenceId) return;
        var clipId = Guid.NewGuid();
        var planned = TimelineEditPlanner.Place(snapshot.Project, sequenceId, mediaId, trackId, clipId, startTicks, snapshot.Revision);
        if (!planned.Success) { ShowErrors(planned.Diagnostics); return; }
        var result = session.Execute(planned.Value!);
        if (result.Success) selectedClipId = clipId;
        Show(result, "素材をタイムラインへ配置しました。");
    }

    private void Media_MouseDown(object sender, MouseButtonEventArgs e)
    {
        mediaDragStart = e.GetPosition(MediaList);
        draggedMediaId = (ItemsControl.ContainerFromElement(MediaList, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext is MediaAssetRow row ? row.Asset.Id : null;
    }
    private void Media_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedMediaId is not { } id) return;
        var point = e.GetPosition(MediaList);
        if (Math.Abs(point.X - mediaDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - mediaDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        draggedMediaId = null;
        DragDrop.DoDragDrop(MediaList, new DataObject(TimelineSurface.MediaDragFormat, id), DragDropEffects.Copy);
    }
    private void Timeline_MediaPlacementRequested(object sender, MediaPlacementEventArgs e) => PlaceMedia(e.MediaId, e.TrackId, e.StartTicks);
    private void Timeline_PlayheadChanged(object? sender, EventArgs e) { RefreshTimelineStatus(); SeekPreview(); }
    private void FitTimeline_Click(object sender, RoutedEventArgs e) { Timeline.FitSequence(); RefreshTimelineStatus(); }
    private void SequenceDuration_Click(object sender, RoutedEventArgs e)
    {
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(x => x.Id == selectedSequenceId);
        if (sequence is null) return;
        var input = new TextBox { Text = Seconds(sequence.DurationTicks), Margin = new Thickness(12) };
        var ok = new Button { Content = "適用", IsDefault = true, Margin = new Thickness(12) };
        var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(ok);
        var dialog = new Window { Owner = this, Title = "シーケンスの長さ（秒）", Width = 320, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        ok.Click += (_, _) => { if (TrySeconds(input.Text, out var ticks) && ticks > 0) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true && TrySeconds(input.Text, out var duration))
            Apply("シーケンスの長さを変更しました。", new SetSequenceDuration(sequence.Id, duration));
    }

    private async void Relink_Click(object sender, RoutedEventArgs e)
    {
        var project = session.GetProject().Project;
        var asset = SelectedAsset(project);
        if (project is null || asset is null) return;
        var extension = asset.Kind == MediaKind.Mov ? "mov" : "wav";
        var picker = new OpenFileDialog { Filter = $"{extension.ToUpperInvariant()} (*.{extension})|*.{extension}" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var prepared = await relink.PrepareAsync(project, asset.Id, picker.FileName);
            if (!prepared.Success) { ShowErrors(prepared.Diagnostics); return; }
            Apply("素材を再リンクしました。", prepared.Value!);
        }
        finally { SetBusy(false); }
    }

    private void AddTrack_Click(object sender,RoutedEventArgs e)
    {
        if(selectedSequenceId is not { } id || sender is not MenuItem menu || !Enum.TryParse<TrackKind>(menu.Tag?.ToString(),out var kind)) return;
        Apply("トラックを追加しました。",new AddTrack(id,Guid.NewGuid(),kind == TrackKind.Video ? "映像" : "音声",kind));
    }
    private void Duplicate_Click(object sender,RoutedEventArgs e)
    {
        var found=FindSelectedClip(session.GetProject().Project); if(found is null || selectedSequenceId is not { } id) return;
        try
        {
            var clip=found.Value.Clip with { Id=Guid.NewGuid(), StartTicks=found.Value.Clip.EndTicks };
            var sequence=session.GetProject().Project!.Sequences.First(s=>s.Id==id);
            var commands=new List<EditCommand>(); if(clip.EndTicks>sequence.DurationTicks) commands.Add(new SetSequenceDuration(id,clip.EndTicks));
            commands.Add(new InsertClip(id,found.Value.Track.Id,clip));
            if(Apply("クリップを複製しました。",commands.ToArray())) { selectedClipId=clip.Id; Refresh(); }
        }
        catch(OverflowException) { Status.Text="時間が大きすぎます。"; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Timeline.DeleteSelected();
    private void Split_Click(object sender, RoutedEventArgs e) => Timeline.SplitSelected();
    private void Undo_Click(object sender, RoutedEventArgs e) => Show(session.Undo(session.GetProject().Revision), "元に戻しました。");
    private void Redo_Click(object sender, RoutedEventArgs e) => Show(session.Redo(session.GetProject().Revision), "やり直しました。");
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { Timeline.ZoomBy(1.25m); RefreshTimelineStatus(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { Timeline.ZoomBy(0.8m); RefreshTimelineStatus(); }
    private void Snapping_Click(object sender, RoutedEventArgs e)
    {
        bool desired = sender switch
        {
            ToggleButton toggle => toggle.IsChecked == true,
            MenuItem item => item.IsChecked,
            _ => !Timeline.SnappingEnabled
        };
        if (desired != Timeline.SnappingEnabled) Timeline.ToggleSnapping();
        SnappingButton.IsChecked = Timeline.SnappingEnabled;
        SnappingMenuItem.IsChecked = Timeline.SnappingEnabled;
        RefreshTimelineStatus();
    }

    private void Sequence_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing) return;
        selectedSequenceId = (SequenceList.SelectedItem as Sequence)?.Id;
        selectedClipId = null;
        Refresh();
    }

    private void Media_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing) return;
        selectedMediaId = (MediaList.SelectedItem as MediaAssetRow)?.Asset.Id;
        if (selectedMediaId is not null)
        {
            selectedClipId = null;
            Timeline.LoadProject(session.GetProject().Project, selectedSequenceId, null);
        }
        RefreshInspector();
    }

    private void Media_DoubleClick(object sender, MouseButtonEventArgs e) => Insert_Click(sender, e);
    private void Timeline_CommandRequested(object sender, TimelineCommandEventArgs e) => Apply("タイムラインを編集しました。", e.Command);
    private void Timeline_ClipSelectionChanged(object sender, TimelineSelectionEventArgs e)
    {
        selectedClipId = e.ClipId;
        if (selectedClipId is not null)
        {
            refreshing = true; MediaList.SelectedItem = null; refreshing = false;
            selectedMediaId = null;
        }
        RefreshInspector(); RefreshTimelineStatus();
    }
    private void Timeline_InteractionFailed(object sender, TimelineDiagnosticsEventArgs e) => ShowErrors(e.Diagnostics);

    private void ApplyInspector_Click(object sender, RoutedEventArgs e)
    {
        var project = session.GetProject().Project;
        var found = FindSelectedClip(project);
        if (project is null || found is null || selectedSequenceId is not { } sequenceId) return;
        if (!TrySeconds(ClipStartBox.Text, out var start) || !TrySeconds(ClipSourceInBox.Text, out var sourceIn) ||
            !TrySeconds(ClipDurationBox.Text, out var duration) || duration <= 0)
        { Refresh("秒数を0以上の数値で入力してください。"); return; }
        var clip = found.Value.Clip;
        if (!double.TryParse(OpacityBox.Text, out var opacity) || !double.TryParse(TransformXBox.Text,out var x) || !double.TryParse(TransformYBox.Text,out var y) ||
            !double.TryParse(ScaleXBox.Text,out var sx) || !double.TryParse(ScaleYBox.Text,out var sy) || !double.TryParse(RotationBox.Text,out var rotation) || !double.TryParse(GainBox.Text,out var gain) || BlendBox.SelectedItem is not BlendMode blend)
        { Status.Text = "合成・変形・音量の数値を確認してください。"; return; }
        Apply("クリップの設定を変更しました。",
            new TrimClip(sequenceId, clip.Id, start, sourceIn, duration),
            new SetClipProperties(sequenceId, clip.Id, ClipEnabledBox.IsChecked == true, new(new(x,y,sx,sy,rotation),opacity,blend),new(gain,MutedBox.IsChecked == true)));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool editingText = Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false } or PasswordBox;
        if (!editingText && e.Key == Key.Delete) { Timeline.DeleteSelected(); e.Handled = true; }
        else if (!editingText && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
        else if (!editingText && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { Open_Click(sender, e); e.Handled = true; }
    }

    private bool Apply(string successMessage, params EditCommand[] commands)
    {
        var result = session.Execute(new([.. commands], session.GetProject().Revision));
        Show(result, successMessage);
        return result.Success;
    }

    private void Show(EditResult result, string successMessage)
    {
        if (!result.Success) ShowErrors(result.Diagnostics);
        else Refresh(successMessage);
    }

    private void ShowErrors(IEnumerable<Diagnostic> diagnostics) =>
        Refresh(string.Join("  ", diagnostics.Select(d => $"[{d.Code}] {d.Message}")));

    private void Refresh(string? message = null)
    {
        refreshing = true;
        var snapshot = session.GetProject();
        InvalidateChangedPreview();
        var project = snapshot.Project;
        var availability = project is null ? new Dictionary<Guid, MediaAvailability>() :
            MediaReferenceResolver.Inspect(project, filename).ToDictionary(x => x.MediaAssetId);
        var rows = project is null ? Array.Empty<MediaAssetRow>() : TimelineQueries.ListMediaAssets(project)
            .Select(asset => MediaAssetRow.Create(asset, availability[asset.Id])).ToArray();
        MediaList.ItemsSource = rows;
        MediaList.SelectedItem = rows.FirstOrDefault(x => x.Asset.Id == selectedMediaId);
        SequenceList.ItemsSource = project is null ? Array.Empty<Sequence>() : TimelineQueries.ListSequences(project);
        var sequence = project?.Sequences.FirstOrDefault(s => s.Id == selectedSequenceId) ?? project?.Sequences.FirstOrDefault();
        selectedSequenceId = sequence?.Id; SequenceList.SelectedItem = sequence;
        Timeline.LoadProject(project, selectedSequenceId, selectedClipId);
        selectedClipId = Timeline.SelectedClipId;
        PreviewInfo.Text = sequence is null ? "シーケンスがありません" :
            $"{sequence.Settings.Width} × {sequence.Settings.Height}\n{sequence.Settings.FrameRate.Numerator}/{sequence.Settings.FrameRate.Denominator} fps · {Seconds(sequence.DurationTicks)} 秒";
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = snapshot.CanUndo;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = snapshot.CanRedo;
        SplitButton.IsEnabled = selectedClipId is not null;
        SnappingButton.IsChecked = Timeline.SnappingEnabled;
        SnappingMenuItem.IsChecked = Timeline.SnappingEnabled;
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0.#} px/s";
        Title = $"{(IsDirty() ? "* " : "")}{project?.Name ?? "FLAMORIS Kachinco"} — Kachinco";
        if (message is not null) Status.Text = message;
        refreshing = false;
        RefreshInspector(); RefreshTimelineStatus();
    }

    private void RefreshTimelineStatus()
    {
        PlayheadText.Text = $"再生ヘッド {Seconds(Timeline.PlayheadTicks)} 秒";
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0.#} px/s";
        SplitButton.IsEnabled = selectedClipId is not null;
    }

    private void RefreshInspector()
    {
        var project = session.GetProject().Project;
        var selected = FindSelectedClip(project);
        InspectorEmpty.Visibility = selected is null && SelectedAsset(project) is null ? Visibility.Visible : Visibility.Collapsed;
        ClipInspector.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        AssetInspector.Visibility = selected is null && SelectedAsset(project) is not null ? Visibility.Visible : Visibility.Collapsed;
        if (selected is { } value && project is not null)
        {
            var asset = project.Assets.First(x => x.Id == value.Clip.MediaAssetId);
            ClipNameText.Text = asset.Name; ClipIdText.Text = value.Clip.Id.ToString();
            ClipStartBox.Text = Seconds(value.Clip.StartTicks);
            ClipSourceInBox.Text = Seconds(value.Clip.SourceInTicks);
            ClipDurationBox.Text = Seconds(value.Clip.DurationTicks);
            ClipEnabledBox.IsChecked = value.Clip.Enabled;
            BlendBox.SelectedItem = value.Clip.Appearance.Blend; OpacityBox.Text = value.Clip.Appearance.Opacity.ToString();
            var transform = value.Clip.Appearance.Transform;
            TransformXBox.Text = transform.X.ToString(); TransformYBox.Text = transform.Y.ToString();
            ScaleXBox.Text = transform.ScaleX.ToString(); ScaleYBox.Text = transform.ScaleY.ToString(); RotationBox.Text = transform.RotationDegrees.ToString();
            GainBox.Text = value.Clip.Audio.Gain.ToString(); MutedBox.IsChecked = value.Clip.Audio.Muted;
        }
        else if (SelectedAsset(project) is { } asset)
        {
            var state = MediaReferenceResolver.Inspect(project!, filename).First(x => x.MediaAssetId == asset.Id);
            AssetNameText.Text = asset.Name; AssetIdText.Text = asset.Id.ToString();
            AssetPathText.Text = state.ResolvedPath ?? asset.SourcePath;
            AssetMetadataText.Text = $"{asset.Kind.ToString().ToUpperInvariant()} · {Seconds(asset.DurationTicks)} 秒" +
                (asset.SampleRate is { } rate ? $"\n{rate} Hz · {asset.Channels ?? 0} ch" : "") +
                (state.IsAvailable ? "\n利用可能" : "\n見つかりません");
        }
    }

    private MediaAsset? SelectedAsset(Project? project)
    {
        if (project is null) return null;
        if (selectedClipId is { } clipId)
        {
            var clip = project.Sequences.SelectMany(x => x.Tracks).SelectMany(x => x.Clips).FirstOrDefault(x => x.Id == clipId);
            if (clip is not null) return project.Assets.FirstOrDefault(x => x.Id == clip.MediaAssetId);
        }
        return project.Assets.FirstOrDefault(x => x.Id == selectedMediaId);
    }

    private (Track Track, Clip Clip)? FindSelectedClip(Project? project)
    {
        if (project is null || selectedSequenceId is not { } sequenceId || selectedClipId is not { } clipId) return null;
        var sequence = project.Sequences.FirstOrDefault(x => x.Id == sequenceId);
        if (sequence is null) return null;
        foreach (var track in sequence.Tracks)
            if (track.Clips.FirstOrDefault(x => x.Id == clipId) is { } clip) return (track, clip);
        return null;
    }

    private bool IsDirty() => session.GetProject().Project is { } p && ProjectJson.Serialize(p).Value != savedJson;
    private bool ConfirmDiscard() => !IsDirty() || MessageBox.Show(this, "未保存の変更を破棄しますか？", "Kachinco", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void Window_Closing(object? sender, CancelEventArgs e) { if (busy || !ConfirmDiscard()) e.Cancel = true; }
    private void SetBusy(bool value) { busy = value; IsEnabled = !value; }
    private static string Seconds(long ticks) => ((decimal)ticks / TimelineTime.TicksPerSecond).ToString("0.###", CultureInfo.CurrentCulture);
    private static bool TrySeconds(string text, out long ticks)
    {
        ticks = 0;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var seconds) &&
            !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out seconds)) return false;
        try { ticks = TimelineTime.SecondsToTicks(seconds); return true; }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException) { return false; }
    }

    private sealed record MediaAssetRow(MediaAsset Asset, string Name, string Details, string State, Brush StateBrush)
    {
        public static MediaAssetRow Create(MediaAsset asset, MediaAvailability availability) => new(asset, asset.Name,
            $"{asset.Kind.ToString().ToUpperInvariant()} · {Seconds(asset.DurationTicks)} 秒",
            availability.IsAvailable ? "●" : "⚠",
            availability.IsAvailable ? Brushes.SeaGreen : Brushes.OrangeRed);
    }
}
