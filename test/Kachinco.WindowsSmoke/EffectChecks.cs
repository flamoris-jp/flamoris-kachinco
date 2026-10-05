using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class EffectChecks
{
    public static async Task Run(MainWindow main)
    {
        var session = (EditorSession)Field(main, "session")!;
        var sequence = Guid.NewGuid(); var track = Guid.NewGuid(); var image = Guid.NewGuid(); var clip = Guid.NewGuid();
        const long t = TimelineTime.TicksPerSecond;
        string root = Path.Combine(Path.GetTempPath(), "kachinco-effect-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); EffectLibraryWindow? window = null;
        try
        {
            Check(session.Execute(new([
                new CreateProject(Guid.NewGuid(), "Effects fixture"),
                new CreateSequence(sequence, "Motion", SequenceSettings.Landscape, 4 * t),
                new AddTrack(sequence, track, "V1", TrackKind.Video),
                new RegisterMedia(new(image, "Artwork.png", "missing.png", MediaKind.Image, t)),
                new InsertClip(sequence, track, new(clip, image, 0, 0, 4 * t, true, ClipAppearance.Default, AudioProperties.Default))])).Success, "image fixture");
            Set(main, "selectedSequenceId", sequence); Set(main, "selectedClipId", clip); Refresh(); await Layout(main);
            Check(((Expander)main.FindName("TransformSection")).Visibility == Visibility.Visible, "image transform inspector");
            ((ComboBox)main.FindName("PropertyChoice")).SelectedItem = VisualProperty.X;
            ((TextBox)main.FindName("PropertyTimeBox")).Text = "0";
            ((TextBox)main.FindName("PropertyValueBox")).Text = "12";
            Invoke(main, "PropertyPoint_Save");
            Check(Current().Appearance.Automation.Single().Points.Single().Value == 12, "UI point uses shared commands");
            Check(session.Undo().Success && Current().Appearance.Automation.IsEmpty, "point Undo");
            Check(session.Redo().Success, "point Redo"); Refresh();
            var library = new EffectLibrary(Path.Combine(root, "library"));
            var preferences = new EffectLibraryPreferences(Path.Combine(root, "settings.json"));
            int changes = 0;
            window = new(session, sequence, clip, preferences, library, false, (_, _) => { }, () => changes++) { Owner = main };
            window.Show(); await Layout(window);
            ((TextBox)Field(window, "name")!).Text = "Saved pan";
            Invoke(window, "Capture"); await WaitIdle();
            Check(library.List().Value.Single().Name == "Saved pan", "Save as Effect UI");
            ((TextBox)Field(window, "intensity")!).Text = "0";
            long revision = session.GetProject().Revision;
            Invoke(window, "Apply"); await WaitIdle();
            Check(changes == 1 && session.GetProject().Revision == revision + 1 && Current().Appearance.Automation.Single().Points.All(p => p.Value == 0), "parameterized Apply UI and one commit");
            Check(session.Undo().Success && Current().Appearance.Automation.Single().Points.Single().Value == 12, "effect Undo");
            window.UpdateLayout(); Directory.CreateDirectory("artifacts");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create("artifacts/effect-library.png")) encoder.Save(output);
            Console.WriteLine("WPF still-image inspector, property point history, Save as Effect and parameterized shared Apply: PASS");
            async Task WaitIdle()
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((bool)Field(window!, "working")!)
                { if (DateTime.UtcNow > deadline) throw new Exception("Effect UI operation timed out."); await Task.Delay(20); }
                await Layout(window!);
            }
        }
        finally
        {
            window?.Close(); session.ReplaceProject(null); Set(main, "selectedSequenceId", null); Set(main, "selectedClipId", null); Refresh();
            Directory.Delete(root, true);
        }
        Clip Current() => session.GetProject().Project!.Sequences.Single().Tracks.Single().Clips.Single();
        void Refresh() => typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object?[] { null });
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Invoke(object owner, string name) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [owner, new RoutedEventArgs()]);
    private static async Task Layout(FrameworkElement element) { element.UpdateLayout(); await element.Dispatcher.InvokeAsync(() => element.UpdateLayout(), DispatcherPriority.ContextIdle); }
    private static void Check(bool value, string name) { if (!value) throw new Exception("Effects: " + name); }
}
