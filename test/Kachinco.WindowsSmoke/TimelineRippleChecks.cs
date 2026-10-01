using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;

internal static class TimelineRippleChecks
{
    public static async Task Run()
    {
        using var session = new EditorSession();
        var sequence = Guid.NewGuid(); var track = Guid.NewGuid(); var asset = Guid.NewGuid();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        const long t = TimelineTime.TicksPerSecond;
        Check(session.Execute(new([
            new CreateProject(Guid.NewGuid(), "Ripple fixture"),
            new CreateSequence(sequence, "Timeline", SequenceSettings.Landscape, 10 * t),
            new RegisterMedia(new(asset, "Missing fixture.mov", "missing.mov", MediaKind.Mov, 10 * t)),
            new AddTrack(sequence, track, "V1", TrackKind.Video),
            new InsertClip(sequence, track, Clip(a, 0, 3 * t)),
            new InsertClip(sequence, track, Clip(b, 3 * t, 5 * t)),
            new InsertClip(sequence, track, Clip(c, 8 * t, 2 * t))])).Success, "fixture");
        var surface = new TimelineSurface { Width = 950, Height = 240 };
        var window = new Window { Content = surface, Width = 990, Height = 300, ShowInTaskbar = false };
        int edits = 0;
        surface.CommandRequested += (_, e) =>
        {
            Check(session.Execute(e.Batch).Success, "shared session edit"); edits++;
            Load();
        };
        window.Show(); Load(); await Layout();
        try
        {
            var original = NativeProjectCodec.Serialize(session.GetProject().Project!).Value;
            var thumb = Begin(c);
            Preview(4 * t);
            Near(Left(c), 240, "inserted clip"); Near(Left(b), 400, "neighbor shift");
            Check(Canvas().Children.OfType<Line>().Any(l => l.Stroke == Brushes.Gold), "insertion marker");
            Check(NativeProjectCodec.Serialize(session.GetProject().Project!).Value == original && edits == 0, "preview mutation");
            thumb.RaiseEvent(new DragCompletedEventArgs(1, 0, true)); await Layout();
            Near(Left(c), 640, "cancelled clip"); Near(Left(b), 240, "cancelled neighbor");
            Check(!surface.HasActiveGesture && edits == 0, "cancel state");
            Check(!Canvas().Children.OfType<Line>().Any(l => l.Stroke == Brushes.Gold), "cancel marker");

            thumb = Begin(c); Preview(4 * t);
            thumb.RaiseEvent(new DragCompletedEventArgs(1, 0, false)); await Layout();
            Check(edits == 1 && surface.SelectedClipId == c, "drop/selection");
            Near(Left(c), 240, "committed clip"); Near(Left(b), 400, "committed neighbor");
            Check(session.Undo().Success, "one Undo"); Load(); await Layout();
            Check(NativeProjectCodec.Serialize(session.GetProject().Project!).Value == original, "Undo restore");
            Check(session.Redo().Success, "one Redo"); Load(); await Layout();
            Near(Left(c), 240, "Redo projection");
            Check(session.Undo().Success, "reset"); Load(); await Layout();

            // Open the actual WPF menu and invoke its routed click path.
            surface.SetCursorTicks(4 * t);
            var menu = Grid(b).ContextMenu!; menu.IsOpen = true; await Layout();
            Check(surface.SelectedClipId == b && menu.Items.Count == 5, "menu selection/actions");
            Check(menu.Items.Cast<MenuItem>().All(i => i.IsEnabled), "interior menu availability");
            menu.IsOpen = false;
            ((MenuItem)menu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Layout();
            Near(Left(b), 0, "context reorder"); Check(edits == 2, "context single batch");
            surface.SetCursorTicks(0);
            menu = Grid(b).ContextMenu!; menu.IsOpen = true; await Layout();
            Check(!((MenuItem)menu.Items[3]).IsEnabled && !((MenuItem)menu.Items[0]).IsEnabled, "disabled earlier/split");
            menu.IsOpen = false;
            ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Layout();
            Check(session.GetProject().Project!.Sequences[0].Tracks[0].Clips.Length == 4, "duplicate command");
            Check(session.Undo().Success, "duplicate undo"); Load(); await Layout();
            surface.DeleteSelected(); await Layout();
            Check(session.GetProject().Project!.Sequences[0].Tracks[0].Clips.Length == 2, "keyboard delete path");
            Console.WriteLine("WPF ripple gap/marker, cancellation, drop, shared Undo/Redo, selection and context actions: PASS");
        }
        finally { surface.DisposeVisualizations(); window.Close(); }

        Clip Clip(Guid id, long start, long duration) => new(id, asset, start, t, duration, true, ClipAppearance.Default, AudioProperties.Default);
        Canvas Canvas() => (Canvas)surface.FindName("TimelineCanvas");
        Grid Grid(Guid id) => Canvas().Children.OfType<Grid>().Single(g => Equals(g.Tag, id));
        double Left(Guid id) => System.Windows.Controls.Canvas.GetLeft(Grid(id));
        void Load() { var snapshot = session.GetProject(); surface.LoadProject(snapshot.Project, sequence, surface.SelectedClipId, snapshot.Revision); }
        async Task Layout() { surface.UpdateLayout(); await surface.Dispatcher.InvokeAsync(() => surface.UpdateLayout(), DispatcherPriority.ContextIdle); }
        Thumb Begin(Guid id)
        {
            var thumb = Grid(id).Children.OfType<Thumb>().First();
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            var state = typeof(TimelineSurface).GetField("drag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
            state.GetType().GetProperty("DeltaX")!.SetValue(state, 1d);
            return thumb;
        }
        void Preview(long ticks) => typeof(TimelineSurface).GetMethod("UpdateRipplePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [ticks]);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("Ripple: " + message); }
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .1, message);
}
