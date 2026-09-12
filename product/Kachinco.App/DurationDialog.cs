using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Kachinco.Core;

namespace Kachinco.App;

internal sealed class DurationDialog : Window
{
    public long DurationTicks { get; private set; }
    public DurationDialog()
    {
        Title = "素材の長さ"; Width = 410; Height = 250; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "この版では素材情報を登録します。\n素材の長さを秒で入力してください。", TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = "8", Margin = new Thickness(0, 16, 0, 8) };
        panel.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var ok = new Button { Content = "登録", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (!decimal.TryParse(input.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal seconds) || seconds <= 0 ||
                seconds > (decimal)long.MaxValue / TimelineTime.TicksPerSecond)
            { error.Text = "有効な正の秒数を入力してください。"; return; }
            DurationTicks = checked((long)decimal.Round(seconds * TimelineTime.TicksPerSecond, 0, MidpointRounding.AwayFromZero));
            if (DurationTicks <= 0) { error.Text = "長さが短すぎます。"; return; }
            DialogResult = true;
        };
        panel.Children.Add(ok); Content = panel;
    }
}
