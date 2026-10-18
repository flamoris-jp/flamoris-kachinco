using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kachinco.Core;

namespace Kachinco.App;

// Both inspector and caption list use the same typed native commands.
public sealed class CaptionStyleEditor : StackPanel
{
    private readonly Dictionary<string, Control> fields = [];
    private readonly Dictionary<string,string> displayed = [];
    private readonly ComboBox scope = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };
    private readonly string[] fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    private EditorSession? session;
    private Guid sequenceId;
    private ImmutableArray<Guid> selected = [];
    private ProjectSnapshot? baseline;
    private string? document;
    private Action<EditResult>? changed;
    private bool refreshing;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CaptionStyleEditor()
    {
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(43,45,49));
        Children.Add(new TextBlock { Foreground = Brushes.Gainsboro, Text = T("字幕スタイル", "Caption style"), FontWeight = FontWeights.Bold, Margin = new(0,8,0,4) });
        scope.ItemsSource = new[] { T("選択した字幕", "Selected captions"), T("同じ字幕トラック", "Same subtitle tracks"), T("シーケンス内の全字幕", "All sequence captions") };
        scope.SelectedIndex = 0; Children.Add(scope);
        Children.Add(new TextBlock { Foreground = Brushes.Gainsboro, Text = T("変更した項目だけ対象へ適用します。", "Only the edited field is applied."), TextWrapping = TextWrapping.Wrap, Margin = new(0,4,0,4) });
        Choice("fontFamily", T("フォント", "Font"), fonts, true);
        Number("fontSize", T("文字サイズ（短辺1080基準px）", "Size (px at short edge 1080)"));
        ColorField("color", T("文字色", "Text color"));
        ColorField("outlineColor", T("縁取り色", "Outline color"));
        Number("outlineWidth", T("縁取り太さ（基準px）", "Outline (reference px)"));
        var positions = new WrapPanel(); Children.Add(positions);
        foreach (var (label,y,anchor) in new[] { (T("上", "Top"),.1,"Top"), (T("中央", "Center"),.5,"Center"), (T("下", "Bottom"),.9,"Bottom") })
        {
            var button = new Button { Content = label, Margin = new(2) }; positions.Children.Add(button);
            button.Click += (_, _) => Commit(new CaptionStylePatch(X: .5, Y: y, VerticalAnchor: anchor));
        }
        Number("x", T("水平位置（0～1）", "Horizontal anchor (0–1)"));
        Number("y", T("垂直位置（0～1）", "Vertical anchor (0–1)"));
        Number("maxWidth", T("テキスト幅（画面比0.05～1）", "Text width (canvas fraction .05–1)"));
        Choice("alignment", T("文字揃え", "Text alignment"), ["Left","Center","Right"]);
        Choice("verticalAnchor", T("垂直基準", "Vertical anchor"), ["Top","Center","Bottom"]);
        var shadow = new CheckBox { Foreground = Brushes.Gainsboro, Content = T("影を表示", "Shadow"), Margin = new(0,6,0,4) }; fields["shadowEnabled"] = shadow; Children.Add(shadow);
        shadow.Click += (_, _) => Field("shadowEnabled", shadow.IsChecked == true);
        ColorField("shadowColor", T("影の色", "Shadow color"));
        Number("shadowX", T("影のX（基準px）", "Shadow X (reference px)"));
        Number("shadowY", T("影のY（基準px）", "Shadow Y (reference px)"));
        var reset = new Button { Content = T("選択字幕を初期スタイルへ戻す", "Reset selected captions to default"), Margin = new(0,6,0,4) };
        reset.Click += (_, _) =>
        {
            if (session is null || baseline is null || document != session.DocumentToken) return;
            changed?.Invoke(session.Execute(new([.. selected.Select(id => (EditCommand)new SetCaptionStyle(sequenceId,id,null))], baseline.Revision)));
        };
        Children.Add(reset); Children.Add(status); IsEnabled = false;
    }
    public void Configure(EditorSession editor, Guid sequence, IEnumerable<Guid> ids, Action<EditResult> onChanged)
    {
        refreshing = true;
        try
        {
            var next = ids.ToImmutableArray();
            if(sequenceId != sequence || !selected.SequenceEqual(next)) scope.SelectedIndex = 0;
            session = editor; sequenceId = sequence; selected = next; changed = onChanged; baseline = editor.GetProject(); document = editor.DocumentToken;
            var captions = baseline.Project?.Sequences.FirstOrDefault(s => s.Id == sequence)?.Tracks.SelectMany(t => t.Captions).Where(c => selected.Contains(c.Id)).ToArray() ?? [];
            IsEnabled = captions.Length > 0;
            var values = captions.Select(c => c.Style ?? CaptionStyle.Default).ToArray();
            var first = JsonSerializer.SerializeToElement(values.FirstOrDefault() ?? CaptionStyle.Default,Json);
            foreach (var (name,control) in fields)
            {
                var v = first.GetProperty(name); displayed[name] = v.ToString();
                if (control is TextBox box) { box.Text = v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.####", CultureInfo.CurrentCulture) : v.GetString(); box.ClearValue(Border.BorderBrushProperty); }
                else if (control is ComboBox combo) { if(combo.IsEditable) combo.Text = v.GetString(); else combo.SelectedItem = v.GetString(); }
                else if (control is CheckBox check) check.IsChecked = v.GetBoolean();
            }
            var missing = values.Select(v => v.FontFamily).Distinct().Where(f => !fonts.Contains(f,StringComparer.OrdinalIgnoreCase)).ToArray();
            status.Text = missing.Length > 0 ? T("未インストールのフォントは Yu Gothic で表示：", "Missing fonts use Yu Gothic: ") + string.Join(", ",missing) :
                values.Distinct().Count() > 1 ? T("複数のスタイルが混在。先頭の値を表示しています。", "Mixed styles; showing the first caption's values.") : "";
        }
        finally { refreshing = false; }
    }
    private void Label(string text) => Children.Add(new TextBlock { Foreground = Brushes.Gainsboro, Text = text, Margin = new(0,6,0,2), TextWrapping = TextWrapping.Wrap });
    private void Number(string name,string label) => TextField(name,label,true);
    private TextBox TextField(string name,string label,bool number)
    {
        Label(label); var box = new TextBox(); fields[name] = box; Children.Add(box);
        void Save(bool force = false)
        {
            if (refreshing) return;
            if (number)
            {
                if ((!double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var value) && !double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)) || !double.IsFinite(value))
                { box.BorderBrush = Brushes.OrangeRed; status.Text = T("有効な数値を入力してください。", "Enter a finite number."); return; }
                Field(name,value,force);
            }
            else Field(name,box.Text.Trim(),force);
        }
        var apply = new Button { Content = T("この項目を適用", "Apply this field"), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_,_) => Save(true); Children.Add(apply);
        box.LostKeyboardFocus += (_, _) => Save();
        box.KeyDown += (_,e) => { if(e.Key == Key.Enter) { Save(); e.Handled = true; } };
        return box;
    }
    private void Choice(string name,string label,string[] choices,bool editable=false)
    {
        Label(label); var combo = new ComboBox { ItemsSource = choices, IsEditable = editable, MaxDropDownHeight = 260 }; fields[name] = combo; Children.Add(combo);
        combo.SelectionChanged += (_, _) => { if (!refreshing && combo.SelectedItem is string value) Field(name,value); };
        var apply = new Button { Content = T("この項目を適用", "Apply this field"), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_,_) => Field(name,combo.Text.Trim(),true);Children.Add(apply);
        if(editable) { combo.LostKeyboardFocus += (_, _) => { if(!refreshing) Field(name,combo.Text.Trim()); }; combo.KeyDown += (_,e) => { if(e.Key==Key.Enter) { Field(name,combo.Text.Trim());e.Handled=true; } }; }
    }
    private void ColorField(string name,string label)
    {
        var box = TextField(name,label + " (HEX)",false);
        var picker = new Button { Content = T("色を選ぶ…", "Choose color…"), HorizontalAlignment = HorizontalAlignment.Right };
        picker.Click += (_, _) =>
        {
            var dialog = new CaptionColorPicker(box.Text) { Owner = Window.GetWindow(this) };
            if(dialog.ShowDialog() == true) Field(name,dialog.Value,true);
        };
        Children.Add(picker);
    }
    private void Field(string name,object value,bool force=false)
    {
        if(refreshing || !IsEnabled) return;
        if(!force && displayed.TryGetValue(name,out var original) && original == JsonSerializer.SerializeToElement(value).ToString()) return;
        // Single UI field becomes one typed partial patch, never a replacement style.
        var patch = JsonSerializer.Deserialize<CaptionStylePatch>(JsonSerializer.Serialize(new Dictionary<string,object> { [name] = value }),Json)!;
        if(baseline?.Project is { } project)
        {
            var first = project.Sequences.FirstOrDefault(s=>s.Id==sequenceId)?.Tracks.SelectMany(t=>t.Captions).FirstOrDefault(c=>selected.Contains(c.Id));
            var old = JsonSerializer.SerializeToElement(first?.Style ?? CaptionStyle.Default,Json).GetProperty(name);
            if(!force && scope.SelectedIndex == 0 && selected.Length == 1 && old.ToString() == JsonSerializer.SerializeToElement(value).ToString()) return;
        }
        Commit(patch);
    }
    private void Commit(CaptionStylePatch patch)
    {
        if(refreshing || session is null || baseline is null || document != session.DocumentToken) return;
        var sequence = baseline.Project?.Sequences.FirstOrDefault(s=>s.Id==sequenceId);
        if(sequence is null) return;
        var tracks = sequence.Tracks.Where(t=>t.Captions.Any(c=>selected.Contains(c.Id)));
        var ids = scope.SelectedIndex switch { 1 => tracks.SelectMany(t=>t.Captions).Select(c=>c.Id), 2 => sequence.Tracks.SelectMany(t=>t.Captions).Select(c=>c.Id), _ => selected.AsEnumerable() };
        var commands = ids.Chunk(4096).Select(chunk => (EditCommand)new PatchCaptionStyles(sequenceId,[..chunk],patch));
        var result = session.Execute(new([..commands],baseline.Revision));
        if(result.Success) changed?.Invoke(result);
        else status.Text = string.Join(" / ",result.Diagnostics.Select(d=>d.Message));
    }
    private static string T(string ja,string en) => EditorText.Choose(ja,en);
}

internal sealed class CaptionColorPicker : Window
{
    private readonly Slider[] channels = new Slider[4];
    public string Value { get; private set; } = "#FFFFFFFF";
    public CaptionColorPicker(string value)
    {
        Title = EditorText.Choose("字幕の色", "Caption color"); Width = 340; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(16) };Content = panel;
        Color color; try { color = (Color)ColorConverter.ConvertFromString(value); } catch(Exception error) when (error is FormatException or NotSupportedException or ArgumentException) { color = Colors.White; }
        var swatch = new Border { Height=56, Margin=new(0,0,0,8) };panel.Children.Add(swatch);
        for(int i=0;i<4;i++)
        {
            panel.Children.Add(new TextBlock { Text = new[] {"Alpha","Red","Green","Blue"}[i] });
            var slider = new Slider { Minimum=0,Maximum=255,TickFrequency=1,IsSnapToTickEnabled=true,Value=new byte[]{color.A,color.R,color.G,color.B}[i] };channels[i]=slider;panel.Children.Add(slider);
            slider.ValueChanged += (_,_) => Update();
        }
        void Update() { if(channels.Any(s=>s is null)) return;var c=Color.FromArgb((byte)channels[0].Value,(byte)channels[1].Value,(byte)channels[2].Value,(byte)channels[3].Value);Value=c.ToString();swatch.Background=new SolidColorBrush(c); }
        Update();var ok=new Button{Content="OK",IsDefault=true,Margin=new(0,12,0,0)};ok.Click+=(_,_)=>DialogResult=true;panel.Children.Add(ok);
    }
}
