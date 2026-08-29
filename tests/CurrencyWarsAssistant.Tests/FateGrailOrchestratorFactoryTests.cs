using System;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>编排器组装工厂单测。</summary>
public sealed class FateGrailOrchestratorFactoryTests
{
    [Fact]
    public void Create_NullRewardStageController_Throws()
    {
        // 依赖真实 RewardStageAutomationController（sealed + DI），无法轻量构造实例；
        // guard 经 FateGrailRealShopRunner.For 抛出 ArgumentNullException。
        Assert.Throws<ArgumentNullException>(() =>
            FateGrailOrchestratorFactory.Create(null!, default));
    }
}
