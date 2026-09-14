using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;

internal static class TimelineLayoutChecks
{
    public static async Task Run(MainWindow main)
    {
        var import = (Button)main.FindName("ImportButton");
        var welcome = (FrameworkElement)main.FindName("WelcomePanel");
        if (!import.IsEnabled || !welcome.IsVisible || string.IsNullOrWhiteSpace(((TextBlock)main.FindName("WelcomeText")).Text))
            throw new Exception("Startup must offer enabled import with actionable guidance.");
        var session = new EditorSession(); var sequence = Guid.NewGuid(); var asset = Guid.NewGuid(); var clip = Guid.NewGuid();
        var tracks = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var commands = new List<EditCommand> {
            new CreateProject(Guid.NewGuid(), "Layout fixture"),
            new CreateSequence(sequence, "Geometry", SequenceSettings.Landscape, 120 * TimelineTime.TicksPerSecond),
            new RegisterMedia(new(asset, "Missing fixture.mov", "missing.mov", MediaKind.Mov, 10 * TimelineTime.TicksPerSecond, null, null))
        };
        foreach (var track in tracks) commands.Add(new AddTrack(sequence, track, "映像", TrackKind.Video));
        commands.Add(new InsertClip(sequence, tracks[7], new(clip, asset, 8 * TimelineTime.TicksPerSecond, 0, 5 * TimelineTime.TicksPerSecond, true, ClipAppearance.Default, AudioProperties.Default)));
        if (!session.Execute(new([.. commands])).Success) throw new Exception("Invalid geometry fixture.");
        var surface = new TimelineSurface { Width = 760, Height = 250 };
        var host = new Window { Width = 800, Height = 320, Content = surface, ShowInTaskbar = false };
        host.Show(); surface.LoadProject(session.GetProject().Project, sequence, clip);
        try
        {
            foreach (double width in new[] {760d, 490d})
            {
                surface.Width = width;
                foreach (decimal zoom in new[] {1m, .8m, 1.25m})
                {
                    surface.ZoomBy(zoom); await Layout(surface);
                    var horizontal = (ScrollBar)surface.FindName("HorizontalScroll");
                    foreach (double x in new[] {0d, 123.5, horizontal.Maximum})
                    foreach (double y in new[] {0d, 113d, 900d})
                    {
                        horizontal.Value = x;
                        ((ScrollViewer)surface.FindName("TimelineScroll")).ScrollToVerticalOffset(y);
                        surface.SetCursorTicks(8 * TimelineTime.TicksPerSecond);
                        await Layout(surface);
                        Check(surface, clip);
                    }
                }
                surface.FitSequence(); await Layout(surface); Check(surface, clip);
            }
            Console.WriteLine("WPF semantic geometry: header/lane rows, clip bounds, ruler/playhead origins, resize/zoom/horizontal/vertical scroll: PASS");
        }
        finally { surface.DisposeVisualizations(); host.Close(); }
    }
    private static async Task Layout(FrameworkElement element)
    {
        element.UpdateLayout();
        await element.Dispatcher.InvokeAsync(() => element.UpdateLayout(), DispatcherPriority.ContextIdle);
    }
    private static void Check(TimelineSurface surface, Guid clipId)
    {
        var headers = (Canvas)surface.FindName("TrackHeaders");
        var lanes = (Canvas)surface.FindName("TimelineCanvas");
        var ruler = (Canvas)surface.FindName("RulerCanvas");
        var viewport = (FrameworkElement)surface.FindName("TimelineViewportHost");
        var rulerViewport = (FrameworkElement)surface.FindName("RulerViewportHost");
        Near(viewport.ActualWidth, rulerViewport.ActualWidth, "ruler/lane viewport width");
        Near(viewport.TranslatePoint(new(0,0), surface).X, rulerViewport.TranslatePoint(new(0,0),surface).X, "ruler/lane origin");
        foreach (Border header in headers.Children)
        {
            var lane = lanes.Children.OfType<Rectangle>().Single(r => Equals(r.Tag, header.Tag));
            Near(header.TranslatePoint(new(0,0),surface).Y, lane.TranslatePoint(new(0,0),surface).Y, "row top");
            Near(header.TranslatePoint(new(0,header.ActualHeight),surface).Y,
                lane.TranslatePoint(new(0,lane.ActualHeight),surface).Y, "row bottom/separator");
        }
        var body = lanes.Children.OfType<Grid>().Single(g => Equals(g.Tag,clipId));
        double top = Canvas.GetTop(body);
        var containing = lanes.Children.OfType<Rectangle>().Single(r => top >= Canvas.GetTop(r) && top < Canvas.GetTop(r) + r.ActualHeight);
        if (top + body.ActualHeight > Canvas.GetTop(containing) + containing.ActualHeight) throw new Exception("Clip escapes lane.");
        Near((double)new TimelineViewport(surface.PixelsPerSecond).TicksToPixels(5 * TimelineTime.TicksPerSecond), body.ActualWidth, "clip time width");
        var rulerLine = ruler.Children.OfType<Line>().Last();
        var laneLine = lanes.Children.OfType<Line>().Last();
        double rulerX = ruler.TranslatePoint(new(rulerLine.X1,0),surface).X;
        double laneX = lanes.TranslatePoint(new(laneLine.X1,0),surface).X;
        Near(rulerX,laneX,"ruler/playhead"); Near(laneX,body.TranslatePoint(new(0,0),surface).X,"clip/playhead");
        var coordinates = new TimelineCoordinates(new(surface.PixelsPerSecond), (decimal)((ScrollBar)surface.FindName("HorizontalScroll")).Value);
        Near((double)coordinates.ViewX(8 * TimelineTime.TicksPerSecond), laneX - viewport.TranslatePoint(new(0,0),surface).X,"scrolled coordinates");
        var thumb = body.Children.OfType<Thumb>().First(); thumb.ApplyTemplate();
        if (VisualTreeHelper.GetChild(thumb,0) is not Border { Background: SolidColorBrush brush } || brush.Color.A != 0)
            throw new Exception("Move affordance hides media under native Thumb chrome.");
    }
    private static void Near(double a,double b,string name)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || Math.Abs(a-b) > .1) throw new Exception($"{name}: {a} != {b}");
    }
}
