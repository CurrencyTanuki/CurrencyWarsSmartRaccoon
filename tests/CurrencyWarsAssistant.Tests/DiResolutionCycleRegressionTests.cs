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
/// DI 解析环回归守卫（2026-09-08 值守班）：1f61037（09-07 13:05）把
/// Func&lt;nint,CancellationToken,Task&lt;bool&gt;&gt; 注册为容器单例工厂后，生产形状出现
/// "IRunAbandoner → recovery(注入该 Func) → 工厂解析 RewardStageAutomationController
/// → 其构造参数 IAbandonSettlementRecovery → 又是 recovery → 再注入同一 Func" 的跨工厂环，
/// 解析在启动线程挂死——13:58/14:01/14:04/14:06/14:10 五个实例 jsonl 全部 0 字节（闪屏永不关闭）。
/// 本测试用与生产同形的环（真实 recovery/controller 构造器 + 同型委托单例工厂）验证解析必须终止。
/// </summary>
public sealed class DiResolutionCycleRegressionTests
{
    [Fact]
    public void IRunAbandoner_Resolution_WithCloseShopSingletonFactory_Terminates()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskEventSink>(new NullTaskEventSink());
        services.AddSingleton<IGameCapture>(new StubCapture());
        services.AddSingleton<IGamePageClassifier>(new StubClassifier());
        services.AddSingleton<IInputController>(new StubInput());
        services.AddSingleton<IGameForegroundGuard>(new StubForegroundGuard());
        services.AddSingleton<ICurrencyWarsOpeningNavigator>(new StubNavigator());
        // 生产形状：controller 为 Transient，其 IAbandonSettlementRecovery 参数=普通 Transient recovery。
        services.AddTransient(sp => new RewardStageAutomationController(
            null!, null!, null!, null!, null!, null!, null!, null!, null!,
            sp.GetRequiredService<IAbandonSettlementRecovery>(),
            sp.GetRequiredService<ITaskEventSink>(),
            recognitionFeed: null));
        services.AddTransient<IAbandonSettlementRecovery, CurrencyWarsRejectedOpeningRecovery>();
        services.AddTransient<IRejectedOpeningRecovery, CurrencyWarsRejectedOpeningRecovery>();
        services.AddTransient<IRunAbandoner, CurrencyWarsRejectedOpeningRecovery>();
        // 1f61037 的形状：Func 单例工厂，工厂体内解析 controller（环的入口）。
        services.AddSingleton<Func<nint, CancellationToken, Task<bool>>>(sp =>
        {
            var rewardStage = sp.GetRequiredService<RewardStageAutomationController>();
            return (handle, token) =>
                rewardStage.CloseShopAsync(handle, "preparation_generic", token);
        });

        using var provider = services.BuildServiceProvider();
        var stopwatch = Stopwatch.StartNew();
        var abandoner = provider.GetRequiredService<IRunAbandoner>();
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"IRunAbandoner 解析耗时 {stopwatch.Elapsed}——DI 工厂环回归（启动挂死形状）");
        Assert.NotNull(abandoner);
    }

    private sealed class StubCapture : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<CaptureFrame>(new InvalidOperationException("repro stub"));
    }

    private sealed class StubClassifier : IGamePageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame) => null;
    }

    private sealed class StubInput : IInputController
    {
        public Task<ActionResult> ClickAsync(
            ClickTarget target, ActionPolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("repro stub"));

        public Task<ActionResult> DragAsync(
            ClickTarget source, PixelPoint targetClientPoint, TimeSpan duration,
            ActionPolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("repro stub"));

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window, InputKey key, ActionPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("repro stub"));

        public Task<ActionResult> ClickWithModifierAsync(
            ClickTarget target, InputKey modifier, ActionPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Failure("repro stub"));
    }

    private sealed class StubForegroundGuard : IGameForegroundGuard
    {
        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle, CancellationToken cancellationToken) =>
            Task.FromException<GameWindowInfo>(new InvalidOperationException("repro stub"));

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo window, CancellationToken cancellationToken) =>
            Task.FromException<GameWindowInfo>(new InvalidOperationException("repro stub"));
    }

    private sealed class StubNavigator : ICurrencyWarsOpeningNavigator
    {
        public Task<CurrencyWarsNavigationResult> RunAsync(
            nint windowHandle, CurrencyWarsNavigationOptions options,
            CancellationToken cancellationToken) =>
            Task.FromException<CurrencyWarsNavigationResult>(
                new InvalidOperationException("repro stub"));
    }
}
