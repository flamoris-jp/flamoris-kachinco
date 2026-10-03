using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private bool inspectorRefreshing;
    private ClipPropertyEdit? inspectorGesture;
    private PropertySlider? inspectorSlider;
    private ProjectSnapshot? inspectorBaseline;
    private Guid? inspectorClipId;
    private readonly Dictionary<TextBox, string> inspectorNumbers = [];

    private void InspectorSlider_Started(object? sender, EventArgs e)
    {
        if (inspectorRefreshing || refreshing || busy || sender is not PropertySlider control ||
            selectedSequenceId is not { } sequence || selectedClipId is not { } clip ||
            !Enum.TryParse<ClipNumericProperty>(control.Tag?.ToString(), out var property)) return;
        CancelInspectorGesture();
        inspectorGesture = new(session.GetProject(), sequence, clip, property); inspectorSlider = control;
        playback.Pause();
    }
    private void InspectorSlider_Preview(object? sender, EventArgs e)
    {
        if (inspectorRefreshing || sender is not PropertySlider control || control != inspectorSlider || inspectorGesture is not { } edit) return;
        if (session.GetProject().Revision != edit.Baseline.Revision) { CancelInspectorGesture(); RefreshInspector(); return; }
        var candidate = edit.Preview(control.Value);
        if (!candidate.Success) { Status.Text = string.Join(" / ", candidate.Diagnostics.Select(d => d.Message)); return; }
        var context = PreviewContext.Create(candidate.Value!, edit.SequenceId, filename);
        if (!context.Success) return;
        previewContext = context.Value; playback.SetContext(previewContext); playback.Scrub(Timeline.PlayheadTicks);
    }
    private void InspectorSlider_Committed(object? sender, EventArgs e)
    {
        if (sender is not PropertySlider control || control != inspectorSlider || inspectorGesture is not { } edit) return;
        inspectorGesture = null; inspectorSlider = null; previewContext = null;
        if (edit.IsUnchanged(control.Value)) { RefreshInteractiveContext(); return; }
        Show(session.Execute(edit.Batch(control.Value)), EditorText.Choose("クリップの設定を変更しました。", "Clip properties updated."));
    }
    private void InspectorSlider_Cancelled(object? sender, EventArgs e) => CancelInspectorGesture();
    private void CancelInspectorGesture()
    {
        if (inspectorGesture is null) return;
        var control = inspectorSlider; inspectorGesture = null; inspectorSlider = null;
        control?.CancelEdit(); previewContext = null; RefreshInteractiveContext();
    }
    private void RememberInspectorNumbers(ProjectSnapshot baseline, Clip? clip)
    {
        inspectorBaseline = baseline; inspectorClipId = clip?.Id;
        foreach (var box in new[] { ClipStartBox, ClipSourceInBox, ClipDurationBox, TransformXBox, TransformYBox })
        { inspectorNumbers[box] = box.Text; box.ClearValue(Border.BorderBrushProperty); }
    }
    private void InspectorNumber_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { CommitInspectorNumber(box); e.Handled = true; }
        else if (e.Key == Key.Escape)
        { if (inspectorNumbers.TryGetValue(box, out var original)) box.Text = original; box.ClearValue(Border.BorderBrushProperty); e.Handled = true; }
    }
    private void InspectorNumber_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (sender is TextBox box) CommitInspectorNumber(box); }
    private void CommitInspectorNumber(TextBox box)
    {
        if (inspectorRefreshing || refreshing || busy || inspectorBaseline is not { Project: { } project } baseline ||
            selectedSequenceId is not { } sequence || inspectorClipId is not { } id || selectedClipId != id ||
            !inspectorNumbers.TryGetValue(box, out var original) || original == box.Text) return;
        var clip = project.Sequences.First(s => s.Id == sequence).Tracks.SelectMany(t => t.Clips).First(c => c.Id == id);
        EditCommand command;
        if (box.Tag?.ToString() is "X" or "Y")
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value)) { Invalid(); return; }
            command = new ClipPropertyEdit(baseline, sequence, id, box.Tag?.ToString() == "X" ? ClipNumericProperty.X : ClipNumericProperty.Y).Command(value);
        }
        else
        {
            if (!TrySeconds(box.Text, out var ticks) || ticks < 0 || box.Tag?.ToString() == "Duration" && ticks == 0) { Invalid(); return; }
            command = new TrimClip(sequence, id,
                box.Tag?.ToString() == "Start" ? ticks : clip.StartTicks,
                box.Tag?.ToString() == "Source" ? ticks : clip.SourceInTicks,
                box.Tag?.ToString() == "Duration" ? ticks : clip.DurationTicks);
        }
        var result = session.Execute(new([command], baseline.Revision));
        if (result.Success) Refresh(EditorText.Choose("クリップの設定を変更しました。", "Clip properties updated."));
        else { Invalid(); Status.Text = string.Join(" / ", result.Diagnostics.Select(d => d.Message)); }
        void Invalid() { box.BorderBrush = Brushes.OrangeRed; Status.Text = EditorText.Choose("有効な範囲の数値を入力してください。", "Enter a finite value within the valid range."); }
    }
    private void InspectorBlend_Changed(object sender, SelectionChangedEventArgs e) => CommitInspectorChoices();
    private void InspectorChoice_Click(object sender, RoutedEventArgs e) => CommitInspectorChoices();
    private void CommitInspectorChoices()
    {
        if (inspectorRefreshing || refreshing || busy || inspectorBaseline is not { Project: { } project } baseline ||
            inspectorClipId is not { } id || selectedClipId != id || selectedSequenceId is not { } sequence || BlendBox.SelectedItem is not BlendMode blend) return;
        var clip = project.Sequences.First(s => s.Id == sequence).Tracks.SelectMany(t => t.Clips).First(c => c.Id == id);
        var appearance = clip.Appearance with { Blend = blend }; var audio = clip.Audio with { Muted = MutedBox.IsChecked == true };
        if (appearance == clip.Appearance && audio == clip.Audio && clip.Enabled == (ClipEnabledBox.IsChecked == true)) return;
        Show(session.Execute(new([new SetClipProperties(sequence, id, ClipEnabledBox.IsChecked == true, appearance, audio)], baseline.Revision)),
            EditorText.Choose("クリップの設定を変更しました。", "Clip properties updated."));
    }
}
