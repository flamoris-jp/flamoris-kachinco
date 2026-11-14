using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;

namespace Kachinco.App;

public sealed class CaptionEditorWindow : Window
{
    private readonly EditorSession session;
    private readonly Guid sequenceId;
    private readonly TrackKind kind;
    private bool IsNote => kind == TrackKind.EditNote;
    private string ItemName => IsNote ? EditorText.Choose("編集メモ", "Editing note") : EditorText.Choose("字幕", "Caption");
    private readonly ListBox list = new() { DisplayMemberPath = "Text", Height = 180 };
    private readonly TextBox start = new(), duration = new(), text = new() { AcceptsReturn = true, Height = 100, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CaptionStyleEditor styles = new();
    private readonly Action? onChanged;
    public CaptionEditorWindow(EditorSession session, Guid sequenceId, Action? onChanged = null, bool selectAll = false, TrackKind kind = TrackKind.Subtitle)
    {
        if (kind is not (TrackKind.Subtitle or TrackKind.EditNote)) throw new ArgumentOutOfRangeException(nameof(kind));
        this.kind = kind; this.session = session; this.sequenceId = sequenceId; this.onChanged = onChanged;
        list.SelectionMode = SelectionMode.Extended;
        Title = IsNote ? EditorText.Choose("編集メモを編集", "Edit editing notes") : EditorText.Choose("字幕を編集", "Edit captions"); Width = 600; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(list);
        var selectAllButton = new Button { Content = EditorText.Choose("全字幕を選択してスタイルを一括変更", "Select all captions to change style"), Margin = new Thickness(0,4,0,8) };
        selectAllButton.Click += (_, _) => list.SelectAll();
        if (!IsNote) panel.Children.Add(selectAllButton);
        panel.Children.Add(new TextBlock { Text = "開始（秒）" }); panel.Children.Add(start);
        panel.Children.Add(new TextBlock { Text = "長さ（秒）" }); panel.Children.Add(duration);
        panel.Children.Add(new TextBlock { Text = ItemName }); panel.Children.Add(text);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        foreach (var (label, handler) in new (string, RoutedEventHandler)[] { ("新規", New), ("適用", Save), ("削除", Delete) })
        { var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(10,4,10,4) }; button.Click += handler; buttons.Children.Add(button); }
        panel.Children.Add(status); if (!IsNote) panel.Children.Add(styles);
        list.SelectionChanged += (_, _) =>
        {
            RefreshStyles();
            if (list.SelectedItem is not Caption caption) return;
            start.Text = Seconds(caption.StartTicks); duration.Text = Seconds(caption.DurationTicks); text.Text = caption.Text;
        };
        Refresh();
        if (selectAll) list.SelectAll(); else New(this, new());
    }
    private void RefreshStyles() => styles.Configure(session,sequenceId,list.SelectedItems.Cast<Caption>().Select(c=>c.Id),result=>
    {
        if(result.Success) { RefreshStyles(); onChanged?.Invoke(); }
        else status.Text=string.Join(" / ",result.Diagnostics.Select(d=>d.Message));
    });
    private Sequence Sequence => session.GetProject().Project!.Sequences.First(s => s.Id == sequenceId);
    private void Refresh() => list.ItemsSource = Sequence.Tracks.Where(t => t.Kind == kind).SelectMany(t => t.Captions).OrderBy(c => c.StartTicks).ToArray();
    private void New(object sender, RoutedEventArgs e) { list.SelectedItem = null; start.Text = "0"; duration.Text = "3"; text.Text = ""; }
    private void Save(object sender, RoutedEventArgs e)
    {
        if(list.SelectedItems.Count > 1) { status.Text=EditorText.Choose("本文・時間の編集は1つ選択してください。", "Select one item to edit text and timing.");return; }
        if (!decimal.TryParse(start.Text, out var from) || !decimal.TryParse(duration.Text, out var length) || from < 0 || length <= 0 || string.IsNullOrWhiteSpace(text.Text))
        { status.Text = EditorText.Choose("開始・長さ・本文を入力してください。", "Enter start, duration and text."); return; }
        try
        {
            long ticks = TimelineTime.SecondsToTicks(from), count = TimelineTime.SecondsToTicks(length), end = checked(ticks + count);
            var commands = new List<EditCommand>();
            if (end > Sequence.DurationTicks) commands.Add(new SetSequenceDuration(sequenceId, end));
            if (list.SelectedItem is Caption caption) commands.Add(new UpdateCaption(sequenceId, caption.Id, ticks, count, text.Text, caption.Enabled));
            else
            {
                var track = Sequence.Tracks.FirstOrDefault(t => t.Kind == kind);
                var id = track?.Id ?? Guid.NewGuid();
                if (track is null) commands.Add(new AddTrack(sequenceId, id, ItemName, kind));
                commands.Add(new AddCaption(sequenceId, id, new(Guid.NewGuid(), ticks, count, text.Text)));
            }
            Apply(commands);
        }
        catch (OverflowException) { status.Text = "時間が大きすぎます。"; }
    }
    private void Delete(object sender, RoutedEventArgs e) { var ids=list.SelectedItems.Cast<Caption>().Select(c=>c.Id).ToArray(); if(ids.Length>0) Apply(ids.Select(id=>(EditCommand)new DeleteCaption(sequenceId,id))); }
    private void Apply(IEnumerable<EditCommand> commands)
    {
        var result = session.Execute(new([.. commands], session.GetProject().Revision));
        status.Text = result.Success ? EditorText.Choose($"{ItemName}を変更しました。", $"{ItemName} updated.") : string.Join(" / ", result.Diagnostics.Select(d => d.Message));
        if (result.Success) { Refresh(); onChanged?.Invoke(); }
    }
    private static string Seconds(long ticks) => ((decimal)ticks / TimelineTime.TicksPerSecond).ToString("0.###", CultureInfo.CurrentCulture);
}
