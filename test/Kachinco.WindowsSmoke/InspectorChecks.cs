using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;

internal static class InspectorChecks
{
    public static async Task Run(MainWindow main)
    {
        var session = (EditorSession)Field("session")!;
        var sequence = Guid.NewGuid(); var video = Guid.NewGuid(); var audio = Guid.NewGuid();
        var vtrack = Guid.NewGuid(); var atrack = Guid.NewGuid(); var vclip = Guid.NewGuid(); var aclip = Guid.NewGuid();
        const long t = TimelineTime.TicksPerSecond;
        Check(session.Execute(new([
            new CreateProject(Guid.NewGuid(), "Inspector fixture"), new CreateSequence(sequence, "Properties", SequenceSettings.Landscape, 8 * t),
            new RegisterMedia(new(video, "Artwork.mov", "missing.mov", MediaKind.Mov, 8 * t)),
            new RegisterMedia(new(audio, "Music.wav", "missing.wav", MediaKind.Wav, 8 * t, 48000, 2)),
            new AddTrack(sequence, vtrack, "V1", TrackKind.Video), new AddTrack(sequence, atrack, "A1", TrackKind.Audio),
            new InsertClip(sequence, vtrack, new(vclip, video, 0, 0, 8 * t, true, ClipAppearance.Default, AudioProperties.Default)),
            new InsertClip(sequence, atrack, new(aclip, audio, 0, 0, 8 * t, true, ClipAppearance.Default, AudioProperties.Default))])).Success, "fixture");
        Set("selectedSequenceId", sequence); Set("selectedClipId", vclip); Refresh(); await Layout();
        try
        {
            var control = (PropertySlider)main.FindName("OpacityControl");
            var slider = (Slider)((Grid)control.Content).Children[0];
            var baseline = session.GetProject(); string before = NativeProjectCodec.Serialize(baseline.Project!).Value!;
            slider.RaiseEvent(new DragStartedEventArgs(0, 0)); slider.Value = .6; slider.Value = .4;
            Check(session.GetProject().Revision == baseline.Revision && NativeProjectCodec.Serialize(session.GetProject().Project!).Value == before, "preview isolation");
            var context = (Kachinco.Infrastructure.PreviewContext)Field("previewContext")!;
            Check(context.Evaluator.Evaluate(0).Value!.VideoLayers[0].Appearance.Opacity == .4, "shared evaluator preview");
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true));
            Check(control.Value == 1 && session.GetProject().Revision == baseline.Revision, "cancel isolation");
            slider.RaiseEvent(new DragStartedEventArgs(0, 0)); slider.Value = .4;
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            Check(session.GetProject().Revision == baseline.Revision + 1, "one release command");
            Check(session.Undo().Success, "Undo"); Refresh();
            Check(NativeProjectCodec.Serialize(session.GetProject().Project!).Value == before, "one Undo restores");
            Check(session.Redo().Success, "Redo"); Refresh(); Check(control.Value == .4, "Redo control projection");

            slider.RaiseEvent(new DragStartedEventArgs(0, 0)); slider.Value = .2;
            var clip = session.GetProject().Project!.Sequences[0].Tracks[0].Clips[0];
            Check(session.Execute(new([new SetClipProperties(sequence, vclip, true, clip.Appearance with { Opacity = .7 }, clip.Audio)])).Success, "concurrent edit");
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            Check(session.GetProject().Project!.Sequences[0].Tracks[0].Clips[0].Appearance.Opacity == .7, "stale release rejection");

            var scale = (PropertySlider)main.FindName("ScaleXControl");
            var scaleNumber = (TextBox)((Grid)scale.Content).Children[1];
            scaleNumber.Text = "1200";
            scaleNumber.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, scaleNumber, slider) { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            Check(session.GetProject().Project!.Sequences[0].Tracks[0].Clips[0].Appearance.Transform.ScaleX == 12 && scale.Value == 12, "precise value beyond slider");
            long revision = session.GetProject().Revision;
            scaleNumber.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, scaleNumber, slider) { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            Check(session.GetProject().Revision == revision, "unchanged focus no-op");
            scaleNumber.Text = "NaN";
            scaleNumber.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, scaleNumber, slider) { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            Check(session.GetProject().Revision == revision && scaleNumber.Text == "NaN", "invalid numeric retained without mutation"); Refresh();

            var inspector = (StackPanel)main.FindName("ClipInspector");
            double oldWidth = inspector.Width;
            foreach (double width in new[] { 230d, 264d })
            {
                inspector.Width = width; await Layout();
                foreach (var item in new[] { control, scale, (PropertySlider)main.FindName("ScaleYControl"), (PropertySlider)main.FindName("RotationControl") })
                { Check(item.ActualWidth <= width && item.ActualWidth >= 150, "compact property width"); Check(((TextBox)((Grid)item.Content).Children[1]).ActualWidth == 66, "bounded numeric width"); }
            }
            inspector.Width = oldWidth; await Layout();
            Directory.CreateDirectory("artifacts");
            var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create("artifacts/inspector-properties.png")) encoder.Save(output);
            Set("selectedClipId", aclip); Refresh(); await Layout();
            Check(((Expander)main.FindName("AudioSection")).Visibility == Visibility.Visible && ((Expander)main.FindName("TransformSection")).Visibility == Visibility.Collapsed, "audio section semantics");
            Console.WriteLine("WPF Inspector narrow layout, native transient preview, cancel, single history, stale release and precise numeric range: PASS");
        }
        finally { session.ReplaceProject(null); Set("selectedSequenceId", null); Set("selectedClipId", null); Refresh(); }
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        void Refresh() => typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object?[] { null });
        async Task Layout() { main.UpdateLayout(); await main.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ContextIdle); }
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception("Inspector: " + name); }
}
