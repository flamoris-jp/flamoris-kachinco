using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public partial class MainWindow
{
    private readonly EffectLibraryPreferences effectPreferences = new();
    private EffectLibrary? effectLibrary;
    private bool allowAiEffectLibrary;
    private bool propertyRefreshing;
    private sealed record PropertyRow(VisualProperty Property, PropertyPoint Point, string Label);

    private void InitializeEffects()
    {
        var root = effectPreferences.Load();
        if (root.Success) effectLibrary = new(root.Value!);
        PropertyChoice.ItemsSource = Enum.GetValues<VisualProperty>(); PropertyChoice.SelectedIndex = 0;
    }
    private void EffectLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        using var operation = BeginHumanOperation(); CancelInspectorGesture(); playback.Pause();
        var window = new EffectLibraryWindow(session, selectedSequenceId, selectedClipId, effectPreferences, effectLibrary, allowAiEffectLibrary,
            (library, allow) => { RevokeMcp(); effectLibrary = library; allowAiEffectLibrary = allow; UpdateMcpStatus(); },
            () => Refresh(EditorText.Choose("エフェクトを適用しました。", "Effect applied."))) { Owner = this };
        window.ShowDialog(); Refresh();
    }
    private void RefreshPropertyPoints(Clip clip)
    {
        propertyRefreshing = true;
        try
        {
            var previous = PropertyPoints.SelectedItem as PropertyRow;
            var property = PropertyChoice.SelectedItem is VisualProperty selected ? selected : VisualProperty.X;
            var points = clip.Appearance.Automation.FirstOrDefault(c => c.Property == property)?.Points ?? [];
            var rows = points.Select(p => new PropertyRow(property, p,
                $"{((decimal)p.Tick / TimelineTime.TicksPerSecond).ToString("0.######", CultureInfo.CurrentCulture)} s · {p.Value.ToString("0.######", CultureInfo.CurrentCulture)}")).ToArray();
            PropertyPoints.ItemsSource = rows;
            PropertyPoints.SelectedItem = rows.FirstOrDefault(r => r.Point.Id == previous?.Point.Id && r.Property == previous.Property);
        }
        finally { propertyRefreshing = false; }
    }
    private void PropertyChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (inspectorRefreshing || propertyRefreshing || PropertyPoints is null) return;
        if (FindSelectedClip(session.GetProject().Project) is { } selected) RefreshPropertyPoints(selected.Clip);
    }
    private void PropertyPoints_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (propertyRefreshing || PropertyTimeBox is null) return;
        if (PropertyPoints.SelectedItem is PropertyRow row)
        {
            PropertyTimeBox.Text = ((decimal)row.Point.Tick / TimelineTime.TicksPerSecond).ToString(CultureInfo.CurrentCulture);
            PropertyValueBox.Text = row.Point.Value.ToString(CultureInfo.CurrentCulture);
        }
    }
    private void PropertyPoint_Save(object sender, RoutedEventArgs e)
    {
        if (busy || inspectorRefreshing || selectedSequenceId is not { } sequence || PropertyChoice.SelectedItem is not VisualProperty property) return;
        var baseline = session.GetProject(); var selected = FindSelectedClip(baseline.Project);
        if (selected is null || selected.Value.Track.Kind != TrackKind.Video) return;
        if (!decimal.TryParse(PropertyTimeBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var seconds) ||
            !double.TryParse(PropertyValueBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value))
        { Status.Text = EditorText.Choose("クリップ先頭からの秒数と値を入力してください。", "Enter seconds relative to clip start and a value."); return; }
        try
        {
            long tick = checked((long)decimal.Round(seconds * TimelineTime.TicksPerSecond));
            var row = PropertyPoints.SelectedItem as PropertyRow;
            EditCommand command = row is not null && row.Property == property
                ? new UpdateClipPropertyPoint(sequence, selected.Value.Clip.Id, property, row.Point with { Tick = tick, Value = value })
                : new AddClipPropertyPoint(sequence, selected.Value.Clip.Id, property, new(Guid.NewGuid(), tick, value));
            Show(session.Execute(new([command], baseline.Revision)), EditorText.Choose("変形ポイントを保存しました。", "Property point saved."));
        }
        catch (OverflowException) { Status.Text = EditorText.Choose("秒数が大きすぎます。", "Time exceeds supported ticks."); }
    }
    private void PropertyPoint_New(object sender, RoutedEventArgs e)
    {
        PropertyPoints.SelectedItem = null;
        if (FindSelectedClip(session.GetProject().Project) is not { } selected || PropertyChoice.SelectedItem is not VisualProperty property) return;
        PropertyTimeBox.Text = ((decimal)(Timeline.PlayheadTicks - selected.Clip.StartTicks) / TimelineTime.TicksPerSecond).ToString(CultureInfo.CurrentCulture);
        PropertyValueBox.Text = EffectComposition.Constant(selected.Clip.Appearance, property).ToString(CultureInfo.CurrentCulture);
    }
    private void PropertyPoint_Delete(object sender, RoutedEventArgs e)
    {
        if (busy || selectedSequenceId is not { } sequence || PropertyPoints.SelectedItem is not PropertyRow row || FindSelectedClip(session.GetProject().Project) is not { } selected) return;
        Show(session.Execute(new([new DeleteClipPropertyPoint(sequence, selected.Clip.Id, row.Property, row.Point.Id)], session.GetProject().Revision)),
            EditorText.Choose("変形ポイントを削除しました。", "Property point deleted."));
    }
}
