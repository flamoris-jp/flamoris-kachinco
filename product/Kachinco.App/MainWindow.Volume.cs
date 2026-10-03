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
    private readonly MonitoringPreferences monitoringPreferences = new();
    private double monitoringBeforeEdit = 1;
    private Guid? volumePointId;
    private Guid? volumeClipId;
    private VolumePointEdit? volumeGesture;
    private bool volumeRefreshing;

    private void InitializeMonitoring()
    {
        var saved = monitoringPreferences.Load();
        playback.SetMonitoringGain(saved.Success ? saved.Value : 1);
        MonitoringControl.Value = playback.MonitoringGain;
        if (!saved.Success) logger.Log(Flamoris.Logging.LogLevel.Warn, "preview", "Monitoring preference could not be loaded", properties:
            new Dictionary<string, object?> { ["diagnosticCodes"] = string.Join(",", saved.Diagnostics.Select(d => d.Code)) });
    }
    private void Monitoring_Started(object? sender, EventArgs e) => monitoringBeforeEdit = playback.MonitoringGain;
    private void Monitoring_Preview(object? sender, EventArgs e)
    {
        try { playback.SetMonitoringGain(MonitoringControl.Value); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        { MonitoringControl.CancelEdit(); Status.Text = EditorText.Choose("モニター音量を変更できません。音声出力を確認してください。", "Could not change monitoring volume. Check the audio output."); }
    }
    private void Monitoring_Committed(object? sender, EventArgs e)
    {
        var saved = monitoringPreferences.Save(playback.MonitoringGain);
        if (!saved.Success) Status.Text = EditorText.Choose("モニター音量を保存できませんでした。", "Could not save monitoring volume.");
    }
    private void Monitoring_Cancelled(object? sender, EventArgs e)
    {
        try { playback.SetMonitoringGain(monitoringBeforeEdit); }
        catch (InvalidOperationException) { }
    }
    private sealed record VolumeRow(VolumePoint Point, string Label);
    private void RefreshVolumePoints(Clip clip)
    {
        volumeRefreshing = true;
        try
        {
        if (volumeClipId != clip.Id) { volumePointId = null; volumeClipId = clip.Id; }
        VolumePointsList.ItemsSource = clip.Audio.VolumePoints.Select(p => new VolumeRow(p,
            $"{VolumeSeconds(p.Tick)} s  ·  {(p.Multiplier * 100).ToString("0.###", CultureInfo.CurrentCulture)} %")).ToArray();
        VolumePointsList.SelectedItem = ((VolumeRow[])VolumePointsList.ItemsSource).FirstOrDefault(p => p.Point.Id == volumePointId);
        RefreshVolumeSelection();
        }
        finally { volumeRefreshing = false; }
    }
    private void VolumePoints_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (inspectorRefreshing || volumeRefreshing) return;
        CancelInspectorGesture();
        volumePointId = (VolumePointsList.SelectedItem as VolumeRow)?.Point.Id;
        RefreshVolumeSelection();
    }
    private void RefreshVolumeSelection()
    {
        var point = (VolumePointsList.SelectedItem as VolumeRow)?.Point;
        VolumeTimeBox.Text = point is null ? "" : VolumeSeconds(point.Tick);
        VolumeTimeBox.ClearValue(Border.BorderBrushProperty);
        VolumeTimeBox.IsEnabled = VolumeTimeApplyButton.IsEnabled = VolumeValueControl.IsEnabled = DeleteVolumePointButton.IsEnabled = point is not null;
        VolumeValueControl.Value = point?.Multiplier ?? 1;
    }
    private (ProjectSnapshot Baseline, Guid Sequence, Clip Clip)? VolumeBaseline()
    {
        if (refreshing || inspectorRefreshing || busy || inspectorBaseline is not { } baseline || selectedSequenceId is not { } sequence ||
            inspectorClipId != selectedClipId || FindSelectedClip(baseline.Project) is not { } selected || selected.Track.Kind != TrackKind.Audio) return null;
        return (baseline, sequence, selected.Clip);
    }
    private void AddVolumePoint_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeBaseline() is not { } value) return;
        long tick = Timeline.PlayheadTicks - value.Clip.StartTicks;
        if (tick < 0 || tick > value.Clip.DurationTicks)
        { Status.Text = EditorText.Choose("再生ヘッドを選択中の音声クリップ内に移動してください。", "Move the playhead into the selected audio clip."); return; }
        if (value.Clip.Audio.VolumePoints.FirstOrDefault(p => p.Tick == tick) is { } existing)
        { volumePointId = existing.Id; RefreshVolumePoints(value.Clip); return; }
        volumePointId = Guid.NewGuid();
        Show(session.Execute(new([new AddClipVolumePoint(value.Sequence, value.Clip.Id, new(volumePointId.Value, tick, 1))], value.Baseline.Revision)),
            EditorText.Choose("音量ポイントを追加しました。", "Volume point added."));
    }
    private void DeleteVolumePoint_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeBaseline() is not { } value || volumePointId is not { } id) return;
        Show(session.Execute(new([new DeleteClipVolumePoint(value.Sequence, value.Clip.Id, id)], value.Baseline.Revision)),
            EditorText.Choose("音量ポイントを削除しました。", "Volume point deleted."));
    }
    private void VolumeTime_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitVolumeTime(); e.Handled = true; }
        else if (e.Key == Key.Escape) { RefreshVolumeSelection(); e.Handled = true; }
    }
    private void VolumeTimeApply_Click(object sender, RoutedEventArgs e) => CommitVolumeTime();
    private void CommitVolumeTime()
    {
        if (VolumeBaseline() is not { } value || value.Clip.Audio.VolumePoints.FirstOrDefault(p => p.Id == volumePointId) is not { } point) return;
        if (!decimal.TryParse(VolumeTimeBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal seconds)) { Invalid(); return; }
        long tick;
        try { tick = checked((long)decimal.Round(checked(seconds * TimelineTime.TicksPerSecond), 0, MidpointRounding.AwayFromZero)); }
        catch (OverflowException) { Invalid(); return; }
        if (point.Tick == tick) return;
        var result = session.Execute(new([new UpdateClipVolumePoint(value.Sequence, value.Clip.Id, point with { Tick = tick })], value.Baseline.Revision));
        if (result.Success) Refresh(EditorText.Choose("音量ポイントの時刻を変更しました。", "Volume point time updated."));
        else { VolumeTimeBox.BorderBrush = Brushes.OrangeRed; Status.Text = string.Join(" / ", result.Diagnostics.Select(d => d.Message)); }
        void Invalid() { VolumeTimeBox.BorderBrush = Brushes.OrangeRed; Status.Text = EditorText.Choose("クリップ先頭からの秒数を入力してください。", "Enter seconds relative to the clip start."); }
    }
    private void FadeVolume_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeBaseline() is not { } value || sender is not Button button) return;
        try { Show(session.Execute(VolumeCurveEdits.Fade(value.Baseline, value.Sequence, value.Clip.Id, button.Tag?.ToString() == "In")),
            EditorText.Choose("音量フェードを設定しました。", "Volume fade applied.")); }
        catch (ArgumentException) { Status.Text = EditorText.Choose("このクリップはフェードを設定するには短すぎます。", "This clip is too short for a fade."); }
    }
    private void VolumeValue_Started(object? sender, EventArgs e)
    {
        if (VolumeBaseline() is not { } value || value.Clip.Audio.VolumePoints.FirstOrDefault(p => p.Id == volumePointId) is not { } point) return;
        CancelInspectorGesture(); volumeGesture = new(value.Baseline, value.Sequence, value.Clip.Id, point); playback.Pause();
    }
    private void VolumeValue_Preview(object? sender, EventArgs e)
    {
        if (volumeGesture is not { } edit) return;
        if (session.GetProject().Revision != edit.Baseline.Revision) { CancelVolumeGesture(); RefreshInspector(); return; }
        var candidate = edit.Preview(VolumeValueControl.Value);
        if (!candidate.Success) { Status.Text = string.Join(" / ", candidate.Diagnostics.Select(d => d.Message)); return; }
        var context = PreviewContext.Create(candidate.Value!, edit.SequenceId, filename);
        if (!context.Success) return;
        previewContext = context.Value; playback.SetContext(previewContext); playback.Scrub(Timeline.PlayheadTicks);
    }
    private void VolumeValue_Committed(object? sender, EventArgs e)
    {
        if (volumeGesture is not { } edit) return;
        volumeGesture = null; previewContext = null;
        if (edit.IsUnchanged(VolumeValueControl.Value)) { RefreshInteractiveContext(); return; }
        Show(session.Execute(edit.Batch(VolumeValueControl.Value)), EditorText.Choose("音量ポイントを変更しました。", "Volume point updated."));
    }
    private void VolumeValue_Cancelled(object? sender, EventArgs e) => CancelVolumeGesture();
    private void CancelVolumeGesture()
    {
        if (volumeGesture is null) return;
        volumeGesture = null; VolumeValueControl.CancelEdit(); previewContext = null; RefreshInteractiveContext();
    }
    // Enough precision to reopen any entered tick without rounding it to milliseconds.
    private static string VolumeSeconds(long ticks) => ((decimal)ticks / TimelineTime.TicksPerSecond).ToString("0.###############", CultureInfo.CurrentCulture);
}
