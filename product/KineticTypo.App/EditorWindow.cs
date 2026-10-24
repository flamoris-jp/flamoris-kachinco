using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Kachinco.Native;
using Microsoft.Win32;

namespace KineticTypo.App;

public sealed partial class EditorWindow : Window
{
    private readonly NativeKineticDocument session = new();
    private NativeKineticRenderer? renderer;
    private readonly ListBox clips = new();
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gainsboro, Margin = new(8) };
    private readonly Slider scrub = new() { Minimum = 0, Maximum = 5 };
    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly TextBox program = new() { AcceptsReturn = true, FontFamily = new("Consolas"), Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch clock = new();
    private readonly SemaphoreSlim rendering = new(1);
    private JsonObject document = null!;
    private JsonObject? handoff;
    private string? requestPath, filePath;
    private string saved = "";
    private string? pendingSource;
    private long revision, playStart, renderGeneration;
    private bool refreshing, closed, exporting, fieldsDirty, showingClip;
    private CancellationTokenSource? exportCancel;

    public EditorWindow(string[] args)
    {
        Title = "KineticTypo / キネタイ"; Width = 1280; Height = 850; MinWidth = 950; MinHeight = 650; Background = new SolidColorBrush(Color.FromRgb(28, 30, 35));
        var root = new DockPanel(); Content = root;
        var bar = new WrapPanel { Background = new SolidColorBrush(Color.FromRgb(241, 239, 235)), Margin = new(0, 0, 0, 4) }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        void Command(string label, Action action) { var b = new Button { Content = label, Margin = new(3), Padding = new(8, 5, 8, 5) }; b.Click += (_, _) => Guard(action); bar.Children.Add(b); }
        Command("新規", () => { if (handoff is null && ConfirmDiscard()) Load(KineticComposition.New(), null); });
        Command("開く", Open); Command("保存", Save); Command("歌詞読込 SRT / PONPON", Import);
        Command("Undo", () => History("undo")); Command("Redo", () => History("redo"));
        Command("＋文字", Add); Command("削除", Delete);
        Command("▶ / ❚❚", TogglePlay); Command("先頭", () => { Stop(); scrub.Value = 0; });
        Command("RGB＋マスク書出", () => _ = ExportAsync()); Command("書出中止", () => exportCancel?.Cancel());
        Command("Pythonスクリプト読込", () => _ = CompileScriptAsync());
        Command("GPU再初期化", () => { Stop(); _ = ResetRendererAsync(); });
        Command("カチンコへ適用", ReturnToHost);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var bottom = new DockPanel { Margin = new(8) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var timeLabel = new TextBlock { Width = 110, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(timeLabel, Dock.Right); bottom.Children.Add(timeLabel); bottom.Children.Add(scrub);
        scrub.ValueChanged += (_, _) => { timeLabel.Text = $"{scrub.Value:F3} s"; if (!refreshing) { if (clock.IsRunning) { playStart = Tick; clock.Restart(); } Render(); } };
        var grid = new Grid { Margin = new(8) }; grid.ColumnDefinitions.Add(new() { Width = new(220) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(310) }); root.Children.Add(grid);
        grid.Children.Add(clips); clips.SelectionChanged += (_, _) => ShowClip();
        var previewBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(55, 58, 65)), Margin = new(8), Child = preview }; Grid.SetColumn(previewBorder, 1); grid.Children.Add(previewBorder);
        var inspector = new StackPanel { Margin = new(5) }; var scroll = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);
        foreach (var field in new[] { ("text", "文字"), ("fontFamily", "フォント"), ("fontSize", "サイズ"), ("color", "文字色 #RRGGBBAA"), ("strokeColor", "縁の色 #RRGGBBAA"), ("strokeWidth", "縁の太さ"), ("x", "X"), ("y", "Y"), ("startTicks", "開始 秒"), ("durationTicks", "長さ 秒"), ("seed", "シード") })
        {
            inspector.Children.Add(new TextBlock { Text = field.Item2, Foreground = Brushes.Gainsboro, Margin = new(0, 5, 0, 2) });
            var box = new TextBox { Padding = new(4), AcceptsReturn = field.Item1 == "text", MinHeight = field.Item1 == "text" ? 55 : 24 };
            box.TextChanged += (_, _) => { if (!showingClip) fieldsDirty = true; };
            inputs[field.Item1] = box; inspector.Children.Add(box);
        }
        var presets = new WrapPanel(); inspector.Children.Add(presets);
        foreach (var item in new[] { ("なし", "none"), ("ブルブル", "shake"), ("飛来", "flight"), ("散る", "scatter") })
        {
            var b = new Button { Content = item.Item1, Margin = new(2) }; b.Click += (_, _) => program.Text = KineticComposition.Preset(item.Item2).ToJsonString(new() { WriteIndented = true }); presets.Children.Add(b);
        }
        inspector.Children.Add(new TextBlock { Text = "動きのスクリプト (IR v1)", Foreground = Brushes.Gainsboro, Margin = new(0, 8, 0, 2) }); inspector.Children.Add(program);
        program.TextChanged += (_, _) => { if (!showingClip) { fieldsDirty = true; pendingSource = null; } };
        var apply = new Button { Content = "文字と動きを反映", Margin = new(0, 8, 0, 8), Padding = new(8) }; apply.Click += (_, _) => Guard(CommitFields); inspector.Children.Add(apply);
        timer.Tick += (_, _) => {
            var seconds = (double)playStart / TimelineTime.TicksPerSecond + clock.Elapsed.TotalSeconds;
            if (seconds >= scrub.Maximum) { Stop(); seconds = scrub.Maximum; }
            refreshing = true; scrub.Value = seconds; refreshing = false; Render();
        };
        if (args.Length == 2 && args[0] == "--handoff")
        {
            requestPath = Path.GetFullPath(args[1]);
            if (new FileInfo(requestPath).Length > KineticComposition.MaximumBytes) throw new InvalidDataException("Handoff too large.");
            handoff = JsonNode.Parse(File.ReadAllText(requestPath))!.AsObject();
            if (handoff["version"]?.GetValue<int>() != 1 || !Guid.TryParse(handoff["handoffId"]?.GetValue<string>(), out _)) throw new InvalidDataException("Invalid handoff.");
            filePath = Path.Combine(Path.GetDirectoryName(requestPath)!, "draft.ktproj"); Load(handoff["composition"]!.AsObject(), filePath);
        }
        else if (args.Length == 1) Load(KineticComposition.Read(args[0]), args[0]);
        else Load(KineticComposition.New(), null);
        PreviewKeyDown += (_, e) => {
            bool control = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            if (control && e.Key == System.Windows.Input.Key.S) { Guard(Save); e.Handled = true; }
            else if (control && e.Key == System.Windows.Input.Key.O) { Guard(Open); e.Handled = true; }
            else if (System.Windows.Input.Keyboard.FocusedElement is not TextBox) {
                if (e.Key == System.Windows.Input.Key.Space) { TogglePlay(); e.Handled = true; }
                else if (control && e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y) { Guard(() => History(e.Key == System.Windows.Input.Key.Z ? "undo" : "redo")); e.Handled = true; }
            }
        };
        InitializeMcp(bar);
        Loaded += (_, _) => Render();
        Closing += (_, e) => {
            if (exporting) { exportCancel?.Cancel(); e.Cancel = true; status.Text = "書き出しを中止しています。完了後に閉じてください。"; return; }
            if (!ConfirmDiscard()) { e.Cancel = true; return; } closed = true; mcp?.Shutdown(); Invalidating?.Invoke(); Stop();
        };
        Closed += async (_, _) => { await rendering.WaitAsync(); try { renderer?.Dispose(); session.Dispose(); } finally { rendering.Release(); } };
    }
    private long Tick => TimelineTime.SecondsToTicks((decimal)scrub.Value);
    private JsonObject? Selected => clips.SelectedIndex >= 0 && clips.SelectedIndex < document["clips"]!.AsArray().Count ? document["clips"]![clips.SelectedIndex]!.AsObject() : null;
    private void Guard(Action action) { try { action(); } catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = ex.Message; } }
    private void Load(JsonObject doc, string? path)
    {
        var result = session.Edit(doc, revision, true); Accept(result); filePath = path; saved = document.ToJsonString(); InvalidateMcp(); Refresh();
    }
    private void Accept(JsonObject result)
    {
        if (result["success"]?.GetValue<bool>() != true) throw new InvalidDataException(result["error"]?.GetValue<string>() ?? "Text command failed.");
        revision = result["revision"]!.GetValue<long>(); document = result["document"]!.AsObject();
    }
    private void Commit(JsonObject next)
    {
        Stop(); Accept(session.Edit(next, revision));
        if (handoff is not null && filePath is not null) { try { KineticComposition.Save(filePath, document); } catch (IOException ex) { status.Text = ex.Message; } }
        Refresh();
    }
    private void Refresh()
    {
        int selected = Math.Max(0, clips.SelectedIndex); refreshing = true; clips.Items.Clear();
        foreach (var clip in document["clips"]!.AsArray()) clips.Items.Add($"{KineticComposition.Tick(clip!, "startTicks") / (double)TimelineTime.TicksPerSecond:F2}  {clip!["text"]!.GetValue<string>().Replace('\n', ' ')}");
        clips.SelectedIndex = Math.Min(selected, clips.Items.Count - 1); scrub.Maximum = KineticComposition.Duration(document) / (double)TimelineTime.TicksPerSecond; refreshing = false;
        ShowClip(); Render();
    }
    private static string ColorText(JsonNode node) => "#" + string.Join("", node.AsArray().Select(n => ((byte)Math.Round(n!.GetValue<double>() * 255)).ToString("X2", CultureInfo.InvariantCulture)));
    private void ShowClip()
    {
        if (Selected is not { } c) return;
        showingClip = true;
        foreach (var pair in inputs) pair.Value.Text = pair.Key switch {
            "startTicks" or "durationTicks" => ((decimal)KineticComposition.Tick(c, pair.Key) / TimelineTime.TicksPerSecond).ToString(CultureInfo.InvariantCulture),
            "color" or "strokeColor" => ColorText(c[pair.Key]!), _ => c[pair.Key]!.ToString()
        };
        program.Text = c["program"]!.ToJsonString(new() { WriteIndented = true });
        showingClip = false; fieldsDirty = false;
    }
    private void CommitFields()
    {
        if (Selected is null) return;
        var next = (JsonObject)document.DeepClone(); var c = next["clips"]![clips.SelectedIndex]!.AsObject();
        foreach (var pair in inputs)
        {
            var text = pair.Value.Text;
            if (pair.Key is "text" or "fontFamily") c[pair.Key] = text;
            else if (pair.Key is "startTicks" or "durationTicks") c[pair.Key] = TimelineTime.SecondsToTicks(decimal.Parse(text, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
            else if (pair.Key is "color" or "strokeColor") {
                string hex = text.Trim().TrimStart('#'); if (hex.Length == 6) hex += "FF"; if (hex.Length != 8) throw new FormatException("色は #RRGGBB または #RRGGBBAA で指定してください。");
                c[pair.Key] = new JsonArray(Enumerable.Range(0, 4).Select(i => JsonValue.Create(byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d) as JsonNode).ToArray());
            }
            else if (pair.Key == "seed") c[pair.Key] = uint.Parse(text, CultureInfo.InvariantCulture);
            else c[pair.Key] = double.Parse(text, CultureInfo.InvariantCulture);
        }
        var parsedProgram = JsonNode.Parse(program.Text)!.AsObject();
        if (!JsonNode.DeepEquals(c["program"], parsedProgram)) c["source"] = pendingSource ?? "";
        pendingSource = null; c["program"] = parsedProgram;
        next["durationTicks"] = Math.Max(KineticComposition.Duration(next), checked(KineticComposition.Tick(c, "startTicks") + KineticComposition.Tick(c, "durationTicks"))).ToString(CultureInfo.InvariantCulture); Commit(next);
    }
    private void Add() { var d = (JsonObject)document.DeepClone(); long length = TimelineTime.SecondsToTicks(2); var start = Math.Min(Tick, KineticComposition.Duration(d)); d["clips"]!.AsArray().Add(KineticComposition.Clip("文字を入力", start, length)); d["durationTicks"] = Math.Max(KineticComposition.Duration(d), start + length).ToString(); Commit(d); clips.SelectedIndex = clips.Items.Count - 1; }
    private void Delete() { if (Selected is null) return; var d = (JsonObject)document.DeepClone(); d["clips"]!.AsArray().RemoveAt(clips.SelectedIndex); Commit(d); }
    private void History(string action) { Stop(); Accept(session.Request(new() { ["action"] = action, ["expectedRevision"] = revision })); Refresh(); }
    private void Open() { if (handoff is not null || !ConfirmDiscard()) return; var dialog = new OpenFileDialog { Filter = "KineticTypo|*.ktproj" }; if (dialog.ShowDialog(this) == true) Load(KineticComposition.Read(dialog.FileName), dialog.FileName); }
    private void Save() { if (fieldsDirty) CommitFields(); if (filePath is null) { var dialog = new SaveFileDialog { Filter = "KineticTypo|*.ktproj", DefaultExt = ".ktproj" }; if (dialog.ShowDialog(this) != true) return; filePath = dialog.FileName; } KineticComposition.Save(filePath, document); saved = document.ToJsonString(); status.Text = "保存しました。"; }
    private bool ConfirmDiscard() { if (document is null || document.ToJsonString() == saved && !fieldsDirty) return true; var answer = MessageBox.Show(this, "変更を保存しますか？", "KineticTypo", MessageBoxButton.YesNoCancel); if (answer == MessageBoxResult.Cancel) return false; if (answer == MessageBoxResult.Yes) { try { Save(); } catch (Exception ex) { status.Text = ex.Message; return false; } return document.ToJsonString() == saved; } return true; }
    private void Import() { var picker = new OpenFileDialog { Filter = "歌詞|*.srt;*.json" }; if (picker.ShowDialog(this) != true) return; if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) throw new InvalidDataException("Lyrics too large."); Commit(KineticComposition.ImportLyrics(File.ReadAllText(picker.FileName), Path.GetExtension(picker.FileName).Equals(".srt", StringComparison.OrdinalIgnoreCase), document)); }
    private void TogglePlay() { if (clock.IsRunning) Stop(); else { if (scrub.Value >= scrub.Maximum) scrub.Value = 0; playStart = Tick; clock.Restart(); timer.Start(); } }
    private void Stop() { clock.Stop(); timer.Stop(); }
    private async Task ResetRendererAsync() { await rendering.WaitAsync(); try { renderer?.Dispose(); renderer = null; } finally { rendering.Release(); } Render(); }
    private async void Render()
    {
        if (!IsLoaded || closed || exporting) return;
        long generation = ++renderGeneration, tick = Math.Min(Tick, KineticComposition.Duration(document) - 1); string source = document.ToJsonString();
        if (!await rendering.WaitAsync(0)) return;
        try
        {
            int width = document["width"]!.GetValue<int>(), height = document["height"]!.GetValue<int>();
            double ratio = Math.Min(1, 900d / width); int w = Math.Max(1, (int)(width * ratio)), h = Math.Max(1, (int)(height * ratio));
            var pixels = await Task.Run(() => { renderer ??= new(); var frame = renderer.Render(source, tick, w, h); return (frame, info: renderer.Info); });
            if (closed) return;
            if (generation != renderGeneration && !clock.IsRunning) { _ = Dispatcher.BeginInvoke(Render); return; }
            preview.Source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels.frame.BgraPremultiplied, w * 4);
            status.Text = $"{tick / (double)TimelineTime.TicksPerSecond:F3} s  ·  {pixels.info["backend"]}  ·  {pixels.info["adapter"]}  ·  {pixels.info["resolvedFonts"]}  {pixels.info["fontWarnings"]}";
        }
        catch (Exception ex) { preview.Source = null; status.Text = ex.Message; Stop(); }
        finally { rendering.Release(); }
    }
    private async Task ExportAsync()
    {
        if (exporting) return;
        var picker = new SaveFileDialog { Title = "出力する新しいフォルダー名", FileName = "kinetai-pair", Filter = "Output folder|*" }; if (picker.ShowDialog(this) != true) return;
        try { if (fieldsDirty) CommitFields(); } catch (Exception ex) { status.Text = ex.Message; return; }
        Stop(); exporting = true; exportCancel = new();
        try { var snapshot = (JsonObject)document.DeepClone(); string path = await Task.Run(() => KineticPair.ExportAsync(snapshot, picker.FileName, new Progress<double>(p => Dispatcher.Invoke(() => status.Text = $"RGB＋マスク書出 {p:P0}")), exportCancel.Token)); status.Text = "書き出しました: " + path; }
        catch (OperationCanceledException) { status.Text = "書き出しを中止しました。"; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { exporting = false; exportCancel.Dispose(); exportCancel = null; }
    }
    private async Task CompileScriptAsync()
    {
        var picker = new OpenFileDialog { Filter = "Kinetic script|*.py" }; if (picker.ShowDialog(this) != true || Selected is null) return;
        try {
            if (new FileInfo(picker.FileName).Length > 65536) throw new InvalidDataException("Script too large.");
            long expected = revision; string clipId = Selected["id"]!.GetValue<string>();
            string source = await File.ReadAllTextAsync(picker.FileName);
            var ir = await new KineticProgramCompiler().CompileAsync(source);
            if (revision != expected || Selected?["id"]?.GetValue<string>() != clipId) throw new InvalidDataException("編集中の文字が変わりました。スクリプトをもう一度読み込んでください。");
            program.Text = ir.ToJsonString(new() { WriteIndented = true }); pendingSource = source; CommitFields();
        } catch (Exception ex) { status.Text = ex.Message; }
    }
    private void ReturnToHost()
    {
        if (handoff is null || requestPath is null) { status.Text = "単独起動中です。保存した .ktproj をカチンコで読み込めます。"; return; }
        CommitFields(); Save();
        var response = KineticHandoff.Response(handoff, document); KineticHandoff.ValidateResponse(handoff, response);
        KineticComposition.WriteAtomicForCompanion(Path.Combine(Path.GetDirectoryName(requestPath)!, "response.json"), response.ToJsonString()); Close();
    }
}
