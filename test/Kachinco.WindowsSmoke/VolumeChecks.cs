using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class VolumeChecks
{
    public static async Task Run(MainWindow main)
    {
        var session = (EditorSession)Field("session")!; var sequence = Guid.NewGuid(); var track = Guid.NewGuid(); var asset = Guid.NewGuid(); var clip = Guid.NewGuid();
        const long t = TimelineTime.TicksPerSecond;
        Check(session.Execute(new([new CreateProject(Guid.NewGuid(), "Volume fixture"), new CreateSequence(sequence, "Audio", SequenceSettings.Landscape, 8 * t),
            new RegisterMedia(new(asset, "Music.wav", "missing.wav", MediaKind.Wav, 8 * t, 48000, 2)), new AddTrack(sequence, track, "A1", TrackKind.Audio),
            new InsertClip(sequence, track, new(clip, asset, 0, 0, 8 * t, true, ClipAppearance.Default, AudioProperties.Default))])).Success, "fixture");
        Set("selectedSequenceId", sequence); Set("selectedClipId", clip); Refresh(); await Layout();
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fkproj");
        try
        {
            var timeline = (TimelineSurface)main.FindName("Timeline"); timeline.SetCursorTicks(t);
            long revision = session.GetProject().Revision; Click("AddVolumePointButton");
            Check(session.GetProject().Revision == revision + 1 && Audio().VolumePoints.Single().Tick == t, "add at playhead");
            var list = (ListBox)main.FindName("VolumePointsList"); Check(list.SelectedItem is not null, "new point selection");
            revision = session.GetProject().Revision; Click("AddVolumePointButton"); Check(session.GetProject().Revision == revision && list.SelectedItem is not null, "existing point selected without mutation");
            var time = (TextBox)main.FindName("VolumeTimeBox"); time.Text = "0"; Click("VolumeTimeApplyButton"); Check(Audio().VolumePoints.Single().Tick == 0, "edit point time");
            var value = (PropertySlider)main.FindName("VolumeValueControl"); var number = (TextBox)((Grid)value.Content).Children[1];
            number.Text = "25"; number.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, number, time) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Check(Audio().VolumePoints.Single().Multiplier == .25, "edit precise point multiplier");
            var slider = (Slider)((Grid)value.Content).Children[0]; string before = Json(); revision = session.GetProject().Revision;
            slider.RaiseEvent(new DragStartedEventArgs(0, 0)); slider.Value = .8;
            Check(Json() == before && session.GetProject().Revision == revision, "point candidate isolation");
            Check(((PreviewContext)Field("previewContext")!).Evaluator.Evaluate(0).Value!.Audio[0].Gain == .8, "native point preview");
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true)); Check(value.Value == .25 && Json() == before, "point cancel");
            slider.RaiseEvent(new DragStartedEventArgs(0, 0)); slider.Value = .5; slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            Check(session.GetProject().Revision == revision + 1, "point release one transaction");
            Check(session.Undo().Success && Json() == before, "point one Undo"); Refresh(); Check(session.Redo().Success, "point Redo"); Refresh();
            Click("FadeInButton"); Click("FadeOutButton");
            Check(Audio().VolumePoints.Length == 4 && Audio().VolumePoints[0].Multiplier == 0 && Audio().VolumePoints[^1].Multiplier == 0, "fade buttons");
            var store = new ProjectFileStore(); Check((await store.SaveAsync(file, session.GetProject().Project!)).Success, "save v3");
            var reopened = await store.LoadAsync(file); Check(reopened.Success && NativeProjectCodec.Serialize(reopened.Value!).Value == Json(), "reopen v3");
            before = Json(); Click("DeleteVolumePointButton"); Check(Audio().VolumePoints.Length == 3, "delete point");
            Check(session.Undo().Success && Json() == before, "delete Undo"); Refresh(); list.SelectedIndex = 0;
            slider.RaiseEvent(new DragStartedEventArgs(0, 0)); slider.Value = .2;
            var point = Audio().VolumePoints[0]; Check(session.Execute(new([new UpdateClipVolumePoint(sequence, clip, point with { Multiplier = .7 })])).Success, "concurrent point edit");
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false)); Check(Audio().VolumePoints[0].Multiplier == .7, "stale point release rejected");
            Refresh(); list.SelectedIndex = 0; time.Text = "1"; revision = session.GetProject().Revision; Click("VolumeTimeApplyButton");
            Check(session.GetProject().Revision == revision && time.Text == "1", "duplicate point time retained without mutation"); Refresh();

            var monitor = (PropertySlider)main.FindName("MonitoringControl"); var monitorSlider = (Slider)((Grid)monitor.Content).Children[0];
            var playback = (InteractivePreview)Field("playback")!; double original = playback.MonitoringGain;
            before = Json(); revision = session.GetProject().Revision;
            monitorSlider.RaiseEvent(new DragStartedEventArgs(0, 0)); monitorSlider.Value = .3;
            Check(playback.MonitoringGain == .3 && Json() == before && session.GetProject().Revision == revision, "monitor scope");
            monitorSlider.RaiseEvent(new DragCompletedEventArgs(0, 0, true)); Check(playback.MonitoringGain == original && monitor.Value == original, "monitor cancel restores preference");
            Check(WindowsPreviewAudioOutput.EncodeMonitoringGain(0) == 0 && WindowsPreviewAudioOutput.EncodeMonitoringGain(1) == uint.MaxValue &&
                WindowsPreviewAudioOutput.EncodeMonitoringGain(.5) == 0x80008000, "Windows stereo monitoring conversion");
            var inspector = (StackPanel)main.FindName("ClipInspector"); double width = inspector.Width;
            inspector.Width = 230; await Layout(); Check(value.ActualWidth <= 230 && list.ActualWidth <= 230, "narrow audio layout");
            inspector.Width = width; list.SelectedIndex = 1; await Layout();
            // Keep the complete Audio section in view for review evidence.
            for (DependencyObject? parent = inspector; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll) { scroll.ScrollToBottom(); break; }
            await Layout(); Directory.CreateDirectory("artifacts");
            var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create("artifacts/volume-automation.png")) encoder.Save(output);
            Console.WriteLine("WPF volume point controls, transient preview, cancel/history/stale edits, v3 save/reopen, fades and monitoring scope: PASS");
        }
        finally { File.Delete(file); session.ReplaceProject(null); Set("selectedSequenceId", null); Set("selectedClipId", null); Refresh(); }
        AudioProperties Audio() => session.GetProject().Project!.Sequences[0].Tracks[0].Clips[0].Audio;
        string Json() => NativeProjectCodec.Serialize(session.GetProject().Project!).Value!;
        void Click(string name) => ((Button)main.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        void Refresh() => typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object?[] { null });
        async Task Layout() { main.UpdateLayout(); await main.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ContextIdle); }
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception("Volume: " + name); }
}
