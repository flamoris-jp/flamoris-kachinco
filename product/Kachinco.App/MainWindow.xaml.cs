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

    public MainWindow()
    {
        InitializeComponent();
        relink = new(mediaProbe);
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
        var start = Math.Clamp(Timeline.PlayheadTicks, 0, sequence.DurationTicks);
        long remaining = sequence.DurationTicks - start;
        long duration = Math.Min(row.Asset.DurationTicks, remaining);
        if (duration <= 0) { Refresh("再生ヘッドがシーケンス終端にあります。"); return; }
        var clip = new Clip(Guid.NewGuid(), row.Asset.Id, start, 0, duration, true,
            ClipAppearance.Default, AudioProperties.Default);
        selectedClipId = clip.Id;
        if (track is null)
        {
            var trackId = Guid.NewGuid();
            Apply("素材をタイムラインへ配置しました。",
                new AddTrack(sequenceId, trackId, kind == TrackKind.Video ? "映像" : "音声", kind),
                new InsertClip(sequenceId, trackId, clip));
        }
        else Apply("素材をタイムラインへ配置しました。", new InsertClip(sequenceId, track.Id, clip));
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
        Apply("クリップの設定を変更しました。",
            new TrimClip(sequenceId, clip.Id, start, sourceIn, duration),
            new SetClipProperties(sequenceId, clip.Id, ClipEnabledBox.IsChecked == true, clip.Appearance, clip.Audio));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Timeline.DeleteSelected(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo_Click(sender, e); e.Handled = true; }
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
