using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Kachinco.App;

// Image-editor-style precise number + convenient slider. Value is a projection;
// events request a transient preview or ONE command, never write project state.
public sealed class PropertySlider : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(PropertySlider), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty SliderMinimumProperty = DependencyProperty.Register(nameof(SliderMinimum), typeof(double), typeof(PropertySlider), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty SliderMaximumProperty = DependencyProperty.Register(nameof(SliderMaximum), typeof(double), typeof(PropertySlider), new PropertyMetadata(1d, Changed));
    public static readonly DependencyProperty NumberMinimumProperty = DependencyProperty.Register(nameof(NumberMinimum), typeof(double), typeof(PropertySlider), new PropertyMetadata(double.NegativeInfinity));
    public static readonly DependencyProperty NumberMaximumProperty = DependencyProperty.Register(nameof(NumberMaximum), typeof(double), typeof(PropertySlider), new PropertyMetadata(double.PositiveInfinity));
    public static readonly DependencyProperty DisplayFactorProperty = DependencyProperty.Register(nameof(DisplayFactor), typeof(double), typeof(PropertySlider), new PropertyMetadata(1d, Changed));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(PropertySlider), new PropertyMetadata("", Changed));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double SliderMinimum { get => (double)GetValue(SliderMinimumProperty); set => SetValue(SliderMinimumProperty, value); }
    public double SliderMaximum { get => (double)GetValue(SliderMaximumProperty); set => SetValue(SliderMaximumProperty, value); }
    public double NumberMinimum { get => (double)GetValue(NumberMinimumProperty); set => SetValue(NumberMinimumProperty, value); }
    public double NumberMaximum { get => (double)GetValue(NumberMaximumProperty); set => SetValue(NumberMaximumProperty, value); }
    public double DisplayFactor { get => (double)GetValue(DisplayFactorProperty); set => SetValue(DisplayFactorProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public event EventHandler? EditStarted;
    public event EventHandler? EditPreview;
    public event EventHandler? EditCommitted;
    public event EventHandler? EditCancelled;
    private readonly Slider slider = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, IsMoveToPointEnabled = true };
    private readonly TextBox number = new() { Width = 66, Padding = new(3, 2, 3, 2), TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock suffix = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(3, 0, 0, 0), Foreground = Brushes.LightGray };
    private bool updating, editing;
    private double original;
    private string rendered = "";
    public PropertySlider()
    {
        Focusable = false;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        slider.Margin = new(0, 0, 6, 0);
        grid.Children.Add(slider); Grid.SetColumn(number, 1); grid.Children.Add(number); Grid.SetColumn(suffix, 2); grid.Children.Add(suffix); Content = grid;
        slider.PreviewMouseLeftButtonDown += (_, _) => Begin();
        slider.PreviewMouseLeftButtonUp += (_, _) => Commit();
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => Begin()));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) => { if (e.Canceled) CancelEdit(); else Commit(); }));
        slider.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() => { if (editing) CancelEdit(); }, DispatcherPriority.Input);
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            bool single = !editing; Begin(); SetCurrentValue(ValueProperty, slider.Value);
            EditPreview?.Invoke(this, EventArgs.Empty); if (single) Commit();
        };
        slider.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) Begin();
        };
        slider.PreviewKeyUp += (_, _) => Commit();
        slider.LostKeyboardFocus += (_, _) => { if (editing) Commit(); };
        number.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitNumber(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Update(); e.Handled = true; }
        };
        number.LostKeyboardFocus += (_, _) => CommitNumber();
        Unloaded += (_, _) => CancelEdit();
        Loaded += (_, _) =>
        {
            var name = System.Windows.Automation.AutomationProperties.GetName(this);
            System.Windows.Automation.AutomationProperties.SetName(slider, name);
            System.Windows.Automation.AutomationProperties.SetName(number, name);
            Update();
        };
        Update();
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PropertySlider)d).Update();
    private void Update()
    {
        updating = true;
        slider.Maximum = Math.Max(SliderMinimum, SliderMaximum); slider.Minimum = SliderMinimum;
        slider.SmallChange = (SliderMaximum - SliderMinimum) / 100;
        slider.LargeChange = (SliderMaximum - SliderMinimum) / 10;
        slider.Value = Math.Clamp(Value, SliderMinimum, Math.Max(SliderMinimum, SliderMaximum));
        rendered = (Value * DisplayFactor).ToString("0.########", CultureInfo.CurrentCulture);
        number.Text = rendered; suffix.Text = Suffix;
        number.ClearValue(Border.BorderBrushProperty); number.ToolTip = null; updating = false;
    }
    private void Begin()
    {
        if (editing) return; original = Value; editing = true; EditStarted?.Invoke(this, EventArgs.Empty);
    }
    private void Commit()
    {
        if (!editing) return; editing = false; EditCommitted?.Invoke(this, EventArgs.Empty);
    }
    public void CancelEdit()
    {
        if (!editing) return; editing = false; Value = original; EditCancelled?.Invoke(this, EventArgs.Empty);
    }
    private void CommitNumber()
    {
        if (updating || number.Text == rendered) return;
        if (!double.TryParse(number.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var displayed) ||
            !double.IsFinite(displayed) || !double.IsFinite(displayed / DisplayFactor) ||
            displayed / DisplayFactor < NumberMinimum || displayed / DisplayFactor > NumberMaximum)
        {
            number.BorderBrush = Brushes.OrangeRed;
            number.ToolTip = EditorText.Choose("有効な範囲の数値を入力してください。", "Enter a finite value within the valid range.");
            return;
        }
        double next = displayed / DisplayFactor;
        if (next == Value) { Update(); return; }
        Begin(); Value = next; EditPreview?.Invoke(this, EventArgs.Empty); Commit();
    }
}
