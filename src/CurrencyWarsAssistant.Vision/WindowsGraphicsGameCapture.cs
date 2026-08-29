using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace CurrencyWarsAssistant.Vision;

/// <summary>
/// Captures the render surface of one window through Windows Graphics Capture.
/// Unlike a desktop BitBlt, overlapping windows and overlays are not included.
/// </summary>
public sealed class WindowsGraphicsGameCapture : IGameCapture, IDisposable
{
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const int D3dDriverTypeHardware = 1;
    private const int D3dDriverTypeWarp = 5;
    private static readonly Guid DxgiDeviceGuid =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C000");
    private static readonly Guid DxgiDevice1Guid =
        new("77DB970F-6276-48BA-BA28-070143B4392C");
    private static readonly Guid DxgiDevice2Guid =
        new("05008617-FBFD-4051-A790-144884B4F6A9");
    private static readonly Guid GraphicsCaptureItemInteropGuid =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private readonly Lazy<IDirect3DDevice> _device =
        new(CreateDirect3DDevice, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly object _frameSync = new();
    private nint _activeWindow;
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private TaskCompletionSource<Direct3D11CaptureFrame>? _pendingFrame;
    private bool _disposed;

    public async ValueTask<CaptureFrame> CaptureAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (window.ClientArea.IsEmpty)
        {
            throw new InvalidOperationException("游戏客户区尺寸无效。");
        }

        if (!window.IsReadyForAutomation)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(window.BindingMessage)
                    ? "当前窗口尚未完成游戏画面定位。"
                    : window.BindingMessage);
        }

        if (NativeWindowMethods.IsIconic(window.Handle))
        {
            throw new InvalidOperationException(
                "游戏窗口已最小化，Windows 无法取得实时渲染画面；请恢复窗口后继续。");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException(
                "当前 Windows 版本或显卡驱动不支持窗口表面捕获。");
        }

        await _captureLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await CaptureWindowAsync(window, cancellationToken);
                }
                catch (Exception) when (
                    attempt < 2 &&
                    !cancellationToken.IsCancellationRequested)
                {
                    ResetSession();
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(150),
                        cancellationToken);
                }
            }
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private async Task<CaptureFrame> CaptureWindowAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        EnsureSession(window.Handle);
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_frameSync)
        {
            if (_disposed || _session is null)
            {
                throw new ObjectDisposedException(
                    nameof(WindowsGraphicsGameCapture));
            }

            _pendingFrame = completion;
        }

        try
        {
            using var frame = await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(3),
                cancellationToken);
            return await CopyFrameAsync(window, frame, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "等待游戏窗口渲染帧超时，请确认游戏窗口没有最小化。",
                exception);
        }
        finally
        {
            lock (_frameSync)
            {
                if (ReferenceEquals(_pendingFrame, completion))
                {
                    _pendingFrame = null;
                }
            }
        }
    }

    private void EnsureSession(nint windowHandle)
    {
        if (_session is not null && _activeWindow == windowHandle)
        {
            return;
        }

        ResetSession();
        var item = CreateItemForWindow(windowHandle);
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("窗口捕获返回了无效尺寸。");
        }

        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device.Value,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
        var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        // Keep one capture session alive for the whole automation run. On
        // systems that require the yellow capture border this makes it steady
        // instead of recreating and flashing it for every recognition step.

        framePool.FrameArrived += OnFrameArrived;
        try
        {
            session.StartCapture();
        }
        catch
        {
            framePool.FrameArrived -= OnFrameArrived;
            session.Dispose();
            framePool.Dispose();
            throw;
        }

        _captureItem = item;
        _framePool = framePool;
        _session = session;
        _activeWindow = windowHandle;
    }

    private void OnFrameArrived(
        Direct3D11CaptureFramePool sender,
        object arguments)
    {
        var frame = sender.TryGetNextFrame();
        TaskCompletionSource<Direct3D11CaptureFrame>? completion;
        lock (_frameSync)
        {
            completion = _pendingFrame;
            if (completion is not null)
            {
                _pendingFrame = null;
            }
        }

        if (completion is null || !completion.TrySetResult(frame))
        {
            frame.Dispose();
        }
    }

    private void ResetSession()
    {
        TaskCompletionSource<Direct3D11CaptureFrame>? pending;
        lock (_frameSync)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }

        pending?.TrySetException(
            new InvalidOperationException("游戏窗口截图会话已重新建立。"));

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
        _captureItem = null;
        _activeWindow = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetSession();
        _captureLock.Dispose();
    }

    private static async Task<CaptureFrame> CopyFrameAsync(
        GameWindowInfo window,
        Direct3D11CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface,
            BitmapAlphaMode.Ignore);
        using var bitmap = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(source)
            : SoftwareBitmap.Convert(
                source,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore);

        var surfaceWidth = bitmap.PixelWidth;
        var surfaceHeight = bitmap.PixelHeight;
        var surfaceStride = checked(surfaceWidth * 4);
        var surfacePixels =
            new byte[checked(surfaceStride * surfaceHeight)];
        var buffer = new Windows.Storage.Streams.Buffer(
            (uint)surfacePixels.Length);
        bitmap.CopyToBuffer(buffer);
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(surfacePixels);

        if (window.HostClientAreaOverride is not { } hostArea)
        {
            return new CaptureFrame(
                surfaceWidth,
                surfaceHeight,
                surfaceStride,
                surfacePixels,
                window.ClientArea,
                DateTimeOffset.UtcNow);
        }

        var surfaceScreenArea =
            surfaceWidth == hostArea.Width &&
            surfaceHeight == hostArea.Height
                ? hostArea
                : NativeWindowMethods.GetWindowScreenRect(window.Handle) ??
                  hostArea;
        var scaleX = surfaceWidth / (double)surfaceScreenArea.Width;
        var scaleY = surfaceHeight / (double)surfaceScreenArea.Height;
        if (Math.Abs(scaleX - scaleY) > 0.01)
        {
            throw new InvalidOperationException(
                "窗口捕获表面与游戏区域的DPI比例不一致，请重新校准游戏画面。");
        }

        var cropX = (int)Math.Round(
            (window.ClientArea.X - surfaceScreenArea.X) * scaleX);
        var cropY = (int)Math.Round(
            (window.ClientArea.Y - surfaceScreenArea.Y) * scaleY);
        var cropWidth = (int)Math.Round(window.ClientArea.Width * scaleX);
        var cropHeight = (int)Math.Round(window.ClientArea.Height * scaleY);
        if (cropX < 0 ||
            cropY < 0 ||
            cropWidth <= 0 ||
            cropHeight <= 0 ||
            cropX + cropWidth > surfaceWidth ||
            cropY + cropHeight > surfaceHeight)
        {
            throw new InvalidOperationException(
                "已定位的游戏区域不在当前窗口捕获表面内，请重新校准。");
        }

        if (!GameAspectRatio.IsSixteenByNine(cropWidth, cropHeight))
        {
            throw new InvalidOperationException(
                GameAspectRatio.InvalidAspectRatioMessage);
        }

        var cropStride = checked(cropWidth * 4);
        var cropPixels = new byte[checked(cropStride * cropHeight)];
        for (var row = 0; row < cropHeight; row++)
        {
            System.Buffer.BlockCopy(
                surfacePixels,
                checked((cropY + row) * surfaceStride + cropX * 4),
                cropPixels,
                row * cropStride,
                cropStride);
        }

        return new CaptureFrame(
            cropWidth,
            cropHeight,
            cropStride,
            cropPixels,
            window.ClientArea,
            DateTimeOffset.UtcNow);
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint windowHandle)
    {
        nint className = 0;
        nint factory = 0;
        nint item = 0;
        try
        {
            var createStringResult = WindowsCreateString(
                "Windows.Graphics.Capture.GraphicsCaptureItem",
                44,
                out className);
            Marshal.ThrowExceptionForHR(createStringResult);

            var factoryGuid = GraphicsCaptureItemInteropGuid;
            var factoryResult = RoGetActivationFactory(
                className,
                ref factoryGuid,
                out factory);
            Marshal.ThrowExceptionForHR(factoryResult);

            var vtable = Marshal.ReadIntPtr(factory);
            var createForWindowPointer = Marshal.ReadIntPtr(
                vtable,
                3 * nint.Size);
            var createForWindow =
                Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(
                    createForWindowPointer);
            var itemGuid = GraphicsCaptureItemGuid;
            var createItemResult = createForWindow(
                factory,
                windowHandle,
                ref itemGuid,
                out item);
            Marshal.ThrowExceptionForHR(createItemResult);

            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0)
            {
                _ = Marshal.Release(item);
            }

            if (factory != 0)
            {
                _ = Marshal.Release(factory);
            }

            if (className != 0)
            {
                _ = WindowsDeleteString(className);
            }
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        var result = D3D11CreateDevice(
            0,
            D3dDriverTypeHardware,
            0,
            D3d11CreateDeviceBgraSupport,
            0,
            0,
            D3d11SdkVersion,
            out var d3dDevice,
            out _,
            out var deviceContext);
        if (result < 0)
        {
            result = D3D11CreateDevice(
                0,
                D3dDriverTypeWarp,
                0,
                D3d11CreateDeviceBgraSupport,
                0,
                0,
                D3d11SdkVersion,
                out d3dDevice,
                out _,
                out deviceContext);
        }

        Marshal.ThrowExceptionForHR(result);
        nint dxgiDevice = 0;
        nint direct3DDevice = 0;
        try
        {
            var queryResult = TryGetDxgiDevice(d3dDevice, out dxgiDevice);
            if (queryResult < 0)
            {
                throw new Win32Exception(
                    queryResult,
                    $"无法从 D3D11 设备取得 DXGI 设备接口（HRESULT 0x{queryResult:X8}）。");
            }

            var createResult = CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice,
                out direct3DDevice);
            Marshal.ThrowExceptionForHR(createResult);
            return MarshalInterface<IDirect3DDevice>.FromAbi(direct3DDevice);
        }
        finally
        {
            if (direct3DDevice != 0)
            {
                _ = Marshal.Release(direct3DDevice);
            }

            if (dxgiDevice != 0)
            {
                _ = Marshal.Release(dxgiDevice);
            }

            if (deviceContext != 0)
            {
                _ = Marshal.Release(deviceContext);
            }

            if (d3dDevice != 0)
            {
                _ = Marshal.Release(d3dDevice);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(
        nint factory,
        nint window,
        ref Guid interfaceId,
        out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(
        nint instance,
        ref Guid interfaceId,
        out nint result);

    private static int QueryInterface(
        nint instance,
        ref Guid interfaceId,
        out nint result)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var queryInterfacePointer = Marshal.ReadIntPtr(vtable);
        var queryInterface =
            Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                queryInterfacePointer);
        return queryInterface(instance, ref interfaceId, out result);
    }

    private static int TryGetDxgiDevice(
        nint d3dDevice,
        out nint dxgiDevice)
    {
        foreach (var candidate in new[]
                 {
                     DxgiDeviceGuid,
                     DxgiDevice1Guid,
                     DxgiDevice2Guid
                 })
        {
            var interfaceId = candidate;
            var result = QueryInterface(
                d3dDevice,
                ref interfaceId,
                out dxgiDevice);
            if (result >= 0)
            {
                return result;
            }
        }

        dxgiDevice = 0;
        return unchecked((int)0x80004002);
    }

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        int length,
        out nint value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint value);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        ref Guid interfaceId,
        out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);
}
