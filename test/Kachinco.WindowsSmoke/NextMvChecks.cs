using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class NextMvChecks
{
    public static async Task Run()
    {
        using var session = new EditorSession();
        long duration = 5 * TimelineTime.TicksPerSecond;
        var asset = new MediaAsset(Guid.NewGuid(), "Embedded", "missing-draft.ktproj", MediaKind.KineticText, duration)
            { TextComposition = KineticComposition.New().ToJsonString() };
        Guid sequence = Guid.NewGuid(), video = Guid.NewGuid(), audio = Guid.NewGuid(), subtitle = Guid.NewGuid(), clip = Guid.NewGuid();
        Check(session.Execute(new([
            new CreateProject(Guid.NewGuid(), "MV UI"), new CreateSequence(sequence, "Main", SequenceSettings.Landscape, duration),
            new RegisterMedia(asset), new AddTrack(sequence, video, "Text", TrackKind.Video),
            new AddTrack(sequence, audio, "Audio", TrackKind.Audio), new AddTrack(sequence, subtitle, "Captions", TrackKind.Subtitle),
            new InsertClip(sequence, video, new(clip, asset.Id, 0, 0, duration, true, ClipAppearance.Default, AudioProperties.Default)),
            new AddCaption(sequence, subtitle, new(Guid.NewGuid(), 0, duration, "Caption"))])).Success, "fixture");
        var surface = new TimelineSurface { Width = 760, Height = 620 };
        var host = new Window { Width = 800, Height = 700, Content = surface, ShowInTaskbar = false };
        void Reload() { var snapshot = session.GetProject(); surface.LoadProject(snapshot.Project, sequence, clip, snapshot.Revision); }
        surface.CommandRequested += (_, e) => { Check(session.Execute(e.Batch).Success, "guarded native command"); Reload(); };
        host.Show(); Reload();
        try
        {
            await surface.Dispatcher.InvokeAsync(() => { surface.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            var canvas = (Canvas)surface.FindName("TimelineCanvas");
            var grid = canvas.Children.OfType<Grid>().Single(g => Equals(g.Tag, clip));
            Check(Descendants<TextBlock>(grid).Any(t => t.Text.Contains("FLAMORIS")), "embedded text visible without draft file");
            Check(!Descendants<TextBlock>(grid).Any(t => t.Text.Contains("表示を作成できません") || t.Text.Contains("Visualization unavailable")), "no FFmpeg error for text");
            Check(surface.PendingVisualizations == 0 && surface.VisualizationWorkers == 0, "text never queues file decoder");
            foreach (var item in canvas.Children.OfType<Grid>())
                foreach (var trim in item.Children.OfType<Thumb>().Where(t => t.HorizontalAlignment is HorizontalAlignment.Left or HorizontalAlignment.Right))
                    Check(trim.Cursor == Cursors.SizeWE && trim.ForceCursor && Panel.GetZIndex(trim) == 2, "visible trim affordance");
            var menu = grid.ContextMenu!;
            var prepare = typeof(TimelineSurface).GetMethod("PrepareClipMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check((bool)prepare.Invoke(surface, [menu, clip])!, "context menu");
            Guid? edited = null;
            surface.KineticEditRequested += (_, e) => edited = e.ClipId;
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(edited == clip, "context editing targets the clicked clip");
            foreach (var track in new[] { video, audio, subtitle })
            {
                var headers = (Canvas)surface.FindName("TrackHeaders");
                var header = headers.Children.OfType<Border>().Single(b => Equals(b.Tag, track));
                var toggle = Descendants<CheckBox>(header).Single();
                toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var project = session.GetProject().Project!;
                Check(!project.Sequences[0].Tracks.Single(t => t.Id == track).Enabled, "track disabled");
                Check(project.Sequences[0].Tracks.SelectMany(t => t.Clips).Single().Enabled, "clip flag preserved");
                var saved = ProjectJson.Serialize(project);
                Check(saved.Success && !ProjectJson.Deserialize(saved.Value!).Value!.Sequences[0].Tracks.Single(t => t.Id == track).Enabled, "track state saved and reopened");
                Check(session.Undo().Success, "toggle undo"); Reload();
                Check(session.GetProject().Project!.Sequences[0].Tracks.Single(t => t.Id == track).Enabled, "undo restores track");
                Check(session.Redo().Success, "toggle redo"); Reload();
                Check(!session.GetProject().Project!.Sequences[0].Tracks.Single(t => t.Id == track).Enabled, "redo disables track");
            }
            Console.WriteLine("MV UI: embedded text, trim cursor, track history/persistence, right-click target: PASS");
        }
        finally { surface.DisposeVisualizations(); host.Close(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("MV UI: " + message); }
}
