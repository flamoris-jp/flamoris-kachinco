using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;

namespace Kachinco.App;

public partial class MainWindow : Window
{
    private readonly EditorSession session = new();
    private readonly ProjectFileStore files = new();
    private string? savedJson;
    private string? filename;
    private Guid? selectedSequenceId;
    private bool refreshing;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        Refresh("新規プロジェクトを作成するか、保存済みのプロジェクトを開いてください。");
    }

    private void NewLandscape_Click(object sender, RoutedEventArgs e) => NewProject(SequenceSettings.Landscape);
    private void NewPortrait_Click(object sender, RoutedEventArgs e) => NewProject(SequenceSettings.Portrait);
    private void NewProject(SequenceSettings settings)
    {
        if (!ConfirmDiscard()) return;
        session.ReplaceProject(null);
        filename = savedJson = null;
        selectedSequenceId = Guid.NewGuid();
        Apply(new CreateProject(Guid.NewGuid(), "新しいプロジェクト"),
            new CreateSequence(selectedSequenceId.Value, "シーケンス 1", settings, 60 * TimelineTime.TicksPerSecond));
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
            selectedSequenceId = null;
            Refresh("プロジェクトを開きました。");
        }
        finally { SetBusy(false); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = session.GetProject();
        if (snapshot.Project is null) { Refresh("先にプロジェクトを作成してください。"); return; }
        var dialog = new SaveFileDialog { Filter = "Kachinco project (*.fkproj)|*.fkproj", DefaultExt = ".fkproj", AddExtension = true,
            FileName = filename is null ? "project.fkproj" : System.IO.Path.GetFileName(filename) };
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

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        if (session.GetProject().Project is null) { Refresh("先にプロジェクトを作成してください。"); return; }
        var picker = new OpenFileDialog { Filter = "MOV / WAV (*.mov;*.wav)|*.mov;*.wav" };
        if (picker.ShowDialog(this) != true) return;
        var duration = new DurationDialog { Owner = this };
        if (duration.ShowDialog() != true) return;
        var kind = System.IO.Path.GetExtension(picker.FileName).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? MediaKind.Mov : MediaKind.Wav;
        Apply(new RegisterMedia(new(Guid.NewGuid(), System.IO.Path.GetFileName(picker.FileName), picker.FileName, kind, duration.DurationTicks)));
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaAsset asset || selectedSequenceId is not { } sequenceId)
        { Refresh("素材とシーケンスを選択してください。"); return; }
        var snapshot = session.GetProject();
        var sequence = snapshot.Project!.Sequences.First(s => s.Id == sequenceId);
        Guid trackId = Guid.NewGuid();
        var kind = asset.Kind == MediaKind.Mov ? TrackKind.Video : TrackKind.Audio;
        // A new visible track avoids guessing an insertion target. Core still validates the batch.
        Apply(new AddTrack(sequenceId, trackId, $"{(kind == TrackKind.Video ? "映像" : "音声")} {sequence.Tracks.Length + 1}", kind),
            new InsertClip(sequenceId, trackId, new(Guid.NewGuid(), asset.Id, 0, 0,
                Math.Min(asset.DurationTicks, sequence.DurationTicks), true, ClipAppearance.Default, AudioProperties.Default)));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSequenceId is { } sequenceId && TimelineGrid.SelectedItem is TimelineRow { IsCaption: false } row)
            Apply(new DeleteClip(sequenceId, row.Id));
    }
    private void Undo_Click(object sender, RoutedEventArgs e) => Show(session.Undo(session.GetProject().Revision));
    private void Redo_Click(object sender, RoutedEventArgs e) => Show(session.Redo(session.GetProject().Revision));
    private void Sequence_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing) return;
        selectedSequenceId = (SequenceList.SelectedItem as Sequence)?.Id;
        Refresh();
    }
    private void Apply(params EditCommand[] commands) => Show(session.Execute(new([.. commands], session.GetProject().Revision)));
    private void Show(EditResult result)
    {
        if (!result.Success) ShowErrors(result.Diagnostics);
        else Refresh("編集を反映しました。");
    }
    private void ShowErrors(IEnumerable<Diagnostic> diagnostics) => Refresh(string.Join("\n", diagnostics.Select(d => d.Message)));

    private void Refresh(string? message = null)
    {
        refreshing = true;
        var snapshot = session.GetProject();
        var project = snapshot.Project;
        var mediaId = (MediaList.SelectedItem as MediaAsset)?.Id;
        MediaList.ItemsSource = project is null ? Array.Empty<MediaAsset>() : TimelineQueries.ListMediaAssets(project);
        if (project is not null) MediaList.SelectedItem = project.Assets.FirstOrDefault(a => a.Id == mediaId);
        SequenceList.ItemsSource = project is null ? Array.Empty<Sequence>() : TimelineQueries.ListSequences(project);
        var sequence = project?.Sequences.FirstOrDefault(s => s.Id == selectedSequenceId) ?? project?.Sequences.FirstOrDefault();
        selectedSequenceId = sequence?.Id; SequenceList.SelectedItem = sequence;
        var rows = new List<TimelineRow>();
        if (sequence is not null && project is not null)
            foreach (var track in sequence.Tracks.Reverse())
            {
                foreach (var clip in TimelineQueries.ListClips(track))
                    rows.Add(new(clip.Id, false, track.Name, project.Assets.First(a => a.Id == clip.MediaAssetId).Name, Seconds(clip.StartTicks), Seconds(clip.DurationTicks)));
                foreach (var caption in TimelineQueries.ListCaptions(track))
                    rows.Add(new(caption.Id, true, track.Name, caption.Text, Seconds(caption.StartTicks), Seconds(caption.DurationTicks)));
            }
        TimelineGrid.ItemsSource = rows;
        PreviewInfo.Text = sequence is null ? "シーケンスがありません" :
            $"{sequence.Settings.Width} × {sequence.Settings.Height}\n{sequence.Settings.FrameRate.Numerator}/{sequence.Settings.FrameRate.Denominator} fps · {Seconds(sequence.DurationTicks)} 秒";
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = snapshot.CanUndo;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = snapshot.CanRedo;
        Title = $"{(IsDirty() ? "* " : "")}{project?.Name ?? "FLAMORIS Kachinco"} — Kachinco";
        if (message is not null) Status.Text = message;
        refreshing = false;
    }
    private bool IsDirty() => session.GetProject().Project is { } p && ProjectJson.Serialize(p).Value != savedJson;
    private bool ConfirmDiscard() => !IsDirty() || MessageBox.Show(this, "未保存の変更を破棄しますか？", "Kachinco", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void Window_Closing(object? sender, CancelEventArgs e) { if (busy || !ConfirmDiscard()) e.Cancel = true; }
    private void SetBusy(bool value) { busy = value; IsEnabled = !value; }
    private static string Seconds(long ticks) => ((decimal)ticks / TimelineTime.TicksPerSecond).ToString("0.###", CultureInfo.CurrentCulture);
    private sealed record TimelineRow(Guid Id, bool IsCaption, string TrackName, string Name, string Start, string Duration);
}
