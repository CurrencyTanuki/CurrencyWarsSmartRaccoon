using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CurrencyWarsAssistant.App;

public partial class CompletedRunsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public CompletedRunsWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            // 打开窗口时渲染默认选中的存档报告。
            if (_viewModel.SelectedCompletedRun is { } initial)
            {
                await RenderReportAsync(initial.RunId);
            }
        };
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshCompletedRunsAsync();
        if (_viewModel.SelectedCompletedRun is { } run)
        {
            await RenderReportAsync(run.RunId);
        }
    }

    /// <summary>
    /// 显式鼠标点击处理：点击存档即选中并渲染（不依赖 SelectionChanged
    /// 时序/绑定写回，确保切换必然生效）。
    /// </summary>
    private void OnRunsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (listBox.InputHitTest(e.GetPosition(listBox)) is DependencyObject hit &&
            ItemsControl.ContainerFromElement(listBox, hit) is ListBoxItem item &&
            item.DataContext is CompletedRunViewModel run)
        {
            listBox.SelectedItem = run;
            _viewModel.SelectedCompletedRun = run;
            _ = RenderReportAsync(run.RunId);
        }
    }

    /// <summary>
    /// 用户点击左侧存档列表时，显式把选中项写入视图模型并刷新报告。
    /// （不依赖 TwoWay 绑定写回时序，保证右侧报告联动刷新。）
    /// </summary>
    private void OnRunsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        File.AppendAllText(
            RunReportLogPath(),
            $"{DateTime.Now:HH:mm:ss} SelectionChanged 触发\n");
        if (sender is System.Windows.Controls.ListBox listBox &&
            listBox.SelectedItem is CompletedRunViewModel run)
        {
            _viewModel.SelectedCompletedRun = run;
            _ = RenderReportAsync(run.RunId);
        }
    }

    private static string RunReportLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CurrencyWarsSmartRaccoon",
            "cwt-report.log");

    private string? _renderingRunId;

    /// <summary>
    /// 调用 gen_report.py（与 HTML 报告原型同一套逻辑）生成报告并渲染。
    /// 同一存档的并发触发（SelectionChanged + PreviewMouseDown）只渲染一次。
    /// </summary>
    internal async Task RenderReportAsync(string runId)
    {
        if (_renderingRunId == runId)
        {
            return; // 去重：同一存档的重复触发直接跳过
        }

        _renderingRunId = runId;
        var logPath = RunReportLogPath();
        try
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} 开始渲染存档 {runId}\n");
            // 立即显示加载反馈（覆盖 WebView2，避免首次初始化/生成期间空白）。
            ReportLoading.Visibility = Visibility.Visible;
            // WebView2 未初始化时先初始化（否则 NavigateToString/Source 抛异常）。
            if (ReportBrowser.CoreWebView2 is null)
            {
                await ReportBrowser.EnsureCoreWebView2Async();
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} WebView2 已初始化\n");
            }

            ReportBrowser.NavigateToString(
                "<div style='color:#91a3b4;font-family:Microsoft YaHei UI;padding:24px'>正在生成对局报告…</div>");
            var htmlPath = await ReportHtmlRenderer.GenerateAsync(runId);
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} HTML 生成完成: {(htmlPath is null ? "失败(null)" : htmlPath)}\n");
            if (htmlPath is not null)
            {
                ReportBrowser.Source = new Uri(htmlPath);
                ReportLoading.Visibility = Visibility.Collapsed;
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} 已设置 Source\n");
            }
            else
            {
                ReportLoading.Visibility = Visibility.Collapsed;
                ReportBrowser.NavigateToString(
                    "<p style='color:#e06060;font-family:Microsoft YaHei UI;padding:24px'>报告生成失败（需本机安装 Python）</p>");
            }
        }
        catch (Exception exception)
        {
            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} 异常: {exception.Message}\n");
            ReportLoading.Visibility = Visibility.Collapsed;
            ReportBrowser.NavigateToString(
                $"<p style='color:#e06060;font-family:Microsoft YaHei UI;padding:24px'>报告渲染异常：{exception.Message}</p>");
        }
        finally
        {
            _renderingRunId = null;
        }
    }
}
