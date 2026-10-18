using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kachinco.Infrastructure;
using Microsoft.Win32;

namespace Kachinco.App;

public partial class MainWindow
{
    private async void ReplaceMp4Title_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        using var operation = BeginHumanOperation();
        var source = new OpenFileDialog { Title = EditorText.Choose("完成したMP4を選択", "Choose a completed MP4"), Filter = "MP4 (*.mp4)|*.mp4" };
        if (source.ShowDialog(this) != true) return;
        string? selectedImage = null;
        if (CurrentSequence()?.TitleImage is { } selection && session.GetProject().Project is { } project)
            selectedImage = MediaReferenceResolver.Inspect(project, filename).FirstOrDefault(a => a.MediaAssetId == selection.MediaAssetId)?.ResolvedPath;
        if (selectedImage is not null && File.Exists(selectedImage))
        {
            var choice = MessageBox.Show(this, EditorText.Choose(
                "選択済みのタイトル画像を使いますか？\n「いいえ」で別の画像ファイルを選べます。",
                "Use the selected project title image?\nChoose No to select another image file."),
                EditorText.Choose("先頭フレームの画像", "First-frame image"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.No) selectedImage = null;
        }
        else selectedImage = null;
        if (selectedImage is null)
        {
            var image = new OpenFileDialog { Title = EditorText.Choose("差し替える画像を選択", "Choose replacement image"), Filter = MediaFilter(Kachinco.Core.MediaKind.Image) };
            if (image.ShowDialog(this) != true) return;
            selectedImage = image.FileName;
        }
        playback.Pause();
        var service = new Mp4TitleReplacement();
        PreparedMp4Title? prepared = null;
        await RunMp4TitleOperation(async (token, status) =>
        {
            status.Text = EditorText.Choose("動画と画像を確認しています…", "Inspecting video and image…");
            prepared = await service.PrepareAsync(source.FileName, selectedImage, token);
        });
        if (prepared is null) return;
        var bitmap = new BitmapImage();
        using (var bytes = new MemoryStream(prepared.PreviewPng.ToArray()))
        { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze(); }
        var preview = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 360, Margin = new Thickness(12) };
        var explanation = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Text = EditorText.Choose(
            $"先頭1フレームをこの画像に置き換えます。フレーム追加は行いません。\n{prepared.Width}×{prepared.Height} / {prepared.FrameCount:N0}フレーム\n冒頭{prepared.ReencodedFrames:N0}フレームを再圧縮、それ以降の{prepared.CopiedFrames:N0}フレームをコピーします。冒頭区間は画質が少し変わる場合があります。音声と総尺は維持します。\n元ファイルを残し、別のMP4として保存します。",
            $"Replace the first frame with this image; no frame is inserted.\n{prepared.Width}×{prepared.Height} / {prepared.FrameCount:N0} frames\nRe-encode the first {prepared.ReencodedFrames:N0} frames; copy the remaining {prepared.CopiedFrames:N0}. The opening region may lose some quality. Preserve audio and duration.\nSave a separate MP4; retain the original.") };
        if (prepared.CopiedFrames == 0) explanation.Text += EditorText.Choose(
            "\nこの動画には後続コピーに使える区切りがないため、映像全体を再圧縮します。", "\nNo suitable later keyframe: the entire video will be re-encoded.");
        var save = new Button { Content = EditorText.Choose("別MP4として保存…", "Save as a separate MP4…"), IsDefault = true, Margin = new Thickness(12) };
        var panel = new StackPanel(); panel.Children.Add(preview); panel.Children.Add(explanation); panel.Children.Add(save);
        var dialog = new Window { Owner = this, Title = EditorText.Choose("完成MP4の先頭フレームを差し替え", "Replace first frame in completed MP4"),
            Width = 680, SizeToContent = SizeToContent.Height, MaxHeight = 760, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        string? output = null;
        save.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "MP4 (*.mp4)|*.mp4", DefaultExt = ".mp4", AddExtension = true, OverwritePrompt = false,
                FileName = Path.GetFileNameWithoutExtension(source.FileName) + "-title.mp4", InitialDirectory = Path.GetDirectoryName(source.FileName) };
            if (picker.ShowDialog(dialog) != true) return;
            if (File.Exists(picker.FileName))
            { MessageBox.Show(dialog, EditorText.Choose("元動画を守るため、新しいファイル名を指定してください。", "Choose a new filename to preserve existing media.")); return; }
            output = picker.FileName; dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true || output is null) return;
        await RunMp4TitleOperation(async (token, status) =>
        {
            var progress = new Progress<string>(phase => status.Text = phase.StartsWith("encode:", StringComparison.Ordinal)
                ? EditorText.Choose($"冒頭{prepared.ReencodedFrames:N0}フレームを処理しています…", $"Encoding {prepared.ReencodedFrames:N0} opening frames…")
                : phase.StartsWith("copy:", StringComparison.Ordinal) ? EditorText.Choose("本編をコピーしています…", "Copying the remaining video…")
                : EditorText.Choose("フレーム数・映像の接続・音声を検証しています…", "Verifying frame timing, splice and audio…"));
            var result = await service.ReplaceAsync(prepared, output, progress, token);
            Status.Text = EditorText.Choose($"先頭フレームを差し替えました。{result.ElapsedMilliseconds / 1000:0.0}秒 / 再圧縮{result.ReencodedFrames:N0}・コピー{result.CopiedFrames:N0}フレーム",
                $"First frame replaced in {result.ElapsedMilliseconds / 1000:0.0}s / {result.ReencodedFrames:N0} re-encoded, {result.CopiedFrames:N0} copied frames.");
        });
    }
    private async Task RunMp4TitleOperation(Func<CancellationToken, TextBlock, Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
        var cancel = new Button { Content = EditorText.Choose("キャンセル", "Cancel"), Margin = new Thickness(16) };
        var panel = new StackPanel(); panel.Children.Add(status); panel.Children.Add(cancel);
        var window = new Window { Owner = this, Title = EditorText.Choose("MP4の先頭フレーム", "MP4 first frame"), Width = 440,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        bool done = false;
        cancel.Click += (_, _) => { cancellation.Cancel(); cancel.IsEnabled = false; };
        window.Closing += (_, args) => { if (!done) { cancellation.Cancel(); args.Cancel = true; } };
        SetBusy(true); window.Show();
        try { await action(cancellation.Token, status); }
        catch (OperationCanceledException) { Status.Text = EditorText.Choose("キャンセルしました。元動画は変更していません。", "Cancelled. Original video retained."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException or System.Text.Json.JsonException or OverflowException)
        {
            Status.Text = EditorText.Choose("先頭フレームを差し替えられませんでした: ", "Could not replace first frame: ") + ex.Message;
            MessageBox.Show(window, Status.Text, EditorText.Choose("MP4の差し替え", "MP4 replacement"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { done = true; window.Close(); SetBusy(false); }
    }
}
