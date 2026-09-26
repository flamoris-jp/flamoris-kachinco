using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Flamoris.Logging;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;
using Track = Kachinco.Core.Track;

namespace Kachinco.App;

public partial class MainWindow : Window
{
    private readonly FlamorisLogger logger;
    private readonly EditorSession session = new();
    private readonly ProjectFileStore files;
    private readonly IMediaProbe mediaProbe;
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

    public MainWindow() : this(KachincoLogging.Create().Logger) { }

    public MainWindow(FlamorisLogger logger)
    {
        this.logger = logger;
        files = new(logger);
        mediaProbe = new FfprobeMediaProbe();
        InitializeComponent();
        relink = new(mediaProbe);
        InitializeMcp();
        InitializeProduction();
        BlendBox.ItemsSource = Enum.GetValues<BlendMode>();
        Refresh(EditorText.ImportGuidance);
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        string language = ((MenuItem)sender).Tag?.ToString() == "en" ? "en" : "ja";
        EditorText.Culture = CultureInfo.GetCultureInfo(language == "ja" ? "ja-JP" : "en-US");
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"/Kachinco.App;component/Strings.{language}.xaml", UriKind.Relative) };
        JapaneseMenu.IsChecked = language == "ja"; EnglishMenu.IsChecked = language == "en";
        Refresh();
    }

    private void NewLandscape_Click(object sender, RoutedEventArgs e) => NewProject(SequenceSettings.Landscape);
    private void NewPortrait_Click(object sender, RoutedEventArgs e) => NewProject(SequenceSettings.Portrait);
    private void NewProject(SequenceSettings settings)
    {
        if (!ConfirmDiscard()) return;
        RevokeMcp();
        session.ReplaceProject(null);
        filename = savedJson = null;
        selectedSequenceId = Guid.NewGuid(); selectedMediaId = selectedClipId = null;
        if (Apply("新しいプロジェクトを作成しました。",
            new CreateProject(Guid.NewGuid(), "新しいプロジェクト"),
            new CreateSequence(selectedSequenceId.Value, "シーケンス 1", settings, 8 * TimelineTime.TicksPerSecond),
            new AddTrack(selectedSequenceId.Value, Guid.NewGuid(), "音声 1", TrackKind.Audio),
            new AddTrack(selectedSequenceId.Value, Guid.NewGuid(), "映像 1", TrackKind.Video),
            new AddTrack(selectedSequenceId.Value, Guid.NewGuid(), "字幕 1", TrackKind.Subtitle)))
            logger.Info("project", "Project created", ProjectContext());
    }

    private void AddLandscapeSequence_Click(object sender, RoutedEventArgs e) => AddSequence(SequenceSettings.Landscape);
    private void AddPortraitSequence_Click(object sender, RoutedEventArgs e) => AddSequence(SequenceSettings.Portrait);
    private void AddSequence(SequenceSettings settings)
    {
        var snapshot = session.GetProject();
        var id = Guid.NewGuid();
        var commands = new List<EditCommand>();
        if (snapshot.Project is null) commands.Add(new CreateProject(Guid.NewGuid(), "新しいプロジェクト"));
        commands.Add(new CreateSequence(id, $"シーケンス {(snapshot.Project?.Sequences.Length ?? 0) + 1}", settings, 8 * TimelineTime.TicksPerSecond));
        commands.Add(new AddTrack(id, Guid.NewGuid(), "音声", TrackKind.Audio));
        commands.Add(new AddTrack(id, Guid.NewGuid(), "映像", TrackKind.Video));
        commands.Add(new AddTrack(id, Guid.NewGuid(), "字幕", TrackKind.Subtitle));
        var result = session.Execute(new([.. commands], snapshot.Revision));
        if (result.Success) selectedSequenceId = id;
        Show(result, EditorText.PlacementGuidance);
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
            if (!result.Success) { LogFailure("document.open", "Project open failed", result.Diagnostics); ShowErrors(result.Diagnostics); return; }
            RevokeMcp();
            var opened = session.ReplaceProject(result.Value!, revision);
            if (!opened.Success) { LogFailure("document.open", "Project switch failed", opened.Diagnostics); ShowErrors(opened.Diagnostics); return; }
            filename = dialog.FileName; savedJson = ProjectJson.Serialize(result.Value!).Value;
            selectedSequenceId = selectedMediaId = selectedClipId = null;
            int missing = MediaReferenceResolver.Inspect(result.Value!, filename).Count(x => !x.IsAvailable);
            logger.Info("document.open", "Project opened",
                ProjectContext(new Dictionary<string, object?> { ["missingMediaCount"] = missing }));
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
            if (!result.Success) { LogFailure("document.save", "Project save failed", result.Diagnostics); ShowErrors(result.Diagnostics); return; }
            filename = result.Value; savedJson = ProjectJson.Serialize(snapshot.Project).Value;
            logger.Info("document.save", "Project saved", ProjectContext());
            Refresh("プロジェクトを保存しました。");
        }
        finally { SetBusy(false); }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "MOV / WAV (*.mov;*.wav)|*.mov;*.wav" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var probed = await mediaProbe.ProbeAsync(picker.FileName);
            if (!probed.Success) { LogFailure("media", "Media probe failed", probed.Diagnostics); ShowErrors(probed.Diagnostics); return; }
            var asset = probed.Value!.ToMediaAsset(Guid.NewGuid());
            selectedMediaId = asset.Id; selectedClipId = null;
            var snapshot = session.GetProject();
            var imported = session.Execute(EditorStartup.Import(snapshot, asset, Guid.NewGuid(), "新しいプロジェクト"));
            Show(imported, $"{asset.Name} を読み込みました。");
            if (imported.Success) logger.Info("document", "Media imported", ProjectContext(new Dictionary<string, object?>
            {
                ["mediaAssetId"] = asset.Id,
                ["mediaKind"] = asset.Kind.ToString(),
                ["durationTicks"] = asset.DurationTicks,
            }));
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
        var planned = trackId == Guid.Empty ?
            TimelineEditPlanner.PlaceOnNewTrack(snapshot.Project, sequenceId, mediaId, Guid.NewGuid(), clipId, startTicks, snapshot.Revision) :
            TimelineEditPlanner.Place(snapshot.Project, sequenceId, mediaId, trackId, clipId, startTicks, snapshot.Revision);
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
            if (!prepared.Success) { LogFailure("media", "Media relink preparation failed", prepared.Diagnostics,
                new Dictionary<string, object?> { ["mediaAssetId"] = asset.Id }); ShowErrors(prepared.Diagnostics); return; }
            if (Apply("素材を再リンクしました。", prepared.Value!))
                logger.Info("document", "Media relinked",
                    ProjectContext(new Dictionary<string, object?> { ["mediaAssetId"] = asset.Id }));
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
        if (!editingText && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Space) { Play_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S) { Save_Click(sender, e); e.Handled = true; }
        else if (!editingText && e.Key == Key.Delete) { Timeline.DeleteSelected(); e.Handled = true; }
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
        if (!result.Success)
        {
            LogFailure("command.failure", "Command failed", result.Diagnostics,
                new Dictionary<string, object?> { ["resultRevision"] = result.Revision });
            ShowErrors(result.Diagnostics);
        }
        else Refresh(successMessage);
    }

    private void LogFailure(string category, string message, IEnumerable<Diagnostic> diagnostics,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        var items = diagnostics.ToArray();
        var context = ProjectContext(properties);
        context["diagnosticCodes"] = string.Join(",", items.Select(item => item.Code).Distinct(StringComparer.Ordinal));
        logger.Error(category, message, properties: context);
    }

    private Dictionary<string, object?> ProjectContext(IReadOnlyDictionary<string, object?>? additional = null)
    {
        var snapshot = session.GetProject();
        var properties = new Dictionary<string, object?>
        {
            ["projectId"] = snapshot.Project?.Id,
            ["revision"] = snapshot.Revision,
        };
        if (additional is not null)
            foreach (var pair in additional) properties[pair.Key] = pair.Value;
        return properties;
    }

    private void ShowErrors(IEnumerable<Diagnostic> diagnostics) =>
        Refresh(string.Join("  ", diagnostics.Select(d => d.Code == "CLIP_OVERLAP" ? EditorText.Choose("クリップが重なります。空いている位置か「+ V / + A」の追加行へ配置してください。", d.Message) : $"[{d.Code}] {d.Message}")));

    private void Refresh(string? message = null)
    {
        // All UI and MCP edits/history refresh synchronously on this dispatcher.
        // Invalidate at document loss, before AddSequence/import or human Redo.
        UpdateMcpStatus();
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
        Timeline.SetMediaContext(filename);
        Timeline.LoadProject(project, selectedSequenceId, selectedClipId);
        var guidance = EditorStartup.Guidance(project, sequence);
        MediaGuidance.Text = guidance switch { EditorGuidance.ImportMedia => EditorText.ImportGuidance, EditorGuidance.CreateSequence => EditorText.SequenceGuidance, EditorGuidance.PlaceMedia => EditorText.PlacementGuidance, _ => EditorText.EditGuidance };
        WelcomeText.Text = MediaGuidance.Text;
        WelcomeSequenceChoices.Visibility = guidance == EditorGuidance.CreateSequence ? Visibility.Visible : Visibility.Collapsed;
        WelcomePanel.Visibility = sequence is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewInfo.Visibility = sequence is null || PreviewImage.Source is not null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = project is not null;
        PlayButton.IsEnabled = PrepareButton.IsEnabled = sequence is not null;
        selectedClipId = Timeline.SelectedClipId;
        PreviewInfo.Text = sequence is null ? EditorText.Choose("シーケンスがありません", "No sequence") :
            $"{sequence.Settings.Width} × {sequence.Settings.Height}\n{sequence.Settings.FrameRate.Numerator}/{sequence.Settings.FrameRate.Denominator} fps · {Seconds(sequence.DurationTicks)} s";
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
        RefreshInteractiveContext(); RefreshPlaybackFeedback();
    }

    private void RefreshTimelineStatus()
    {
        RefreshClapperOverlay();
        PlayheadText.Text = $"{EditorText.Choose("再生ヘッド", "Playhead")} {Seconds(Timeline.PlayheadTicks)} s";
        ZoomText.Text = $"{Timeline.PixelsPerSecond:0.#} px/s";
        SplitButton.IsEnabled = selectedClipId is not null;
    }

    private void RefreshInspector()
    {
        var project = session.GetProject().Project;
        var selected = FindSelectedClip(project);
        ClipContext.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        ContextClipName.Text = selected is { } chosen ? project?.Assets.FirstOrDefault(a => a.Id == chosen.Clip.MediaAssetId)?.Name : "";
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
            AssetMetadataText.Text = $"{asset.Kind.ToString().ToUpperInvariant()} · {Seconds(asset.DurationTicks)} s" +
                (asset.SampleRate is { } rate ? $"\n{rate} Hz · {asset.Channels ?? 0} ch" : "") +
                "\n" + (state.IsAvailable ? EditorText.Choose("利用可能", "Available") : EditorText.Choose("見つかりません", "Missing"));
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
            $"{asset.Kind.ToString().ToUpperInvariant()} · {Seconds(asset.DurationTicks)} s",
            availability.IsAvailable ? "●" : "⚠",
            availability.IsAvailable ? Brushes.SeaGreen : Brushes.OrangeRed);
    }
}
