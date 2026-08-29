using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CurrencyWarsAssistant.App;

internal sealed class StartupWindow : Window
{
    public StartupWindow()
    {
        Title = ProductIdentity.DisplayName;
        Width = 420;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x1A));
        Foreground = Brushes.White;
        ShowInTaskbar = true;
        ShowActivated = true;
        Topmost = true;

        Content = new Border
        {
            Padding = new Thickness(26, 22, 26, 20),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x48, 0x66)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = ProductIdentity.DisplayName,
                        FontFamily = new FontFamily("Microsoft YaHei UI"),
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(
                            Color.FromRgb(0xE8, 0xF5, 0xFF))
                    },
                    new TextBlock
                    {
                        Text = "正在载入游戏数据与识别规则…",
                        Margin = new Thickness(0, 14, 0, 0),
                        FontFamily = new FontFamily("Microsoft YaHei UI"),
                        FontSize = 13,
                        Foreground = new SolidColorBrush(
                            Color.FromRgb(0x9F, 0xB6, 0xCF))
                    }
                }
            }
        };
    }
}
