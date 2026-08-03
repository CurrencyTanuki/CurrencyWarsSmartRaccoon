using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.App;

public partial class GameAreaCalibrationWindow : Window
{
    private const double MaximumPreviewWidth = 1020;
    private const double MaximumPreviewHeight = 610;
    private readonly GameWindowInfo _hostWindow;
    private readonly MainViewModel _viewModel;
    private readonly GdiGameCapture _capture = new();
    private Point? _selectionOrigin;
    private Rect? _selection;

    public GameAreaCalibrationWindow(
        GameWindowInfo hostWindow,
        MainViewModel viewModel)
    {
        InitializeComponent();
        _hostWindow = hostWindow;
        _viewModel = viewModel;
    }

    public async Task LoadPreviewAsync()
    {
        try
        {
            var frame = await _capture.CaptureAsync(
                _hostWindow,
                CancellationToken.None);
            PreviewImage.Source = frame.ToBitmapSource();
            var scale = Math.Min(
                MaximumPreviewWidth / frame.Width,
                MaximumPreviewHeight / frame.Height);
            scale = Math.Min(1, scale);
            var width = Math.Max(1, frame.Width * scale);
            var height = Math.Max(1, frame.Height * scale);
            PreviewSurface.Width = width;
            PreviewSurface.Height = height;
            PreviewImage.Width = width;
            PreviewImage.Height = height;
            SelectionCanvas.Width = width;
            SelectionCanvas.Height = height;
            SetInitialSelection(width, height);
        }
        catch (Exception exception)
        {
            SelectionStatus.Text = $"窗口预览失败：{exception.Message}";
            ConfirmButton.IsEnabled = false;
        }
    }

    private async void OnReloadPreview(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.BringWindowToForeground(_hostWindow);
        await Task.Delay(250);
        await LoadPreviewAsync();
    }

    private void OnSelectionStarted(
        object sender,
        MouseButtonEventArgs e)
    {
        _selectionOrigin = Clamp(e.GetPosition(SelectionCanvas));
        SelectionCanvas.CaptureMouse();
        UpdateSelection(_selectionOrigin.Value, _selectionOrigin.Value);
        e.Handled = true;
    }

    private void OnSelectionMoved(object sender, MouseEventArgs e)
    {
        if (_selectionOrigin is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelection(
            _selectionOrigin.Value,
            Clamp(e.GetPosition(SelectionCanvas)));
    }

    private void OnSelectionCompleted(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_selectionOrigin is not null)
        {
            UpdateSelection(
                _selectionOrigin.Value,
                Clamp(e.GetPosition(SelectionCanvas)));
        }

        _selectionOrigin = null;
        SelectionCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SetInitialSelection(double width, double height)
    {
        var selectionWidth = Math.Min(width, height * 16d / 9d);
        var selectionHeight = selectionWidth * 9d / 16d;
        if (selectionHeight > height)
        {
            selectionHeight = height;
            selectionWidth = selectionHeight * 16d / 9d;
        }

        var left = (width - selectionWidth) / 2;
        var top = height - selectionHeight;
        SetSelection(new Rect(
            left,
            Math.Max(0, top),
            selectionWidth,
            selectionHeight));
    }

    private void UpdateSelection(Point origin, Point current)
    {
        var canvasWidth = GetCanvasWidth();
        var canvasHeight = GetCanvasHeight();
        var left = Math.Min(origin.X, current.X);
        var top = Math.Min(origin.Y, current.Y);
        var availableWidth = canvasWidth - left;
        var availableHeight = canvasHeight - top;
        var requestedWidth = Math.Abs(current.X - origin.X);
        var requestedHeight = Math.Abs(current.Y - origin.Y);
        var width = Math.Max(
            requestedWidth,
            requestedHeight * 16d / 9d);
        var height = width * 9d / 16d;
        if (width > availableWidth)
        {
            width = availableWidth;
            height = width * 9d / 16d;
        }

        if (height > availableHeight)
        {
            height = availableHeight;
            width = height * 16d / 9d;
        }

        SetSelection(new Rect(left, top, width, height));
    }

    private void SetSelection(Rect selection)
    {
        var canvasWidth = GetCanvasWidth();
        var canvasHeight = GetCanvasHeight();
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            ConfirmButton.IsEnabled = false;
            return;
        }

        _selection = selection;
        SelectionBox.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(SelectionBox, selection.X);
        System.Windows.Controls.Canvas.SetTop(SelectionBox, selection.Y);
        SelectionBox.Width = selection.Width;
        SelectionBox.Height = selection.Height;

        var pixelWidth = (int)Math.Round(
            selection.Width / canvasWidth *
            _hostWindow.ClientArea.Width);
        var pixelHeight = (int)Math.Round(pixelWidth * 9d / 16d);
        ConfirmButton.IsEnabled =
            pixelWidth >= 320 &&
            pixelHeight >= 180 &&
            GameAspectRatio.IsSixteenByNine(pixelWidth, pixelHeight);
        SelectionStatus.Text =
            $"选择区域：{pixelWidth}×{pixelHeight} · " +
            (ConfirmButton.IsEnabled ? "16:9，可确认" : "区域过小");
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var canvasWidth = GetCanvasWidth();
        var canvasHeight = GetCanvasHeight();
        if (_selection is not { } selection ||
            canvasWidth <= 0 ||
            canvasHeight <= 0)
        {
            return;
        }

        var host = _hostWindow.ClientArea;
        var x = (int)Math.Round(
            selection.X / canvasWidth * host.Width);
        var y = (int)Math.Round(
            selection.Y / canvasHeight * host.Height);
        var width = (int)Math.Round(
            selection.Width / canvasWidth * host.Width);
        var height = (int)Math.Round(width * 9d / 16d);
        x = Math.Clamp(x, 0, Math.Max(0, host.Width - width));
        y = Math.Clamp(y, 0, Math.Max(0, host.Height - height));
        var screenArea = new PixelRect(
            host.X + x,
            host.Y + y,
            width,
            height);
        var binding = _viewModel.BindGameArea(
            _hostWindow.Handle,
            screenArea,
            _hostWindow.SourceKind);
        if (binding?.IsReadyForAutomation == true)
        {
            DialogResult = true;
            return;
        }

        SelectionStatus.Text =
            binding?.BindingMessage ??
            "窗口已失效，请刷新窗口列表后重试。";
    }

    private double GetCanvasWidth() =>
        SelectionCanvas.ActualWidth > 0
            ? SelectionCanvas.ActualWidth
            : SelectionCanvas.Width;

    private double GetCanvasHeight() =>
        SelectionCanvas.ActualHeight > 0
            ? SelectionCanvas.ActualHeight
            : SelectionCanvas.Height;

    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, GetCanvasWidth()),
        Math.Clamp(point.Y, 0, GetCanvasHeight()));
}
