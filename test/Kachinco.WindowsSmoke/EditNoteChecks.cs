using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class EditNoteChecks
{
    public static void Run(MainWindow main)
    {
        const long t = TimelineTime.TicksPerSecond;
        var session = (EditorSession)Get("session")!;
        var sequence = Guid.NewGuid();
        Check(session.Execute(new([new CreateProject(Guid.NewGuid(), "Editing notes"),
            new CreateSequence(sequence, "S", SequenceSettings.Landscape, 8 * t)])).Success, "fixture");
        Set("selectedSequenceId", sequence); Refresh();
        var timeline = (TimelineSurface)main.FindName("Timeline");
        typeof(TimelineSurface).GetField("playheadTicks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(timeline, 2 * t);
        Invoke("CreateEditNote_Click", main, new RoutedEventArgs());
        try
        {
            var track = session.GetProject().Project!.Sequences[0].Tracks.Single();
            var note = track.Captions.Single();
            Check(track.Kind == TrackKind.EditNote && note.StartTicks == 2 * t && note.DurationTicks == 3 * t, "M track at cursor");
            var editor = (CaptionTextEditor)main.FindName("CaptionTextInspector");
            Check(editor.Visibility == Visibility.Visible && ((CaptionStyleEditor)main.FindName("CaptionInspector")).Visibility == Visibility.Collapsed, "note text without subtitle styling");
            var box = editor.Children.OfType<TextBox>().Single(); box.Text = "ここで急に引く";
            Check(editor.TryApply(), "shared text command");
            Invoke("RefreshEditNoteOverlay");
            var overlay = (Border)main.FindName("EditNotesOverlay");
            Check(overlay.Visibility == Visibility.Visible && ((TextBlock)main.FindName("EditNotesOverlayText")).Text.Contains("急に引く"), "active native note overlay");
            var show = (MenuItem)main.FindName("ShowEditNotesMenu"); show.IsChecked = false; Invoke("RefreshEditNoteOverlay");
            Check(overlay.Visibility == Visibility.Collapsed, "transient hide");
            show.IsChecked = true;
            typeof(TimelineSurface).GetField("playheadTicks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(timeline, 5 * t);
            Invoke("RefreshEditNoteOverlay"); Check(overlay.Visibility == Visibility.Collapsed, "half-open end");
            var project = session.GetProject().Project!;
            Check(!SrtCodec.Write(project.Sequences[0]).Value!.Contains("急に引く"), "no subtitle export leakage");
            var reopened = ProjectJson.Deserialize(ProjectJson.Serialize(project).Value!);
            Check(reopened.Success && reopened.Value!.Sequences[0].Tracks[0].Captions[0].Text == box.Text, "save/reopen");
            var list = new CaptionEditorWindow(session, sequence);
            Check(((ListBox)((StackPanel)((ScrollViewer)list.Content).Content).Children[0]).Items.Count == 0, "subtitle list excludes notes");
            list.Close();
            list = new CaptionEditorWindow(session, sequence, kind: TrackKind.EditNote);
            Check(((ListBox)((StackPanel)((ScrollViewer)list.Content).Content).Children[0]).Items.Count == 1, "dedicated note list");
            list.Close();
            Console.WriteLine("Editing note creation, inspector, native overlay, visibility, subtitle isolation and reload: PASS");
        }
        finally
        {
            ((CaptionTextEditor)main.FindName("CaptionTextInspector")).Clear();
            session.ReplaceProject(null); Set("selectedSequenceId", null); Set("selectedClipId", null); Refresh();
        }
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
        void Refresh() => Invoke("Refresh", (object?)null);
    }
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Editing notes: " + name); }
}
