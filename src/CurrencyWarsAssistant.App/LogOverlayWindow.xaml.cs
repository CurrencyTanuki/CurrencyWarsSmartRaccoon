using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;

namespace CurrencyWarsAssistant.App;

public partial class LogOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);
    private readonly DispatcherTimer _topmostTimer;
    private nint _handle;
    private OverlayHitTargetWindow? _dragTarget;

    public LogOverlayWindow(MainViewModel viewModel)
    {
        InitializeComponent();
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
        DragGrip.LayoutUpdated += OnDragGripLayoutUpdated;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(
            _handle,
            GwlExStyle,
            new nint(styles | WsExNoActivate | WsExTransparent));
        _dragTarget = new OverlayHitTargetWindow(BeginDrag, Cursors.SizeAll);
        PositionDragTarget();
        EnsureTopmost();
        _topmostTimer.Start();
    }

    private void BeginDrag()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button may have been released before WPF entered its move
            // loop. Keeping the current position is the safe fallback.
        }
        finally
        {
            PositionDragTarget();
        }
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
        _dragTarget?.EnsureTopmost();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _topmostTimer.Stop();
        _dragTarget?.Close();
        _dragTarget = null;
    }

    private void OnOverlayLayoutChanged(object? sender, EventArgs e) =>
        PositionDragTarget();

    private void OnDragGripLayoutUpdated(object? sender, EventArgs e) =>
        PositionDragTarget();

    private void OnOverlayVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            PositionDragTarget();
            if (!_topmostTimer.IsEnabled)
            {
                _topmostTimer.Start();
            }
        }
        else
        {
            _dragTarget?.Hide();
            _topmostTimer.Stop();
        }
    }

    private void PositionDragTarget()
    {
        if (IsVisible)
        {
            _dragTarget?.PositionOver(DragGrip);
        }
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
