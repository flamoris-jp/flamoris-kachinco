using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class GpuExportChecks
{
    public static async Task Run(MainWindow main)
    {
        var composition = (MenuItem)main.FindName("ExportCompositionMenu");
        var encoder = (MenuItem)main.FindName("ExportEncoderMenu");
        var session = (EditorSession)Field(main, "session")!;
        var preview = (InteractivePreview)Field(main, "playback")!;
        var originalPreview = preview.BackendPreference;
        string directory = Path.Combine(Path.GetTempPath(), "kachinco-export-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            Select(composition, 1); Select(encoder, 1);
            Check((PreviewBackendPreference)Field(main, "exportComposition")! == PreviewBackendPreference.Cpu, "composition CPU menu");
            Check((VideoEncoderPreference)Field(main, "exportEncoder")! == VideoEncoderPreference.Cpu, "encoder CPU menu");
            Check(preview.BackendPreference == originalPreview, "export selection does not change preview");
            string source = Path.Combine(directory, "image.png");
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 200, 255, 0, 180, 0, 255, 180, 0, 0, 255, 100, 100, 100, 255 }, 8);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(source)) png.Save(file);
            Guid sequence = Guid.NewGuid(), asset = Guid.NewGuid(), track = Guid.NewGuid();
            Check(session.Execute(new([
                new CreateProject(Guid.NewGuid(), "Export fixture"),
                new CreateSequence(sequence, "Export", SequenceSettings.Portrait, TimelineTime.TicksPerSecond / 10),
                new RegisterMedia(new(asset, "image", source, MediaKind.Image, TimelineTime.TicksPerSecond)),
                new AddTrack(sequence, track, "V1", TrackKind.Video),
                new InsertClip(sequence, track, new(Guid.NewGuid(), asset, 0, 0, TimelineTime.TicksPerSecond / 10, true,
                    ClipAppearance.Default with { Transform = new(1, 0, 1, 1, 0) }, AudioProperties.Default))])).Success, "project fixture");
            Set(main, "selectedSequenceId", sequence);
            long revision = session.GetProject().Revision;
            var task = (Task<ExportResult?>)typeof(MainWindow).GetMethod("RenderOutput", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(main, [Path.Combine(directory, "output.mp4")])!;
            var result = await task;
            Check(result?.Stage == ExportStage.Completed && File.Exists(result.OutputPath), "ordinary UI export completes");
            var completion = main.OwnedWindows.Cast<Window>().Single(w => w.Title == "書き出し結果");
            var panel = (StackPanel)completion.Content;
            Check(panel.Children.OfType<TextBlock>().Any(t => t.Text.Contains(result!.OutputPath!)), "completion retains saved path");
            Check(!panel.Children.OfType<ProgressBar>().Single().IsIndeterminate && main.IsEnabled, "completion stops progress and restores editor");
            panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(result!.Metrics?.VideoEncoder == "libx264" && result.Metrics.Composition?.CpuFrames == 3, "UI choices reach common export service");
            Check(session.GetProject().Revision == revision && preview.BackendPreference == originalPreview, "export leaves history and preview preference unchanged");
            Console.WriteLine("WPF export menu independence and real portrait H.264/AAC export: PASS");
        }
        finally
        {
            Select(composition, 0); Select(encoder, 0);
            session.ReplaceProject(null); Set(main, "selectedSequenceId", null);
            Directory.Delete(directory, true);
        }
    }
    private static void Select(MenuItem menu, int index)
    { ((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Check(menu.Items.Cast<MenuItem>().Count(i => i.IsChecked) == 1, "mutually exclusive menu"); }
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void Check(bool value, string label) { if (!value) throw new Exception("GPU export: " + label); }
}
