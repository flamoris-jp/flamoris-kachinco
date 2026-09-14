using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;

namespace Kachinco.App;

public sealed class CaptionEditorWindow : Window
{
    private readonly EditorSession session;
    private readonly Guid sequenceId;
    private readonly ListBox list = new() { DisplayMemberPath = "Text", Height = 180 };
    private readonly TextBox start = new(), duration = new(), text = new() { AcceptsReturn = true, Height = 100, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    public CaptionEditorWindow(EditorSession session, Guid sequenceId)
    {
        this.session = session; this.sequenceId = sequenceId;
        Title = "字幕を編集"; Width = 520; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock { Text = "開始（秒）" }); panel.Children.Add(start);
        panel.Children.Add(new TextBlock { Text = "長さ（秒）" }); panel.Children.Add(duration);
        panel.Children.Add(new TextBlock { Text = "字幕" }); panel.Children.Add(text);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        foreach (var (label, handler) in new (string, RoutedEventHandler)[] { ("新規", New), ("適用", Save), ("削除", Delete) })
        { var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(10,4,10,4) }; button.Click += handler; buttons.Children.Add(button); }
        panel.Children.Add(status);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not Caption caption) return;
            start.Text = Seconds(caption.StartTicks); duration.Text = Seconds(caption.DurationTicks); text.Text = caption.Text;
        };
        Refresh(); New(this, new());
    }
    private Sequence Sequence => session.GetProject().Project!.Sequences.First(s => s.Id == sequenceId);
    private void Refresh() => list.ItemsSource = Sequence.Tracks.SelectMany(t => t.Captions).OrderBy(c => c.StartTicks).ToArray();
    private void New(object sender, RoutedEventArgs e) { list.SelectedItem = null; start.Text = "0"; duration.Text = "3"; text.Text = ""; }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(start.Text, out var from) || !decimal.TryParse(duration.Text, out var length) || from < 0 || length <= 0 || string.IsNullOrWhiteSpace(text.Text))
        { status.Text = "開始・長さ・字幕を入力してください。"; return; }
        try
        {
            long ticks = TimelineTime.SecondsToTicks(from), count = TimelineTime.SecondsToTicks(length), end = checked(ticks + count);
            var commands = new List<EditCommand>();
            if (end > Sequence.DurationTicks) commands.Add(new SetSequenceDuration(sequenceId, end));
            if (list.SelectedItem is Caption caption) commands.Add(new UpdateCaption(sequenceId, caption.Id, ticks, count, text.Text, caption.Enabled));
            else
            {
                var track = Sequence.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Subtitle);
                var id = track?.Id ?? Guid.NewGuid();
                if (track is null) commands.Add(new AddTrack(sequenceId, id, "字幕", TrackKind.Subtitle));
                commands.Add(new AddCaption(sequenceId, id, new(Guid.NewGuid(), ticks, count, text.Text)));
            }
            Apply(commands);
        }
        catch (OverflowException) { status.Text = "時間が大きすぎます。"; }
    }
    private void Delete(object sender, RoutedEventArgs e) { if (list.SelectedItem is Caption caption) Apply([new DeleteCaption(sequenceId, caption.Id)]); }
    private void Apply(IEnumerable<EditCommand> commands)
    {
        var result = session.Execute(new([.. commands], session.GetProject().Revision));
        status.Text = result.Success ? "字幕を変更しました。" : string.Join(" / ", result.Diagnostics.Select(d => d.Message));
        if (result.Success) Refresh();
    }
    private static string Seconds(long ticks) => ((decimal)ticks / TimelineTime.TicksPerSecond).ToString("0.###", CultureInfo.CurrentCulture);
}
