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
    // P1（2026-09-10 关游戏卡死修复）：捕获目标销毁快路径——Closed 事件置位后
    // CaptureAsync 入口毫秒级失败，不再让 3s 等帧超时+对半死目标 ResetSession 的
    // 同步 Dispose 走到 WinRT 楔死窗口（该楔死曾致进程级冻结，根因诊断见
    // docs/AUDIT_20260910_123_WATCH.md 关联 handoff 〇-7.12）。
    private int _targetClosed;
    // 会话代际（对抗审查 P1-1）：每次 EnsureSession 新建 +1；Closed 回调闭包捕获
    // 自己的代际，迟发/孤儿回调凭代际失配被忽略，绝不给重建后的新会话下毒。
    private int _generation;
    // 供 ResetSession 尽力退订的最近一次 Closed 订阅（WinRT 事件要求具体委托类型）。
    private Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object>?
        _closedHandler;
    // 1.2.96 识别流冻结根因诊断（纯观测）：捕获层计数——与管线层截图循环统计对照，
    // 区分"WGC/游戏不产帧""等帧超时""会话反复重建"三种病理。
    private long _frameArrivals;
    private long _captureSuccesses;
    private long _captureTimeouts;
    private long _sessionCreations;
    private long _sessionRebuilds;
    private long _lastFrameArrivedTicks;
    private long _lastCaptureSuccessTicks;
    private long _rebuildMarker;

    public CaptureStreamStats? StreamStats => new(
        Interlocked.Read(ref _frameArrivals),
        Interlocked.Read(ref _captureSuccesses),
        Interlocked.Read(ref _captureTimeouts),
        Interlocked.Read(ref _sessionCreations),
        Interlocked.Read(ref _sessionRebuilds),
        TicksToTime(Volatile.Read(ref _lastFrameArrivedTicks)),
        TicksToTime(Volatile.Read(ref _lastCaptureSuccessTicks)));

    private static DateTimeOffset? TicksToTime(long ticks) => ticks == 0
        ? null
        : new DateTimeOffset(DateTimeOffset.UtcNow.Ticks - (Environment.TickCount64 - ticks) * TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    public async ValueTask<CaptureFrame> CaptureAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // P1-1（对抗审查）：快失败仅对"请求窗口==当前会话目标"生效——游戏重启后
        // 新 HWND 放行进入 EnsureSession→ResetSession 清标志重建，捕获不再永久锁死。
        if (Volatile.Read(ref _targetClosed) == 1 &&
            Interlocked.CompareExchange(ref _activeWindow, 0, 0) == window.Handle)
        {
            throw new InvalidOperationException(
                "游戏窗口已关闭，捕获目标不再存在。");
        }

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
                    var frame = await CaptureWindowAsync(window, cancellationToken);
                    Interlocked.Increment(ref _captureSuccesses);
                    Volatile.Write(ref _lastCaptureSuccessTicks, Environment.TickCount64);
                    return frame;
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
            Interlocked.Increment(ref _captureTimeouts);
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
        var generation = Interlocked.Increment(ref _generation);
        var item = CreateItemForWindow(windowHandle);
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("窗口捕获返回了无效尺寸。");
        }

        // P1（2026-09-10）：订阅目标销毁事件——窗口关闭即刻置快路径标志并唤醒
        // 在途等待，采集循环不再对死目标走"3s 超时→ResetSession"的楔死窗口。
        // 闭包捕获代际：迟发/孤儿 Closed 回调凭代际失配被忽略，不下毒新会话
        // （对抗审查 P1-1 竞态变体）。handler 存字段供 ResetSession 尽力退订。
        Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object> handler =
            (_, _) => OnCaptureItemClosed(generation);
        item.Closed += handler;
        _closedHandler = handler;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device.Value,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            // Keep one capture session alive for the whole automation run. On
            // systems that require the yellow capture border this makes it steady
            // instead of recreating and flashing it for every recognition step.

            framePool.FrameArrived += OnFrameArrived;
            session.StartCapture();
        }
        catch
        {
            // P2-2/P3-1（对抗审查）：创建段任何一步失败——退订+后台释放，
            // 绝不在调用线程同步 Dispose（与根因同型楔死窗口）。
            if (framePool is not null)
            {
                framePool.FrameArrived -= OnFrameArrived;
            }

            item.Closed -= handler;
            BackgroundDispose(framePool, session);
            throw;
        }

        _captureItem = item;
        _framePool = framePool;
        _session = session;
        _activeWindow = windowHandle;
        Interlocked.Increment(ref _sessionCreations);
        if (Volatile.Read(ref _rebuildMarker) == 1)
        {
            Interlocked.Increment(ref _sessionRebuilds);
            Volatile.Write(ref _rebuildMarker, 0);
        }
    }

    private void OnFrameArrived(
        Direct3D11CaptureFramePool sender,
        object arguments)
    {
        // P3-2（对抗审查）：free-threaded 回调与后台 Dispose 并发是设计内状态，
        // TryGetNextFrame 撞上并发销毁会在 WinRT 回调内抛出→fail-fast 风险。
        // 帧已丢由上层 3s 超时/快路径兜底，此处必须吞异常。
        try
        {
            Interlocked.Increment(ref _frameArrivals);
            Volatile.Write(ref _lastFrameArrivedTicks, Environment.TickCount64);
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
        catch
        {
        }
    }

    private void OnCaptureItemClosed(int generation)
    {
        // 代际门控（对抗审查 P1-1 竞态变体）：迟发/孤儿回调不属于当前会话代际时
        // 直接忽略——绝不给重建后的健康新会话下毒。
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        Volatile.Write(ref _targetClosed, 1);
        TaskCompletionSource<Direct3D11CaptureFrame>? completion;
        lock (_frameSync)
        {
            completion = _pendingFrame;
            _pendingFrame = null;
        }

        // 立刻唤醒在途等帧：让 CaptureWindowAsync 的 await 处毫秒级失败，
        // 不再消耗 3s 超时（超时路径会触发对半死目标的 ResetSession）。
        completion?.TrySetException(new InvalidOperationException(
            "游戏窗口已关闭，捕获目标不再存在。"));
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

        // P1-1（对抗审查）：复位点必须在 ResetSession——入口快失败只对
        // "请求窗口==当前会话目标"生效（见 CaptureAsync），新 HWND 放行后经此
        // 自然清标志重建，游戏重启后捕获不再永久锁死。
        Volatile.Write(ref _targetClosed, 0);

        var item = _captureItem;
        var session = _session;
        var framePool = _framePool;
        var handler = _closedHandler;
        _closedHandler = null;
        _session = null;
        _framePool = null;
        _captureItem = null;
        _activeWindow = 0;

        if (item is not null && handler is not null)
        {
            try
            {
                item.Closed -= handler;
            }
            catch
            {
                // 目标已销毁时退订可能失败——忽略（代际门控兜底迟发回调）。
            }
        }

        // 释放了活会话=下一次 EnsureSession 的创建是"重建"（1.2.96 诊断计数）。
        if (session is not null)
        {
            Volatile.Write(ref _rebuildMarker, 1);
        }

        // P1（2026-09-10 关游戏卡死根因修复）：**绝不在调用线程同步 Dispose**——
        // 对"目标已销毁"的 WGC 会话/帧池同步 Dispose 会等待在途 WinRT 回调而楔死
        // 持有 _captureLock 的生产者线程（进程级冻结，OS AppHangB1 04:37:07 实锤）。
        // 改为后台 Dispose 一次性放弃：楔死时泄漏一个后台线程+WinRT 对象
        // （有界、优于挂死）；正常路径后台 Dispose 照常完成。
        BackgroundDispose(framePool, session);
    }

    private void BackgroundDispose(
        Direct3D11CaptureFramePool? framePool,
        GraphicsCaptureSession? session)
    {
        if (framePool is null && session is null)
        {
            return;
        }

        if (framePool is not null)
        {
            framePool.FrameArrived -= OnFrameArrived;
        }

        _ = Task.Run(() =>
        {
            try
            {
                framePool?.Dispose();
                session?.Dispose();
            }
            catch
            {
                // 后台释放失败无可恢复动作；不再向采集线程抛。
            }
        });
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
