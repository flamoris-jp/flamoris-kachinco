using System.Windows;
using Kachinco.Core;

namespace Kachinco.App;

public partial class MainWindow
{
    private void CreateEditNote_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !CaptionTextInspector.TryApply() || CurrentSequence() is not { } sequence) return;
        using var operation = BeginHumanOperation();
        var snapshot = session.GetProject();
        var note = new Caption(Guid.NewGuid(), 0, 3 * TimelineTime.TicksPerSecond,
            EditorText.Choose("編集メモ", "Editing note"));
        try
        {
            var result = session.Execute(CaptionImport.Plan(sequence, [note], Timeline.PlayheadTicks,
                snapshot.Revision, TrackKind.EditNote));
            if (result.Success) { selectedClipId = note.Id; selectedMediaId = null; }
            Show(result, EditorText.Choose("再生ヘッド位置に編集メモを作成しました。", "Created editing note at playhead."));
        }
        catch (OverflowException) { Status.Text = EditorText.Choose("時間が大きすぎます。", "Time is too large."); }
    }

    private void EditEditNotes_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !CaptionTextInspector.TryApply() || selectedSequenceId is not { } id) return;
        using var operation = BeginHumanOperation();
        new CaptionEditorWindow(session, id, () => Refresh(), kind: TrackKind.EditNote) { Owner = this }.ShowDialog();
    }

    private void ShowEditNotes_Click(object sender, RoutedEventArgs e) => RefreshEditNoteOverlay();

    private void RefreshEditNoteOverlay()
    {
        if (EditNotesOverlay is null) return;
        if (ShowEditNotesMenu?.IsChecked != true || previewContext is null)
        { EditNotesOverlay.Visibility = Visibility.Collapsed; return; }
        // Native decides half-open activity/order; the existing cursor supplies time.
        long tick = Math.Clamp(Timeline.PlayheadTicks, 0, previewContext.Sequence.DurationTicks - 1);
        var frame = previewContext.Evaluator.Evaluate(tick);
        var notes = frame.Value?.EditNotes ?? [];
        string value = string.Join("\n", notes.Select(n => n.Text));
        if (EditNotesOverlayText.Text != value) EditNotesOverlayText.Text = value;
        EditNotesOverlay.Visibility = notes.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }
}
