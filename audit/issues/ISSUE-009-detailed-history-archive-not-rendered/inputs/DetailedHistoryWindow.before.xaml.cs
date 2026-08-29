using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CurrencyWarsAssistant.App;

public partial class DetailedHistoryWindow : Window
{
    private readonly MainViewModel _viewModel;

    public DetailedHistoryWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            // 实时对局报告：显示当前正在记录的对局（已打节点）；
            // 未记录时显示提示。节点随对局推进累积（备战开始固化上一节点），
            // 定时刷新让报告跟随进度。
            if (viewModel.DetailedHistoryNodes.Count == 0)
            {
                await viewModel.RefreshCompletedRunsAsync();
                viewModel.LoadLatestArchiveIntoDetailedHistory();
            }

            await RenderRealtimeReportAsync();
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            _refreshTimer.Tick += async (_, _) => await RenderRealtimeReportAsync();
            _refreshTimer.Start();
        };
    }

    private DispatcherTimer? _refreshTimer;

    private async Task RenderRealtimeReportAsync()
    {
        try
        {
            if (ReportBrowser.CoreWebView2 is null)
            {
                await ReportBrowser.EnsureCoreWebView2Async();
            }

            var directory = RealTimeReportBuilder.BuildReportDirectory(_viewModel);
            if (directory is null)
            {
                ReportLoading.Visibility = Visibility.Visible;
                ReportLoading.Text = "未在记录对局——开启实时记录后，此处实时显示对局报告";
                return;
            }

            ReportLoading.Visibility = Visibility.Visible;
            ReportLoading.Text = "正在生成实时对局报告…";
            var runId = Path.GetFileName(directory);
            var runsParent = Path.GetDirectoryName(directory);
            var outHtml = Path.Combine(Path.GetTempPath(), "cwt-realtime-report.html");
            var htmlPath = await ReportHtmlRenderer.GenerateFromAsync(
                runsParent ?? string.Empty,
                runId,
                outHtml);
            if (htmlPath is not null)
            {
                ReportBrowser.Source = new Uri(htmlPath);
                ReportLoading.Visibility = Visibility.Collapsed;
            }
            else
            {
                ReportLoading.Text = "实时报告生成失败（需本机安装 Python）";
            }
        }
        catch (Exception)
        {
            ReportLoading.Text = "实时报告加载异常";
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnCompletedRunsClick(object sender, RoutedEventArgs e)
    {
        await ((MainViewModel)DataContext).RefreshCompletedRunsAsync();
        new CompletedRunsWindow((MainViewModel)DataContext)
        {
            Owner = this
        }.Show();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }
}
