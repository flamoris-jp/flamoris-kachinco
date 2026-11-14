using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kachinco.Core;

namespace Kachinco.App;

// A draft for one selected caption. Only UpdateCaption commits project state.
public sealed class CaptionTextEditor : StackPanel
{
    private readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        MinHeight = 90, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock heading = new() { Margin = new(0,0,0,4) };
    private readonly Button apply = new() { Margin = new(0,4,0,4) };
    private bool editNote;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private EditorSession? session;
    private ProjectSnapshot? baseline;
    private Caption? caption;
    private Guid sequenceId;
    private string? document;
    private Action<EditResult>? changed;
    public bool HasChanges => caption is not null && text.Text != caption.Text;

    public CaptionTextEditor()
    {
        heading.Text = T("字幕クリップの文章", "Caption clip text"); Children.Add(heading);
        Children.Add(text);
        apply.Content = T("字幕を適用", "Apply caption");
        apply.Click += (_, _) => TryApply();
        Children.Add(apply);
        Children.Add(new TextBlock { Text = T("Ctrl+Enterで適用。Ctrl+Sでプロジェクト保存。", "Ctrl+Enter applies. Ctrl+S saves the project."), TextWrapping = TextWrapping.Wrap });
        Children.Add(status);
        text.LostKeyboardFocus += (_, _) => TryApply();
        text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { TryApply(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Reload(); e.Handled = true; }
        };
    }

    public void Configure(EditorSession editor, Guid sequence, Guid captionId, Action<EditResult> onChanged, bool isEditNote = false)
    {
        editNote = isEditNote;
        heading.Text = editNote ? T("編集メモの文章（書出し対象外）", "Editing note text (never exported)") : T("字幕クリップの文章", "Caption clip text");
        apply.Content = editNote ? T("メモを適用", "Apply note") : T("字幕を適用", "Apply caption");
        bool keepDraft = session == editor && document == editor.DocumentToken && sequenceId == sequence && caption?.Id == captionId && HasChanges;
        session = editor; sequenceId = sequence; changed = onChanged;
        if (keepDraft)
        {
            var current = editor.GetProject();
            var latest = current.Project?.Sequences.FirstOrDefault(s => s.Id == sequence)?.Tracks.SelectMany(t => t.Captions).FirstOrDefault(c => c.Id == captionId);
            // Unrelated/style edits can refresh the revision without overwriting a text draft.
            if (latest is not null && caption is not null && latest.Text == caption.Text)
            { baseline = current; caption = latest; }
            return;
        }
        baseline = editor.GetProject(); document = editor.DocumentToken;
        caption = baseline.Project?.Sequences.FirstOrDefault(s => s.Id == sequence)?.Tracks.SelectMany(t => t.Captions).FirstOrDefault(c => c.Id == captionId);
        text.Text = caption?.Text ?? ""; status.Text = "";
    }

    public bool TryApply(bool notify = true)
    {
        if (!HasChanges) return true;
        if (session is null || baseline is null || caption is null || document != session.DocumentToken)
        { status.Text = T("プロジェクトが変わりました。字幕を選び直してください。", "Project changed. Select the caption again."); return false; }
        if (string.IsNullOrWhiteSpace(text.Text))
        { status.Text = editNote ? T("編集メモを入力してください。", "Enter editing note text.") : T("字幕の文章を入力してください。", "Enter caption text."); return false; }
        var result = session.Execute(new([new UpdateCaption(sequenceId, caption.Id, caption.StartTicks,
            caption.DurationTicks, text.Text, caption.Enabled)], baseline.Revision));
        if (!result.Success)
        { status.Text = string.Join(" / ", result.Diagnostics.Select(d => d.Message)); return false; }
        caption = caption with { Text = text.Text }; baseline = session.GetProject();
        status.Text = editNote ? T("メモを適用しました。プロジェクト保存でファイルに残せます。", "Note applied. Save the project to keep it on disk.") : T("字幕を適用しました。プロジェクト保存でファイルに残せます。", "Caption applied. Save the project to keep it on disk.");
        if (notify) changed?.Invoke(result);
        return true;
    }

    public void Clear()
    {
        caption = null; baseline = null; document = null; text.Text = ""; status.Text = "";
    }

    private void Reload()
    {
        if (session is null || caption is null || document != session.DocumentToken) return;
        baseline = session.GetProject();
        caption = baseline.Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId)?.Tracks.SelectMany(t => t.Captions).FirstOrDefault(c => c.Id == caption.Id);
        text.Text = caption?.Text ?? ""; status.Text = "";
    }
    private static string T(string ja, string en) => EditorText.Choose(ja, en);
}
