using System;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>真实商店执行器适配器单测。</summary>
public sealed class FateGrailRealShopRunnerTests
{
    [Fact]
    public void For_NullController_ThrowsArgumentNullException()
    {
        // 委托与 RewardStageAutomationController.RunShopRefreshPurchaseLoopAsync 的
        // 签名对齐已由 dotnet build 0 错（编译期）验证；此处锁定异常参数。
        // 注：windowHandle==0 守卫位于 controller 非 null 之后，需真实 controller 才可触发，
        // 而 controller 为 sealed+DI 无法轻量构造，故 windowHandle 守卫仅靠代码审查确认。
        var ex = Assert.Throws<ArgumentNullException>(() =>
            FateGrailRealShopRunner.For(null!, 1234));
        Assert.Equal("controller", ex.ParamName);
    }
}
