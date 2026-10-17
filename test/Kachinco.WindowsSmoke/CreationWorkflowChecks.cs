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
using Kachinco.Infrastructure;

internal static class CreationWorkflowChecks
{
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kachinco-creation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var main = new MainWindow(); main.Show();
        var session = Field<EditorSession>(main, "session");
        try
        {
            string image = Path.Combine(directory, "image.png");
            var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
            var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(image)) png.Save(stream);
            Guid sequence = Guid.NewGuid(), track = Guid.NewGuid(), asset = Guid.NewGuid(), clip = Guid.NewGuid();
            var edits = session.Execute(new([
                new CreateProject(Guid.NewGuid(), "Creation checks"),
                new CreateSequence(sequence, "Sequence", SequenceSettings.Landscape, 120 * TimelineTime.TicksPerSecond),
                new RegisterMedia(new(asset, "Still", image, MediaKind.Image, TimelineTime.TicksPerSecond)),
                new AddTrack(sequence, track, "V1", TrackKind.Video),
                new InsertClip(sequence, track, new(clip, asset, 0, 0, 120 * TimelineTime.TicksPerSecond, true, ClipAppearance.Default, AudioProperties.Default))
            ]));
            if (!edits.Success) throw new Exception("Creation fixture invalid.");
            Set(main, "selectedSequenceId", sequence);
            Invoke(main, "Refresh", (object?)null);
            var playback = Field<InteractivePreview>(main, "playback");
            var timeline = (TimelineSurface)main.FindName("Timeline");
            playback.Scrub(3 * TimelineTime.TicksPerSecond);
            await playback.Completion;
            timeline.SetCursorTicks(3 * TimelineTime.TicksPerSecond);
            ((Button)main.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playback.Completion;
            if (playback.PositionTicks != 3 * TimelineTime.TicksPerSecond || timeline.PlayheadTicks != playback.PositionTicks)
                throw new Exception("Stop must retain the preview/cursor position.");
            ((Button)main.FindName("GoToStartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playback.Completion;
            if (playback.PositionTicks != 0 || timeline.PlayheadTicks != 0) throw new Exception("Go-to-start disagrees with playback.");
            ((Button)main.FindName("NextFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playback.Completion;
            long frame = TimelineTime.FrameToTicks(1, new(30, 1));
            if (timeline.PlayheadTicks != frame || playback.PositionTicks != frame) throw new Exception("Frame forward must update engine and cursor.");
            ((Button)main.FindName("PreviousFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playback.Completion;
            if (timeline.PlayheadTicks != 0 || playback.PositionTicks != 0) throw new Exception("Frame backward must update engine and cursor.");
            // A start intent at an already-zero cursor must still cancel a later pending seek.
            playback.Scrub(5 * TimelineTime.TicksPerSecond);
            ((Button)main.FindName("GoToStartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await playback.Completion;
            if (playback.PositionTicks != 0) throw new Exception("Repeated zero seek lost to a stale frame.");

            var horizontal = (ScrollBar)timeline.FindName("HorizontalScroll");
            timeline.BeginPlaybackFollow(); timeline.SetPlaybackCursorTicks(20 * TimelineTime.TicksPerSecond);
            if (horizontal.Value <= 0) throw new Exception("Playback follow did not page.");
            var ruler = (Canvas)timeline.FindName("RulerCanvas");
            var lanes = (Canvas)timeline.FindName("TimelineCanvas");
            var a = ruler.Children.OfType<System.Windows.Shapes.Line>().Last();
            var b = lanes.Children.OfType<System.Windows.Shapes.Line>().Last();
            if (Math.Abs(a.X1 - b.X1) > .01) throw new Exception("Follow must retain ruler/playhead alignment.");
            timeline.ShowStart();
            if (horizontal.Value != 0) throw new Exception("Start must reveal zero.");

            string project = Path.Combine(directory, "project.fkproj");
            Set(main, "filename", project); Set(main, "savedJson", null);
            await (Task)Invoke(main, "SaveProjectAsync", false)!;
            if (!File.Exists(project) || (string?)Field<object?>(main, "filename") != project)
                throw new Exception("Known-path save did not publish.");
            var original = File.ReadAllText(project);
            if (!session.Execute(new([new SetTrackEnabled(sequence, track, false)])).Success) throw new Exception("Save edit failed.");
            await (Task)Invoke(main, "SaveProjectAsync", false)!;
            if (File.ReadAllText(project) == original || !(await new ProjectFileStore().LoadAsync(project)).Success)
                throw new Exception("Second save must overwrite without a picker.");
            var saved = Field<string?>(main, "savedJson");
            string invalid = Path.Combine(directory, "missing", "project.fkproj");
            Set(main, "filename", invalid);
            await (Task)Invoke(main, "SaveProjectAsync", false)!;
            if (Field<string?>(main, "filename") != invalid || Field<string?>(main, "savedJson") != saved)
                throw new Exception("Failed save changed filename or dirty baseline.");
            playback.SetContext(null); await playback.Completion;
            await CheckTitleFailure(main, session, "TitlePreview_Click");
            await CheckTitleFailure(main, session, "TitleExport_Click");
            Set(main, "filename", project);
            if (!session.Execute(new([new SetTrackEnabled(sequence, track, true), new SetTitleImage(sequence, new(asset))])).Success)
                throw new Exception("Title error fixture invalid.");
            File.Move(image, image + ".offline");
            try
            {
                await CheckTitleFailure(main, session, "TitlePreview_Click", "Video source is missing.");
                await CheckTitleFailure(main, session, "TitleFromFrame_Click", "Video source is missing.");
            }
            finally { File.Move(image + ".offline", image); }
            Console.WriteLine("Creation workflow: WPF transport, page follow and atomic known-path save: PASS");
            Console.WriteLine("Title UI: no selection and missing source report errors without unhandled exceptions, edits or a stuck busy state: PASS");
        }
        finally
        {
            var endingPlayback = Field<InteractivePreview>(main, "playback");
            endingPlayback.SetContext(null); await endingPlayback.Completion;
            session.ReplaceProject(null); Set(main, "savedJson", null);
            main.Close();
            await main.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, true);
        }
    }
    private static async Task CheckTitleFailure(MainWindow main, EditorSession session, string handler, string? expected = null)
    {
        var errors = new List<Exception>();
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        main.Dispatcher.UnhandledException += Unhandled;
        try
        {
            var before = session.GetProject();
            string saved = ProjectJson.Serialize(before.Project!).Value!;
            var status = (TextBlock)main.FindName("Status"); status.Text = "";
            Invoke(main, handler, main, new RoutedEventArgs());
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Field<bool>(main, "busy") && DateTime.UtcNow < deadline) await Task.Delay(10);
            await main.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
            if (errors.Count != 0) throw new Exception(handler + " leaked an exception.", errors[0]);
            if (Field<bool>(main, "busy") || !main.IsEnabled) throw new Exception(handler + " left the window busy.");
            if (string.IsNullOrWhiteSpace(status.Text) || expected is not null && status.Text != expected)
                throw new Exception(handler + " did not report the title error: " + status.Text);
            var after = session.GetProject();
            if (before.Revision != after.Revision || before.CanUndo != after.CanUndo || before.CanRedo != after.CanRedo ||
                saved != ProjectJson.Serialize(after.Project!).Value)
                throw new Exception(handler + " changed the project/history on failure.");
        }
        finally { main.Dispatcher.UnhandledException -= Unhandled; }
    }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
}
