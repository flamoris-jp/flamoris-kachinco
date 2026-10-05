using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;
using Kachinco.Infrastructure;

namespace Kachinco.App;

public sealed class EffectLibraryWindow : Window
{
    private readonly EditorSession session;
    private readonly Guid? sequenceId, clipId;
    private readonly EffectLibraryPreferences preferences;
    private EffectLibrary? library;
    private readonly Action<EffectLibrary, bool> configured;
    private readonly Action changed;
    private readonly TextBox path = new(), name = new(), description = new() { AcceptsReturn = true, Height = 55, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox intensity = new() { Text = "1" }, duration = new() { Text = "1" };
    private readonly CheckBox allowAi = new();
    private readonly ListBox items = new() { Height = 170, DisplayMemberPath = "Name" };
    private readonly ComboBox recipes = new() { DisplayMemberPath = "Id" }, clappers = new() { DisplayMemberPath = "Name" };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap }, status = new() { TextWrapping = TextWrapping.Wrap };
    private bool working;
    private static string Text(string ja, string en) => EditorText.Choose(ja, en);

    public EffectLibraryWindow(EditorSession session, Guid? sequenceId, Guid? clipId, EffectLibraryPreferences preferences,
        EffectLibrary? library, bool allow, Action<EffectLibrary, bool> configured, Action changed)
    {
        this.session = session; this.sequenceId = sequenceId; this.clipId = clipId; this.preferences = preferences;
        this.library = library; this.configured = configured; this.changed = changed;
        Title = Text("エフェクトライブラリ", "Effect Library"); Width = 630; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Label(panel, "Effect Library Path"); path.Text = library?.Root ?? preferences.DefaultPath; panel.Children.Add(path);
        allowAi.Content = Text("この起動中、MCPからライブラリへのアクセスを許可する", "Allow MCP library access during this launch"); allowAi.IsChecked = allow; panel.Children.Add(allowAi);
        Button(panel, Text("設定を保存", "Save settings"), Configure);
        Label(panel, Text("設定変更では既存のエフェクトは移動・削除されません。", "Changing this setting leaves existing effects in their current folder."));
        panel.Children.Add(items); panel.Children.Add(details);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        Button(actions, Text("再読込", "Refresh"), (_, _) => Refresh());
        Button(actions, Text("適用", "Apply"), Apply);
        Button(actions, Text("削除", "Delete"), Delete);
        Label(panel, Text("名前", "Name")); panel.Children.Add(name); Label(panel, Text("説明", "Description")); panel.Children.Add(description);
        var saves = new WrapPanel(); panel.Children.Add(saves);
        Button(saves, Text("選択クリップをエフェクトとして保存", "Save selected clip as Effect"), Capture);
        Button(saves, Text("名前・説明を更新", "Update name/description"), Update);
        Label(panel, Text("強さ（0〜1）", "Intensity (0–1)")); panel.Children.Add(intensity);
        Label(panel, Text("時間倍率（0より大きい値）", "Duration scale (>0)")); panel.Children.Add(duration);
        var sequence = session.GetProject().Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId);
        Label(panel, Text("保存元Recipe", "Recipe to save")); recipes.ItemsSource = sequence?.Recipes ?? []; panel.Children.Add(recipes);
        Button(panel, Text("Recipeをエフェクトとして保存", "Save Recipe as Effect"), SaveRecipe);
        Label(panel, Text("Recipe適用先Clapper", "Clapper for Recipe effect")); clappers.ItemsSource = sequence?.Clappers ?? []; panel.Children.Add(clappers);
        Label(panel, Text("RecipeはClapperへ登録後、通常の「生成 / 再生成」で映像にします。", "After binding a Recipe to a Clapper, use ordinary Generate / Regenerate to render it."));
        panel.Children.Add(status);
        items.SelectionChanged += (_, _) => {
            if (items.SelectedItem is not EffectDefinition item) return;
            name.Text = item.Name; description.Text = item.Description;
            intensity.Text = item.Defaults.Intensity.ToString(CultureInfo.CurrentCulture); duration.Text = item.Defaults.DurationScale.ToString(CultureInfo.CurrentCulture);
            details.Text = $"{item.Id} · v{item.Version}\n" + (item.RecipeSource ?? string.Join(", ", item.Curves.Select(c => $"{c.Property}: {c.Samples.Length}")));
        };
        Closing += (_, e) => { if (working) e.Cancel = true; };
        Refresh();
    }
    private void Configure(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var saved = preferences.Save(path.Text);
        if (!saved.Success) { Report(saved.Diagnostics); return; }
        library = new(saved.Value!); path.Text = library.Root; configured(library, allowAi.IsChecked == true); Refresh();
    }
    private void Refresh()
    {
        if (library is null) { status.Text = Text("保存先設定を確認して保存してください。", "Review and save the library path setting."); return; }
        Guid? selected = (items.SelectedItem as EffectDefinition)?.Id;
        var listed = library.List(); items.ItemsSource = listed.Value.IsDefault ? [] : listed.Value;
        items.SelectedItem = listed.Value.IsDefault ? null : listed.Value.FirstOrDefault(i => i.Id == selected);
        Report(listed.Diagnostics);
    }
    private async void Capture(object sender, RoutedEventArgs e)
    {
        if (working || library is null || sequenceId is not { } sequence || clipId is not { } clip) return;
        var captured = EffectComposition.Capture(session.GetProject(), sequence, clip, name.Text);
        if (!captured.Success) { Report(captured.Diagnostics); return; }
        await Save(captured.Value! with { Description = description.Text });
    }
    private async void SaveRecipe(object sender, RoutedEventArgs e)
    {
        if (working || library is null || recipes.SelectedItem is not Recipe recipe) return;
        await Save(new(1, Guid.NewGuid(), name.Text, description.Text, 1, "1", [], new(), recipe.Source, recipe.Seed));
    }
    private async void Update(object sender, RoutedEventArgs e)
    {
        if (working || items.SelectedItem is not EffectDefinition item) return;
        await Save(item with { Name = name.Text, Description = description.Text }, item.Version);
    }
    private async Task Save(EffectDefinition item, int? expected = null)
    {
        if (library is null) return;
        working = true;
        try
        {
            var saved = await library.SaveAsync(item, expected);
            if (!saved.Success) { Report(saved.Diagnostics); return; }
            Refresh(); items.SelectedItem = ((System.Collections.Immutable.ImmutableArray<EffectDefinition>)items.ItemsSource).FirstOrDefault(e => e.Id == saved.Value!.Id);
            status.Text = Text("エフェクトを保存しました。", "Effect saved.");
        }
        finally { working = false; }
    }
    private async void Apply(object sender, RoutedEventArgs e)
    {
        if (working || library is null || items.SelectedItem is not EffectDefinition item || sequenceId is not { } sequence) return;
        working = true;
        try
        {
            var validated = await library.ValidateProgramAsync(item);
            if (!validated.Success) { Report(validated.Diagnostics); return; }
            Result<EditBatch> plan;
            if (item.RecipeSource is not null && clappers.SelectedItem is Clapper clapper)
                plan = EffectComposition.PlanRecipe(session.GetProject(), sequence, clapper.Id, item);
            else if (clipId is { } clip && double.TryParse(intensity.Text, out var amount) && double.TryParse(duration.Text, out var scale))
                plan = EffectComposition.Plan(session.GetProject(), sequence, clip, item, new(amount, scale));
            else { status.Text = Text("対象とパラメータを確認してください。", "Review the target and parameters."); return; }
            if (!plan.Success) { Report(plan.Diagnostics); return; }
            var result = session.Execute(plan.Value!); Report(result.Diagnostics);
            if (result.Success) { changed(); status.Text = Text("適用しました。Undoで戻せます。", "Applied. Undo restores the previous state."); }
        }
        finally { working = false; }
    }
    private void Delete(object sender, RoutedEventArgs e)
    {
        if (working || library is null || items.SelectedItem is not EffectDefinition item) return;
        var result = library.Delete(item.Id, item.Version);
        if (result.Success) Refresh(); else Report(result.Diagnostics);
    }
    private void Report(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics) => status.Text = string.Join(" / ", diagnostics.Select(d => d.Message));
    private static void Label(Panel panel, string label) => panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 3) });
    private static void Button(Panel panel, string title, RoutedEventHandler action) { var button = new Button { Content = title, Margin = new Thickness(2, 5, 2, 5) }; button.Click += action; panel.Children.Add(button); }
}
