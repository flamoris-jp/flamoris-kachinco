using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;

namespace Kachinco.App;

public partial class MainWindow
{
    private TitleImageService CreateTitleImageService() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Kachinco", "title-images"),
        new WindowsPreviewImageEncoder(64 * 1024 * 1024), new WindowsCaptionRasterizer(Dispatcher));
    private Sequence? CurrentSequence() => session.GetProject().Project?.Sequences.FirstOrDefault(s => s.Id == selectedSequenceId);
    private bool HasTitleImage(Sequence sequence)
    {
        if (sequence.TitleImage is not null) return true;
        Status.Text = EditorText.Choose("先にタイトル画像を選択してください。", "Choose a title image first.");
        return false;
    }
    private void RemoveMedia_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        if (MediaList.SelectedItem is not MediaAssetRow row) return;
        Apply(EditorText.Choose("素材の登録を解除しました。元ファイルは残っています。", "Media unregistered. Source file retained."),
            new UnregisterMedia(row.Asset.Id));
    }
    private async void TitleFromFile_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CurrentSequence() is not { } sequence) return;
        using var operation = BeginHumanOperation();
        var picker = new OpenFileDialog { Filter = MediaFilter(MediaKind.Image) };
        if (picker.ShowDialog(this) != true) return;
        var snapshot = session.GetProject();
        SetBusy(true);
        try
        {
            var probed = await mediaProbe.ProbeAsync(picker.FileName);
            if (!probed.Success) { ShowErrors(probed.Diagnostics); return; }
            if (probed.Value!.Kind != MediaKind.Image) return;
            var asset = probed.Value.ToMediaAsset(Guid.NewGuid());
            Show(session.Execute(new([new RegisterMedia(asset), new SetTitleImage(sequence.Id, new(asset.Id))], snapshot.Revision)),
                EditorText.Choose("タイトル画像を設定しました。", "Title image selected."));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private async void TitleFromFrame_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CurrentSequence() is not { } sequence) return;
        using var operation = BeginHumanOperation();
        playback.Pause();
        var snapshot = session.GetProject();
        long tick = Math.Min(Timeline.PlayheadTicks, sequence.DurationTicks - 1);
        SetBusy(true);
        try
        {
            using var prepared = await CreateTitleImageService().PrepareAsync(snapshot.Project!, sequence.Id, tick, filename);
            var result = session.Execute(prepared.Batch(sequence.Id, snapshot.Revision));
            if (result.Success) prepared.Keep();
            Show(result, EditorText.Choose("合成フレームをタイトル画像に設定しました。", "Composited frame selected as title image."));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private void TitleClear_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        if (CurrentSequence() is { } sequence)
            Apply(EditorText.Choose("タイトル画像の選択を解除しました。", "Title selection cleared."), new SetTitleImage(sequence.Id, null));
    }
    private void TitleReserve_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        if (CurrentSequence() is { } sequence)
            Apply(EditorText.Choose("先頭に1フレームを確保しました。", "First title frame reserved."),
                new ReserveTitleFrame(sequence.Id, Guid.NewGuid(), Guid.NewGuid()));
    }
    private void TitleRelease_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        if (CurrentSequence() is { } sequence)
            Apply(EditorText.Choose("先頭1フレームの確保を解除しました。", "First title frame released."),
                new ReleaseTitleFrame(sequence.Id));
    }
    private async void TitlePreview_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CurrentSequence() is not { } sequence) return;
        if (!HasTitleImage(sequence)) return;
        using var operation = BeginHumanOperation();
        SetBusy(true);
        try
        {
            var frame = await CreateTitleImageService().RenderSelectionAsync(session.GetProject().Project!, sequence.Id, filename);
            var bytes = await new WindowsPreviewImageEncoder(64 * 1024 * 1024).EncodeAsync(frame, frame.Width, frame.Height, "png", 100, default);
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); }
            var window = new Window { Owner = this, Title = EditorText.Choose("タイトル画像", "Title image"),
                Width = 640, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.Uniform } };
            SetBusy(false); window.ShowDialog();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private async void TitleExport_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CurrentSequence() is not { } sequence) return;
        if (!HasTitleImage(sequence)) return;
        using var operation = BeginHumanOperation();
        var picker = new SaveFileDialog { Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg", DefaultExt = ".png", AddExtension = true, FileName = "title.png" };
        if (picker.ShowDialog(this) != true) return;
        string target = Path.GetFullPath(picker.FileName);
        var project = session.GetProject().Project!;
        if (MediaReferenceResolver.Inspect(project, filename).Any(a => string.Equals(a.ResolvedPath, target, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(filename, target, StringComparison.OrdinalIgnoreCase))
        { Status.Text = EditorText.Choose("元素材とは別の保存先を選んでください。", "Choose a destination different from source media."); return; }
        SetBusy(true); string? temporary = null;
        try
        {
            var frame = await CreateTitleImageService().RenderSelectionAsync(project, sequence.Id, filename);
            string format = Path.GetExtension(target).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
            byte[] bytes = await new WindowsPreviewImageEncoder(64 * 1024 * 1024).EncodeAsync(frame, frame.Width, frame.Height, format, 95, default);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await stream.WriteAsync(bytes); await stream.FlushAsync(); }
            File.Move(temporary, target, true); temporary = null;
            Status.Text = EditorText.Choose("タイトル画像を書き出しました。", "Title image exported.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status.Text = ex.Message; }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            SetBusy(false);
        }
    }
    private void Crossfade_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        var selected = FindSelectedClip(session.GetProject().Project);
        if (selected is not { } value || value.Track.Kind != TrackKind.Video || CurrentSequence() is not { } sequence) return;
        var incoming = value.Clip;
        var outgoing = value.Track.Clips.Where(c => c.Id != incoming.Id && c.StartTicks < incoming.StartTicks && c.EndTicks >= incoming.StartTicks)
            .OrderByDescending(c => c.StartTicks).FirstOrDefault();
        if (outgoing is null) { Status.Text = EditorText.Choose("同じトラックで接するか重なる2つの映像の、後ろのクリップを選んでください。", "Select the later of two touching or overlapping video clips on the same track."); return; }
        var input = new TextBox { Text = Seconds(Math.Min(TimelineTime.SecondsToTicks(.5m), incoming.DurationTicks)), Margin = new Thickness(12) };
        var ok = new Button { Content = EditorText.Choose("適用", "Apply"), IsDefault = true, Margin = new Thickness(12) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = EditorText.Choose("長さ（秒）。前のクリップを必要分だけ延長します。素材に余裕が必要です。", "Duration in seconds. Extends the outgoing clip if needed; requires source handles."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
        panel.Children.Add(input); panel.Children.Add(ok);
        var dialog = new Window { Owner = this, Title = EditorText.Choose("クロスフェード", "Crossfade"), Width = 380,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        ok.Click += (_, _) => { if (TrySeconds(input.Text, out var t) && t > 0) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true && TrySeconds(input.Text, out long duration))
            Apply(EditorText.Choose("クロスフェードを設定しました。透明度カーブから調整できます。", "Crossfade applied. Adjust it through opacity automation."),
                new ApplyCrossfade(sequence.Id, outgoing.Id, incoming.Id, duration, Guid.NewGuid(), Guid.NewGuid()));
    }
    private void ClearFade_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginHumanOperation();
        if (CurrentSequence() is { } sequence && selectedClipId is { } clipId)
            Apply(EditorText.Choose("透明度カーブを解除しました。", "Opacity curve cleared."),
                new SetClipPropertyCurve(sequence.Id, clipId, new(VisualProperty.Opacity, [])));
    }
}
