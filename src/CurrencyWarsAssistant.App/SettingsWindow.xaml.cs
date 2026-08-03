using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CurrencyWarsAssistant.App;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 出售策略 ListBox 拦截滚轮会阻止外层 ScrollViewer 滚动；
    /// 这里把滚轮事件转发给最近的祖先 ScrollViewer，实现全页滚动。
    /// </summary>
    private void OnBenchSaleModeMouseWheel(
        object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        var current = sender as DependencyObject;
        while (current is not null)
        {
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            if (current is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(
                    scrollViewer.VerticalOffset - e.Delta);
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.SaveUserSettings();
        base.OnClosing(e);
    }
}
