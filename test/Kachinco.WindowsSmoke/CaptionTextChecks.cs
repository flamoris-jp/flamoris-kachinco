using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class CaptionTextChecks
{
    public static async Task Run(MainWindow main)
    {
        const long t = TimelineTime.TicksPerSecond;
        var session = (EditorSession)Get("session")!;
        var sequence = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "caption-inspector-" + Guid.NewGuid().ToString("N") + ".fkproj");
        Check(session.Execute(new([new CreateProject(Guid.NewGuid(), "Caption inspector"),
            new CreateSequence(sequence, "S", SequenceSettings.Landscape, 8 * t)])).Success, "fixture");
        Set("selectedSequenceId", sequence); Refresh();
        var timeline = (TimelineSurface)main.FindName("Timeline");
        typeof(TimelineSurface).GetField("playheadTicks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(timeline, 2 * t);
        Invoke("CreateCaptionAtPlayhead_Click", main, new RoutedEventArgs());
        var original = session.GetProject().Project!.Sequences[0].Tracks.SelectMany(track => track.Captions).Single();
        Check(original.StartTicks == 2 * t && original.DurationTicks == 3 * t, "create at playhead");
        Check((Guid?)Get("selectedClipId") == original.Id, "new clip selection");
        Check(session.Execute(new([new SetCaptionStyle(sequence, original.Id, new(Color: "#FFCC00", FontSize: 64)),
            new UpdateCaption(sequence, original.Id, original.StartTicks, original.DurationTicks, original.Text, false)])).Success, "style and disabled fixture");
        Refresh();
        var editor = (CaptionTextEditor)main.FindName("CaptionTextInspector");
        var box = editor.Children.OfType<TextBox>().Single();
        try
        {
            Check(editor.Visibility == Visibility.Visible && box.Text == original.Text, "selected caption inspector");
            long revision = session.GetProject().Revision;
            box.Text = "編集した字幕\nSecond line";
            Check(editor.HasChanges && session.GetProject().Revision == revision, "draft isolation");
            editor.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(session.GetProject().Revision == revision + 1 && Caption().Text == box.Text, "one apply transaction");
            Check(Caption().Id == original.Id && Caption().StartTicks == 2 * t && Caption().DurationTicks == 3 * t &&
                !Caption().Enabled && Caption().Style!.FontSize == 64 && Caption().Style!.Color == "#FFCC00", "text edit preserves caption properties");
            Check(session.Undo().Success && Caption().Text == original.Text, "text Undo");
            Check(session.Redo().Success && Caption().Text == "編集した字幕\nSecond line", "text Redo");
            Refresh();
            box.Text = " "; revision = session.GetProject().Revision;
            Check(!editor.TryApply() && editor.HasChanges && session.GetProject().Revision == revision, "empty draft rejection");
            box.Text = "保存ボタン前の字幕\nUnsaved draft";
            Set("filename", path);
            await (Task)typeof(MainWindow).GetMethod("SaveProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [false])!;
            var saved = await new ProjectFileStore().LoadAsync(path);
            Check(saved.Success && saved.Value!.Sequences[0].Tracks[0].Captions[0].Text == box.Text, "Save flushes pending text before snapshot");
            Check(!editor.HasChanges && !(bool)Invoke("IsDirty")!, "saved project is clean");
            Check(saved.Value!.Sequences[0].Tracks[0].Captions[0].Style!.Color == "#FFCC00", "save preserves style");
            var srt = SrtCodec.Write([Caption() with { Enabled = true }]);
            Check(srt.Success && SrtCodec.Parse(srt.Value!).Value[0].Text == Caption().Text, "edited SRT roundtrip");

            // Selecting another caption can commit text and rebuild the timeline during DragStarted.
            // A detached old gesture must never write its old text back into the project.
            var imported = SrtCodec.Parse("1\n00:00:00,000 --> 00:00:01,000\nImported clip\n").Value;
            Check(session.Execute(CaptionImport.Plan(session.GetProject().Project!.Sequences[0], imported, 0, session.GetProject().Revision)).Success, "imported caption fixture");
            Refresh();
            box.Text = "Draft before selecting imported caption";
            var canvas = (Canvas)timeline.FindName("TimelineCanvas");
            var importedGrid = canvas.Children.OfType<Grid>().Single(g => Equals(g.Tag, imported[0].Id));
            var oldThumb = importedGrid.Children.OfType<Thumb>().First();
            oldThumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            oldThumb.RaiseEvent(new DragDeltaEventArgs(40, 0));
            oldThumb.RaiseEvent(new DragCompletedEventArgs(40, 0, false));
            Check(!timeline.HasActiveGesture && Caption().Text == "Draft before selecting imported caption", "selection-time apply cancels detached gesture");
            Check((Guid?)Get("selectedClipId") == imported[0].Id && box.Text == "Imported clip", "SRT caption selection populates inspector");
            Set("selectedClipId", original.Id); Refresh();

            box.Text = "Concurrent draft";
            Check(session.Execute(new([new UpdateCaption(sequence, original.Id, 2*t, 3*t, "Other writer", false)])).Success, "concurrent text edit");
            Check(!editor.TryApply() && Caption().Text == "Other writer", "stale text cannot overwrite");
            editor.Clear(); Refresh();
            box.Text = "Replacement document draft";
            Check(session.ReplaceProject(session.GetProject().Project).Success, "document replacement");
            Check(!editor.TryApply() && Caption().Text == "Other writer", "document guard");
            editor.Clear(); Refresh();
            box.Text = "Selection change saves this caption";
            Invoke("Timeline_ClipSelectionChanged", timeline, new TimelineSelectionEventArgs(null));
            Check(Caption().Text == "Selection change saves this caption" && editor.Visibility == Visibility.Collapsed, "selection applies old caption before switching");
            Console.WriteLine("Caption creation, inspector text, draft/save, history, SRT, style preservation and stale/document guards: PASS");
        }
        finally
        {
            editor.Clear(); session.ReplaceProject(null);
            Set("selectedSequenceId", null); Set("selectedClipId", null); Set("filename", null); Set("savedJson", null); Refresh();
            File.Delete(path);
        }
        Caption Caption() => session.GetProject().Project!.Sequences[0].Tracks.SelectMany(track => track.Captions).Single(c => c.Id == original.Id);
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
        void Refresh() => Invoke("Refresh", (object?)null);
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception("Caption text: " + label); }
}
