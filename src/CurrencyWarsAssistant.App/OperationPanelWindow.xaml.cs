using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CurrencyWarsAssistant.App;

public partial class OperationPanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _topmostTimer;
    private HwndSource? _source;
    private nint _handle;
    private OverlayHitTargetWindow? _toggleTarget;
    private OverlayHitTargetWindow? _detailsTarget;
    private DetailedHistoryWindow? _detailedHistoryWindow;
    private bool _chartAutoScrollPending;
    private string? _lastAutoScrolledNodeId;

    public OperationPanelWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _topmostTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(750),
            DispatcherPriority.Background,
            (_, _) => EnsureTopmost(),
            Dispatcher);
        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnOverlayLayoutChanged;
        SizeChanged += OnOverlayLayoutChanged;
        IsVisibleChanged += OnOverlayVisibilityChanged;
        ClickThroughToggle.LayoutUpdated += OnToggleLayoutUpdated;
        DetailedHistoryButton.LayoutUpdated += OnDetailsButtonLayoutUpdated;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.DashboardRows.CollectionChanged += OnDashboardRowsChanged;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessageHook);
        var styles = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(
            _handle,
            GwlExStyle,
            new nint(styles | WsExNoActivate));
        _toggleTarget = new OverlayHitTargetWindow(() =>
            _viewModel.IsLogOverlayClickThrough = false);
        _detailsTarget = new OverlayHitTargetWindow(ShowDetailedHistory);
        ApplyClickThrough();
        EnsureTopmost();
        _topmostTimer.Start();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLogOverlayClickThrough))
        {
            ApplyClickThrough();
        }
    }

    private void OnChartMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer ||
            viewer.ExtentWidth <= viewer.ViewportWidth)
        {
            return;
        }

        viewer.ScrollToHorizontalOffset(
            Math.Clamp(
                viewer.HorizontalOffset - e.Delta,
                0,
                viewer.ScrollableWidth));
        e.Handled = true;
    }

    private void OnDashboardRowsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (_chartAutoScrollPending)
        {
            return;
        }

        _chartAutoScrollPending = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _chartAutoScrollPending = false;
                var latestNodeId = _viewModel.DashboardRows.LastOrDefault()?.NodeId;
                if (string.IsNullOrWhiteSpace(latestNodeId) ||
                    string.Equals(
                        latestNodeId,
                        _lastAutoScrolledNodeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _lastAutoScrolledNodeId = latestNodeId;
                DamageChartScroller.ScrollToRightEnd();
                TheoryChartScroller.ScrollToRightEnd();
                GoldChartScroller.ScrollToRightEnd();
                ActionChartScroller.ScrollToRightEnd();
                // 新节点同步时表格自动滚动到底部，直接展示最新节点数据。
                TableScroller?.ScrollToEnd();
            });
    }

    private void OnDetailedHistoryClick(object sender, RoutedEventArgs e) =>
        ShowDetailedHistory();

    private void ShowDetailedHistory()
    {
        if (_detailedHistoryWindow is { IsLoaded: true })
        {
            _detailedHistoryWindow.Show();
            _detailedHistoryWindow.Activate();
            return;
        }

        _detailedHistoryWindow = new DetailedHistoryWindow(_viewModel)
        {
            Owner = this
        };
        _toggleTarget?.Hide();
        _detailsTarget?.Hide();
        _detailedHistoryWindow.Closed += (_, _) =>
        {
            _detailedHistoryWindow = null;
            PositionToggleTarget();
        };
        _detailedHistoryWindow.Show();
        _detailedHistoryWindow.Activate();
    }

    private nint WindowMessageHook(
        nint handle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message != WmNcHitTest)
        {
            return 0;
        }

        var screenPoint = GetScreenPoint(longParameter);
        if (ContainsScreenPoint(ClickThroughToggle, screenPoint))
        {
            handled = true;
            return HtClient;
        }

        if (ContainsScreenPoint(DetailedHistoryButton, screenPoint))
        {
            handled = true;
            return HtClient;
        }

        if (_viewModel.IsLogOverlayClickThrough)
        {
            handled = true;
            return HtTransparent;
        }

        if (ContainsScreenPoint(DragHandle, screenPoint))
        {
            handled = true;
            return HtCaption;
        }

        return 0;
    }

    public void EnsureTopmost()
    {
        if (_handle == 0)
        {
            return;
        }

        _ = SetWindowPos(
            _handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
        _toggleTarget?.EnsureTopmost();
        _detailsTarget?.EnsureTopmost();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _topmostTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.DashboardRows.CollectionChanged -= OnDashboardRowsChanged;
        _source?.RemoveHook(WindowMessageHook);
        _toggleTarget?.Close();
        _toggleTarget = null;
        _detailsTarget?.Close();
        _detailsTarget = null;
        _detailedHistoryWindow?.Close();
        _detailedHistoryWindow = null;
    }

    private void ApplyClickThrough()
    {
        if (_handle == 0)
        {
            return;
        }

        var styles = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        styles = _viewModel.IsLogOverlayClickThrough
            ? styles | WsExTransparent
            : styles & ~WsExTransparent;
        _ = SetWindowLongPtr(
            _handle,
            GwlExStyle,
            new nint(styles | WsExNoActivate));
        PositionToggleTarget();
    }

    private void OnOverlayLayoutChanged(object? sender, EventArgs e) =>
        PositionToggleTarget();

    private void OnToggleLayoutUpdated(object? sender, EventArgs e) =>
        PositionToggleTarget();

    private void OnDetailsButtonLayoutUpdated(object? sender, EventArgs e) =>
        PositionToggleTarget();

    private void OnOverlayVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        PositionToggleTarget();
        // 窗口重新显示后按钮布局可能尚未完成（尺寸为 0 导致覆盖层
        // 隐藏、按钮点击穿透），延迟重试一次保证覆盖层定位到位。
        // Background 优先级在渲染完成后执行，此时按钮布局已就绪。
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => PositionToggleTarget());
    }

    private void PositionToggleTarget()
    {
        if (IsVisible &&
            _viewModel.IsLogOverlayClickThrough &&
            _detailedHistoryWindow is not { IsVisible: true })
        {
            _toggleTarget?.PositionOver(ClickThroughToggle);
            _detailsTarget?.PositionOver(DetailedHistoryButton);
        }
        else
        {
            _toggleTarget?.Hide();
            _detailsTarget?.Hide();
        }
    }

    private static bool ContainsScreenPoint(
        FrameworkElement element,
        Point screenPoint)
    {
        var local = element.PointFromScreen(screenPoint);
        return local.X >= 0 &&
               local.Y >= 0 &&
               local.X <= element.ActualWidth &&
               local.Y <= element.ActualHeight;
    }

    private static Point GetScreenPoint(nint parameter)
    {
        var packed = parameter.ToInt64();
        return new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint window,
        int index,
        nint newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
