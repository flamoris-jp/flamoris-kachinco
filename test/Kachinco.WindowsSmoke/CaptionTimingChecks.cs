using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;

internal static class CaptionTimingChecks
{
    public static async Task Run()
    {
        using var session = new EditorSession();
        var sequence = Guid.NewGuid(); var track = Guid.NewGuid(); var id = Guid.NewGuid();
        const long t = TimelineTime.TicksPerSecond;
        Check(session.Execute(new([new CreateProject(Guid.NewGuid(), "Caption drag"),
            new CreateSequence(sequence, "Timeline", SequenceSettings.Landscape, 10 * t),
            new AddTrack(sequence, track, "S1", TrackKind.Subtitle),
            new AddCaption(sequence, track, new(id, t, 2 * t, "�̎� / lyric") { Style = new(FontSize: 58) })])).Success, "fixture");
        var surface = new TimelineSurface { Width = 950, Height = 240 };
        var window = new Window { Content = surface, Width = 990, Height = 300, ShowInTaskbar = false };
        int edits = 0;
        surface.CommandRequested += (_, e) => { Check(session.Execute(e.Batch).Success, "shared session"); edits++; Load(); };
        window.Show(); Load(); await Layout();
        try
        {
            var original = NativeProjectCodec.Serialize(session.GetProject().Project!).Value;
            // Drag deltas preview locally; the session changes only on release.
            var body = Begin(0); Delta(body, 40); Delta(body, 40);
            Check(surface.HasActiveGesture && edits == 0, "preview has no edit");
            Check(NativeProjectCodec.Serialize(session.GetProject().Project!).Value == original, "preview preserves project");
            Check(Math.Abs(Canvas.GetLeft(Grid()) - 160) < .1, "preview position");
            body.RaiseEvent(new DragCompletedEventArgs(80, 0, false)); await Layout();
            Check(edits == 1 && Caption().StartTicks == 2 * t && Caption().DurationTicks == 2 * t, "move");
            Check(Caption().Style!.FontSize == 58 && Caption().Text == "�̎� / lyric", "appearance preserved");
            Check(session.Undo().Success, "Undo"); Load();
            Check(NativeProjectCodec.Serialize(session.GetProject().Project!).Value == original, "one Undo restores");
            Check(session.Redo().Success, "Redo"); Load();
            Check(Caption().StartTicks == 2 * t, "Redo timing");
            Check(session.Undo().Success, "reset"); Load();

            var left = Begin(1); Delta(left, 40); left.RaiseEvent(new DragCompletedEventArgs(40, 0, false)); await Layout();
            Check(Caption().StartTicks == t + t / 2 && End() == 3 * t && edits == 2, "start trim");
            var right = Begin(2); Delta(right, 40); right.RaiseEvent(new DragCompletedEventArgs(40, 0, false)); await Layout();
            Check(End() == 3 * t + t / 2 && edits == 3, "end trim");

            var beforeCancel = NativeProjectCodec.Serialize(session.GetProject().Project!).Value;
            body = Begin(0); Delta(body, 80);
            // Invoke the Escape route; no physical mouse capture is needed by this routed gesture test.
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(surface), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            surface.RaiseEvent(key); await Layout();
            Check(key.Handled && !surface.HasActiveGesture && edits == 3, "Escape cancels");
            Check(NativeProjectCodec.Serialize(session.GetProject().Project!).Value == beforeCancel, "cancel retains timing");
            body = Begin(0); Delta(body, 20); Load(); await Layout();
            Check(!surface.HasActiveGesture && edits == 3, "reload cancels");
            body = Begin(0); body.RaiseEvent(new DragCompletedEventArgs(0, 0, false)); await Layout();
            Check(edits == 3 && surface.SelectedClipId == id, "click selects without edit");
            Console.WriteLine("WPF caption move, both edges, previews, selection, Escape/reload cancellation, style and Undo/Redo: PASS");
        }
        finally { surface.DisposeVisualizations(); window.Close(); }
        Caption Caption() => session.GetProject().Project!.Sequences[0].Tracks[0].Captions[0];
        long End() => Caption().StartTicks + Caption().DurationTicks;
        Grid Grid() => ((Canvas)surface.FindName("TimelineCanvas")).Children.OfType<Grid>().Single(g => Equals(g.Tag, id));
        void Load() { var snapshot = session.GetProject(); surface.LoadProject(snapshot.Project, sequence, surface.SelectedClipId, snapshot.Revision); }
        async Task Layout() { surface.UpdateLayout(); await surface.Dispatcher.InvokeAsync(() => surface.UpdateLayout(), DispatcherPriority.ContextIdle); }
        Thumb Begin(int index)
        {
            var thumb = Grid().Children.OfType<Thumb>().ElementAt(index);
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0)); return thumb;
        }
        static void Delta(Thumb thumb, double x) => thumb.RaiseEvent(new DragDeltaEventArgs(x, 0));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("Caption timing: " + message); }
}
