using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;

namespace Kachinco.App;

public sealed class AuthoringWindow : Window
{
    private readonly EditorSession session;
    private readonly Guid sequenceId;
    private readonly ListBox clappers = new() { DisplayMemberPath = "Name", Height = 110 };
    private readonly ComboBox recipes = new() { DisplayMemberPath = "Id" };
    private readonly ComboBox geometry = new() { ItemsSource = new[] { "なし", "点", "四角" }, SelectedIndex = 0 };
    private readonly TextBox name = new(), start = new(), duration = new(), x = new(), y = new(), width = new(), height = new(), seed = new() { Text = "1" };
    private readonly TextBox source = new() { AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap,
        Text = "text(text=\"グエー\", x=0, y=100, vx=180, size=64)\nparticles(count=24, x=100, y=200, vx=80, vy=-20, size=5)" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? rendering;
    public AuthoringWindow(EditorSession session, Guid sequenceId, long cursor)
    {
        this.session = session; this.sequenceId = sequenceId;
        Title = "Clapper / Recipe"; Width = 620; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Clapper — 名前付きの時間と場所", FontSize = 18 }); panel.Children.Add(clappers);
        var fields = new Grid(); fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new()); panel.Children.Add(fields);
        int row = 0;
        foreach (var (label, control) in new (string, Control)[] { ("名前", name), ("開始（秒）", start), ("長さ（秒）", duration), ("場所", geometry), ("X（px）", x), ("Y（px）", y), ("幅（px）", width), ("高さ（px）", height) })
        {
            fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, Margin = new Thickness(3) }; Grid.SetRow(text,row); fields.Children.Add(text);
            Grid.SetRow(control,row); Grid.SetColumn(control,1); fields.Children.Add(control); row++;
        }
        var actions = new WrapPanel(); panel.Children.Add(actions);
        AddButton(actions, "新規", (_, _) => New(cursor)); AddButton(actions, "Clapperを保存", SaveClapper); AddButton(actions, "削除", DeleteClapper);
        panel.Children.Add(new TextBlock { Text = "Recipe — 文字・火の粉（最大10秒）", FontSize = 18, Margin = new Thickness(0,16,0,4) });
        panel.Children.Add(recipes); panel.Children.Add(new TextBlock { Text = "新規作成は下のボタン。既存Recipeを選ぶと同じ素材を再生成します。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(source); panel.Children.Add(new TextBlock { Text = "乱数シード" }); panel.Children.Add(seed);
        var renderActions = new WrapPanel(); panel.Children.Add(renderActions);
        AddButton(renderActions, "新しいRecipe", (_, _) => recipes.SelectedItem = null);
        AddButton(renderActions, "検証", ValidateRecipe); AddButton(renderActions, "生成 / 再生成", GenerateRecipe);
        AddButton(renderActions, "キャンセル", (_, _) => rendering?.Cancel()); panel.Children.Add(status);
        clappers.SelectionChanged += (_, _) => LoadClapper();
        recipes.SelectionChanged += (_, _) => { if (recipes.SelectedItem is Recipe recipe) { source.Text = recipe.Source; seed.Text = recipe.Seed.ToString(CultureInfo.InvariantCulture); clappers.SelectedItem = Sequence.Clappers.First(c => c.Id == recipe.ClapperId); } };
        Closing += (_, e) => { if (rendering is not null) { rendering.Cancel(); e.Cancel = true; } };
        Refresh(); New(cursor);
    }
    private Sequence Sequence => session.GetProject().Project!.Sequences.First(s => s.Id == sequenceId);
    private void Refresh() { clappers.ItemsSource = Sequence.Clappers; recipes.ItemsSource = Sequence.Recipes; }
    private void New(long cursor)
    {
        clappers.SelectedItem = null; name.Text = "A-" + (Sequence.Clappers.Length + 1);
        long from = Math.Min(cursor, Math.Max(0, Sequence.DurationTicks - TimelineTime.TicksPerSecond));
        start.Text = Seconds(from); duration.Text = Seconds(Math.Min(3 * TimelineTime.TicksPerSecond, Sequence.DurationTicks - from));
        x.Text = y.Text = "0"; width.Text = Sequence.Settings.Width.ToString(); height.Text = Sequence.Settings.Height.ToString(); geometry.SelectedIndex = 0;
    }
    private void LoadClapper()
    {
        if (clappers.SelectedItem is not Clapper c) return;
        name.Text = c.Name; start.Text = Seconds(c.StartTicks); duration.Text = Seconds(c.DurationTicks);
        geometry.SelectedIndex = c.Geometry is null ? 0 : c.Geometry.Kind == ClapperGeometryKind.Point ? 1 : 2;
        x.Text = (c.Geometry?.X ?? 0).ToString(); y.Text = (c.Geometry?.Y ?? 0).ToString();
        width.Text = (c.Geometry?.Width ?? Sequence.Settings.Width).ToString(); height.Text = (c.Geometry?.Height ?? Sequence.Settings.Height).ToString();
    }
    private void SaveClapper(object sender, RoutedEventArgs e)
    {
        if (rendering is not null) return;
        try
        {
            var previous = clappers.SelectedItem as Clapper;
            long from = TimelineTime.SecondsToTicks(decimal.Parse(start.Text)), count = TimelineTime.SecondsToTicks(decimal.Parse(duration.Text));
            ClapperGeometry? area = geometry.SelectedIndex == 0 ? null : new(geometry.SelectedIndex == 1 ? ClapperGeometryKind.Point : ClapperGeometryKind.Rectangle,
                double.Parse(x.Text), double.Parse(y.Text), geometry.SelectedIndex == 1 ? 0 : double.Parse(width.Text), geometry.SelectedIndex == 1 ? 0 : double.Parse(height.Text));
            var c = new Clapper(previous?.Id ?? Guid.NewGuid(), name.Text, from, count, area,
                previous?.TargetTrackId ?? Sequence.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Id, previous?.SourceClipId, previous?.Notes ?? "");
            var result = session.Execute(new([previous is null ? new AddClapper(sequenceId,c) : new UpdateClapper(sequenceId,c)],session.GetProject().Revision));
            status.Text = result.Success ? "Clapperを保存しました。" : string.Join(" / ",result.Diagnostics.Select(d=>d.Message));
            if (result.Success) { Refresh(); clappers.SelectedItem = Sequence.Clappers.First(v => v.Id == c.Id); }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException) { status.Text = "時間・座標を確認してください。"; }
    }
    private void DeleteClapper(object sender, RoutedEventArgs e)
    {
        if (rendering is not null || clappers.SelectedItem is not Clapper c) return;
        var result = session.Execute(new([new DeleteClapper(sequenceId,c.Id)],session.GetProject().Revision));
        status.Text = result.Success ? "Clapperを削除しました。" : string.Join(" / ",result.Diagnostics.Select(d=>d.Message));
        if (result.Success) Refresh();
    }
    private async void ValidateRecipe(object sender, RoutedEventArgs e)
    {
        var result = await new RecipeCompiler().CompileAsync(source.Text);
        status.Text = result.Success ? $"検証OK: {result.Value!.Operations.Length} 描画命令" : string.Join(" / ",result.Diagnostics.Select(d=>d.Message));
    }
    private async void GenerateRecipe(object sender, RoutedEventArgs e)
    {
        if (rendering is not null || clappers.SelectedItem is not Clapper c) { status.Text = "保存済みClapperを選択してください。"; return; }
        if (!int.TryParse(seed.Text, out var randomSeed)) { status.Text = "シードには整数を入力してください。"; return; }
        var previous = recipes.SelectedItem as Recipe;
        var asset = previous is null ? null : session.GetProject().Project!.Assets.FirstOrDefault(a => a.Provenance?.RecipeId == previous.Id);
        if (previous is not null && asset is null) { status.Text = "再生成対象の素材がありません。"; return; }
        var picker = new SaveFileDialog { Filter = "Generated MOV (*.mov)|*.mov", DefaultExt = ".mov", FileName = "recipe-"+Guid.NewGuid().ToString("N")+".mov" };
        if (picker.ShowDialog(this) != true) return;
        var snapshot = session.GetProject();
        var recipe = new Recipe(previous?.Id ?? Guid.NewGuid(),c.Id,source.Text,(previous?.Revision ?? 0)+1,randomSeed,"1","1");
        rendering = new(); var token = rendering.Token;
        status.Text = "生成しています…";
        try
        {
            var service = new RecipeGenerationService(new RecipeCompiler(),new WindowsRecipeRasterizer(Dispatcher));
            var result = await Task.Run(() => service.PrepareAsync(snapshot,sequenceId,recipe,picker.FileName,asset?.Id,token));
            if (!result.Success) { status.Text = string.Join(" / ",result.Diagnostics.Select(d=>d.Message)); return; }
            var prepared = result.Value!;
            if(token.IsCancellationRequested) { File.Delete(prepared.OutputPath); status.Text="キャンセルしました。"; return; }
            var committed = session.Execute(prepared.Batch);
            if (!committed.Success) { File.Delete(prepared.OutputPath); status.Text = "編集中に状態が変わりました。生成をやり直してください。"; return; }
            status.Text = "通常のクリップとして配置しました。Undo一回で戻せます。"; Refresh();
            recipes.SelectedItem = Sequence.Recipes.First(r => r.Id == recipe.Id);
        }
        finally { rendering.Dispose(); rendering = null; }
    }
    private static void AddButton(Panel panel,string title,RoutedEventHandler handler) { var button=new Button{Content=title};button.Click+=handler;panel.Children.Add(button); }
    private static string Seconds(long ticks) => ((decimal)ticks/TimelineTime.TicksPerSecond).ToString("0.###",CultureInfo.CurrentCulture);
}
