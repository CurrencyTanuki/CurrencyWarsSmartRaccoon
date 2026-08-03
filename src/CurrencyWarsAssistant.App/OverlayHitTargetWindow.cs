using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// A tiny input-only window placed exactly over one visible overlay control.
/// The large visual overlay can therefore use WS_EX_TRANSPARENT without
/// enlarging the interactive hotspot.
/// </summary>
internal sealed class OverlayHitTargetWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);
    private readonly Action _onClick;

    public OverlayHitTargetWindow(Action onClick, Cursor? cursor = null)
    {
        _onClick = onClick;
        Width = 1;
        Height = 1;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Cursor = cursor ?? Cursors.Hand;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    public void PositionOver(FrameworkElement element)
    {
        if (!element.IsVisible ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            Hide();
            return;
        }

        var source = PresentationSource.FromVisual(element);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var devicePoint = element.PointToScreen(new Point(0, 0));
        var dipPoint = source.CompositionTarget.TransformFromDevice
            .Transform(devicePoint);
        Left = dipPoint.X;
        Top = dipPoint.Y;
        Width = element.ActualWidth;
        Height = element.ActualHeight;
        if (!IsVisible)
        {
            Show();
        }
    }

    public void EnsureTopmost()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        _onClick();
    }

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
