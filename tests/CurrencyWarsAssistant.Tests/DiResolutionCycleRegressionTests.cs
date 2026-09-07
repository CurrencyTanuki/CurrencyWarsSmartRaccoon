using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// DI 解析死锁回归守卫（2026-09-08 值守班）：1f61037 曾把
/// Func&lt;nint,CancellationToken,Task&lt;bool&gt;&gt; 注册为容器单例工厂，与
/// "IRunAbandoner → recovery(注入 Func) → 工厂解析 RewardStageAutomationController
/// → 其 IAbandonSettlementRecovery → recovery → 再注入同一 Func" 构成解析重入死锁，
/// 指令测试台启动线程永久卡死（09-07 13:58~14:10 五实例 jsonl 0 字节；复现测试
/// testhost 挂死 519 秒 CPU 冻结）。修复后生产形状=逐接口显式工厂（无 Func 单例注册），
/// 本测试镜像该形状，断言弃局器解析必须在有界时间内完成——防死锁形状回归。
/// </summary>
public sealed class DiResolutionCycleRegressionTests
{
    [Fact]
    public async Task IRunAbandoner_Resolution_WithPerInterfaceFactories_Terminates()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskEventSink>(new NullTaskEventSink());
        services.AddSingleton<IGameCapture>(new StubCapture());
        services.AddSingleton<IGamePageClassifier>(new StubClassifier());
        services.AddSingleton<IInputController>(new StubInput());
        services.AddSingleton<IGameForegroundGuard>(new StubForegroundGuard());
        services.AddSingleton<ICurrencyWarsOpeningNavigator>(new StubNavigator());
        services.AddSingleton<IWishTrialPopupHandler>(new StubWishPopupHandler());
        // 生产形状：controller 为 Transient，其 IAbandonSettlementRecovery=普通 Transient recovery。
        // 具名参数（复核 P3-2）：防止未来插入新参数时静默错位。
        services.AddTransient(sp => new RewardStageAutomationController(
            capture: null!,
            pageClassifier: null!,
            shopReader: null!,
            shopPurchasePlanner: null!,
            strategyReader: null!,
            visualDetector: null!,
            input: null!,
            foregroundGuard: null!,
            preparationCompletionController: null!,
            settlementRecovery: sp.GetRequiredService<IAbandonSettlementRecovery>(),
            eventSink: sp.GetRequiredService<ITaskEventSink>(),
            recognitionFeed: null));
        services.AddTransient<IAbandonSettlementRecovery, CurrencyWarsRejectedOpeningRecovery>();
        // 修复后形状：逐接口显式工厂——工厂体内先解析 controller 再 new recovery，
        // 绝不注册 Func<nint,CancellationToken,Task<bool>> 单例（重入死锁根源）。
        services.AddTransient<IRejectedOpeningRecovery>(sp =>
            BuildRecovery(sp));
        services.AddTransient<IRunAbandoner>(sp => BuildRecovery(sp));

        using var provider = services.BuildServiceProvider();
        var stopwatch = Stopwatch.StartNew();
        // 复核 P2-1：断言必须包住阻塞解析本身——若断言放在解析之后，
        // 死锁回归形态下解析永不返回，断言成为死代码（靠外部超时才能暴露）。
        var resolveTask = Task.Run(() => provider.GetRequiredService<IRunAbandoner>());
        var completed = await Task.WhenAny(resolveTask, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(
            completed == resolveTask,
            $"IRunAbandoner 解析 30 秒未返回——DI 工厂环回归（启动挂死形状）");
        var abandoner = await resolveTask;
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"IRunAbandoner 解析耗时 {stopwatch.Elapsed}——DI 工厂环回归");
        Assert.NotNull(abandoner);
    }

    /// <summary>与 App.xaml.cs 修复后工厂同构的 recovery 构造（含关店闭包）。</summary>
    private static CurrencyWarsRejectedOpeningRecovery BuildRecovery(
        IServiceProvider provider)
    {
        var rewardStage = provider.GetRequiredService<RewardStageAutomationController>();
        return new CurrencyWarsRejectedOpeningRecovery(
            provider.GetRequiredService<ICurrencyWarsOpeningNavigator>(),
            provider.GetRequiredService<IGameCapture>(),
            provider.GetRequiredService<IGamePageClassifier>(),
            provider.GetRequiredService<IInputController>(),
            provider.GetRequiredService<IGameForegroundGuard>(),
            provider.GetRequiredService<ITaskEventSink>(),
            wishTrialHandler: provider.GetRequiredService<IWishTrialPopupHandler>(),
            closeShopIfOpen: async (handle, token) =>
                await rewardStage.CloseShopAsync(handle, "preparation_generic", token),
            selectLeftmostStrategyIfUp: null);
    }

    private sealed class StubWishPopupHandler : IWishTrialPopupHandler
    {
        public Task<bool> DismissWishTrialPopupIfUpAsync(
            nint windowHandle, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class StubCapture : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<CaptureFrame>(new InvalidOperationException("stub"));
    }

    private sealed class StubClassifier : IGamePageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame) => null;
    }

    private sealed class StubInput : IInputController
    {
        public Task<ActionResult> ClickAsync(
            ClickTarget target, ActionPolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("stub"));

        public Task<ActionResult> DragAsync(
            ClickTarget source, PixelPoint targetClientPoint, TimeSpan duration,
            ActionPolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("stub"));

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window, InputKey key, ActionPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("stub"));

        public Task<ActionResult> ClickWithModifierAsync(
            ClickTarget target, InputKey modifier, ActionPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("stub"));
    }

    private sealed class StubForegroundGuard : IGameForegroundGuard
    {
        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle, CancellationToken cancellationToken) =>
            Task.FromException<GameWindowInfo>(new InvalidOperationException("stub"));

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo window, CancellationToken cancellationToken) =>
            Task.FromException<GameWindowInfo>(new InvalidOperationException("stub"));
    }

    private sealed class StubNavigator : ICurrencyWarsOpeningNavigator
    {
        public Task<CurrencyWarsNavigationResult> RunAsync(
            nint windowHandle, CurrencyWarsNavigationOptions options,
            CancellationToken cancellationToken) =>
            Task.FromException<CurrencyWarsNavigationResult>(
                new InvalidOperationException("stub"));
    }
}
