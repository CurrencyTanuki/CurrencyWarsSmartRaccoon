using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CurrencyWarsAssistant.App;

public partial class DetailedHistoryWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DetailedHistoryArchiveRenderState _archiveRenderState = new();
    private readonly DetailedHistoryRealtimeRenderState _realtimeRenderState = new();
    private bool _isClosed;

    public DetailedHistoryWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _refreshTimer.Tick += async (_, _) => await RenderRealtimeReportAsync();
        Loaded += async (_, _) =>
        {
            // 实时对局报告：显示当前正在记录的对局（已打节点）；
            // 未记录时显示提示。节点随对局推进累积（备战开始固化上一节点），
            // 定时刷新让报告跟随进度。
            await viewModel.RefreshIncompleteRunsAsync();
            await viewModel.RefreshCompletedRunsAsync();
            if (_isClosed)
            {
                return;
            }

            if (viewModel.DetailedHistoryNodes.Count == 0)
            {
                viewModel.LoadLatestArchiveIntoDetailedHistory();
            }

            await RenderRealtimeReportAsync();
            if (_isClosed)
            {
                return;
            }

            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _archiveRenderState.Reset();
            _realtimeRenderState.Reset();
            _refreshTimer.Stop();
        };
    }

    private async Task RenderRealtimeReportAsync()
    {
        if (_isClosed)
        {
            return;
        }

        string? inFlightRealtimeSignature = null;
        try
        {
            if (ReportBrowser.CoreWebView2 is null)
            {
                // 弹性初始化：损坏/跨内核版本不兼容的旧 user data 目录自动改名重建
                //（2026-08-10 修复 0x800700AA）。失败时由外层 catch 提示用户。
                var (initialized, webViewFailure) = await WebView2Initializer.EnsureInitializedAsync(
                    ReportBrowser);
                if (!initialized)
                {
                    throw new InvalidOperationException(
                        "WebView2 初始化失败（已尝试自动修复，仍无法启动内置报告组件）",
                        webViewFailure);
                }
            }

            if (_isClosed)
            {
                return;
            }

            var hasRealtimeActivity =
                _viewModel.IsRunning ||
                _viewModel.IsPassiveCollectionRunning;
            var realtimeDirectory = RealTimeReportBuilder.BuildReportDirectory(_viewModel);
            var source = DetailedHistoryReportSourceSelector.Select(
                hasRealtimeActivity,
                realtimeDirectory,
                _viewModel.CompletedRuns.Select(run => run.RunId).ToArray());

            if (source.Kind is DetailedHistoryReportSourceKind.None or
                DetailedHistoryReportSourceKind.WaitingForRealtime)
            {
                _archiveRenderState.Reset();
                _realtimeRenderState.Reset();
                ReportBrowser.Visibility = Visibility.Collapsed;
                ReportLoading.Visibility = Visibility.Visible;
                ReportLoading.Text = source.Kind ==
                    DetailedHistoryReportSourceKind.WaitingForRealtime
                    ? "正在记录对局——完成首个战斗节点后显示实时报告"
                    : "没有可显示的实时对局或历史存档";
                return;
            }

            string? htmlPath;
            if (source is
                {
                    Kind: DetailedHistoryReportSourceKind.Realtime,
                    Identifier: { } directory
                })
            {
                _archiveRenderState.Reset();
                var signature = await DetailedHistoryRealtimeRenderState
                    .ComputeContentSignatureAsync(directory);
                if (signature is null ||
                    !_realtimeRenderState.TryBegin(signature))
                {
                    return;
                }

                inFlightRealtimeSignature = signature;
                if (ReportBrowser.Visibility != Visibility.Visible)
                {
                    ReportLoading.Visibility = Visibility.Visible;
                    ReportLoading.Text = "正在生成实时对局报告…";
                }

                var runId = Path.GetFileName(directory);
                var runsParent = Path.GetDirectoryName(directory);
                var outHtml = Path.Combine(
                    Path.GetTempPath(),
                    "cwt-realtime-report.html");
                htmlPath = await ReportHtmlRenderer.GenerateFromAsync(
                    runsParent ?? string.Empty,
                    runId,
                    outHtml);
                if (!_realtimeRenderState.Complete(
                        signature,
                        htmlPath is not null && !_isClosed))
                {
                    return;
                }

                inFlightRealtimeSignature = null;
            }
            else if (source is
                {
                    Kind: DetailedHistoryReportSourceKind.Archive,
                    Identifier: { } archiveRunId
                })
            {
                _realtimeRenderState.Reset();
                if (!_archiveRenderState.TryBegin(archiveRunId))
                {
                    return;
                }

                ReportBrowser.Visibility = Visibility.Collapsed;
                ReportLoading.Visibility = Visibility.Visible;
                ReportLoading.Text = "正在生成最近保存的对局报告…";
                try
                {
                    htmlPath = await ReportHtmlRenderer.GenerateAsync(archiveRunId);
                    if (!_archiveRenderState.Complete(
                            archiveRunId,
                            htmlPath is not null && !_isClosed))
                    {
                        return;
                    }
                }
                catch
                {
                    _ = _archiveRenderState.Complete(
                        archiveRunId,
                        succeeded: false);
                    throw;
                }
            }
            else
            {
                return;
            }

            if (_isClosed)
            {
                return;
            }

            if (htmlPath is not null)
            {
                var sourceUri = new Uri(htmlPath);
                if (ReportBrowser.Source == sourceUri &&
                    ReportBrowser.CoreWebView2 is not null)
                {
                    ReportBrowser.CoreWebView2.Reload();
                }
                else
                {
                    ReportBrowser.Source = sourceUri;
                }

                ReportBrowser.Visibility = Visibility.Visible;
                ReportLoading.Visibility = Visibility.Collapsed;
            }
            else
            {
                ReportLoading.Text = "对局报告生成失败（内置报告组件缺失或损坏）";
            }
        }
        catch (Exception)
        {
            if (inFlightRealtimeSignature is not null)
            {
                _ = _realtimeRenderState.Complete(
                    inFlightRealtimeSignature,
                    succeeded: false);
            }

            _archiveRenderState.Reset();
            if (!_isClosed)
            {
                ReportBrowser.Visibility = Visibility.Collapsed;
                ReportLoading.Visibility = Visibility.Visible;
                ReportLoading.Text = "对局报告加载异常";
            }
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

internal enum DetailedHistoryReportSourceKind
{
    None,
    WaitingForRealtime,
    Realtime,
    Archive
}

internal readonly record struct DetailedHistoryReportSource(
    DetailedHistoryReportSourceKind Kind,
    string? Identifier);

internal static class DetailedHistoryReportSourceSelector
{
    public static DetailedHistoryReportSource Select(
        bool hasRealtimeActivity,
        string? realtimeDirectory,
        IReadOnlyList<string> archiveRunIds)
    {
        ArgumentNullException.ThrowIfNull(archiveRunIds);

        // The in-memory current run remains the freshest source after the user
        // pauses collection. Prefer it whenever it can produce a report; do
        // not fall back to an older completed archive merely because capture
        // has stopped. A process restart has no in-memory entries, so normal
        // archive selection still applies.
        if (!string.IsNullOrWhiteSpace(realtimeDirectory))
        {
            return new(DetailedHistoryReportSourceKind.Realtime, realtimeDirectory);
        }

        if (hasRealtimeActivity)
        {
            return new(DetailedHistoryReportSourceKind.WaitingForRealtime, null);
        }

        foreach (var runId in archiveRunIds)
        {
            if (!string.IsNullOrWhiteSpace(runId))
            {
                return new(DetailedHistoryReportSourceKind.Archive, runId);
            }
        }

        return new(DetailedHistoryReportSourceKind.None, null);
    }

    public static bool ShouldRenderArchive(
        string? renderedArchiveRunId,
        string candidateArchiveRunId) =>
        !string.Equals(
            renderedArchiveRunId,
            candidateArchiveRunId,
            StringComparison.Ordinal);
}

internal sealed class DetailedHistoryArchiveRenderState
{
    private string? _renderedRunId;
    private string? _inFlightRunId;

    public bool TryBegin(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_inFlightRunId is not null ||
            !DetailedHistoryReportSourceSelector.ShouldRenderArchive(
                _renderedRunId,
                runId))
        {
            return false;
        }

        _inFlightRunId = runId;
        return true;
    }

    public bool Complete(string runId, bool succeeded)
    {
        if (!string.Equals(_inFlightRunId, runId, StringComparison.Ordinal))
        {
            return false;
        }

        _inFlightRunId = null;
        if (succeeded)
        {
            _renderedRunId = runId;
        }

        return true;
    }

    public void Reset()
    {
        _renderedRunId = null;
        _inFlightRunId = null;
    }
}

internal sealed class DetailedHistoryRealtimeRenderState
{
    private string? _renderedSignature;
    private string? _inFlightSignature;

    public bool TryBegin(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        if (_inFlightSignature is not null ||
            string.Equals(
                _renderedSignature,
                signature,
                StringComparison.Ordinal))
        {
            return false;
        }

        _inFlightSignature = signature;
        return true;
    }

    public bool Complete(string signature, bool succeeded)
    {
        if (!string.Equals(
                _inFlightSignature,
                signature,
                StringComparison.Ordinal))
        {
            return false;
        }

        _inFlightSignature = null;
        if (succeeded)
        {
            _renderedSignature = signature;
        }

        return true;
    }

    public void Reset()
    {
        _renderedSignature = null;
        _inFlightSignature = null;
    }

    public static async Task<string?> ComputeContentSignatureAsync(
        string reportDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        var reportPath = Path.Combine(
            reportDirectory,
            "completed-run.v1.json");
        if (!File.Exists(reportPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            reportPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}
