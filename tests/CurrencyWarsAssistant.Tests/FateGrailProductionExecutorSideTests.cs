using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」生产执行器的纯函数测试（不依赖真实点屏组件）。</summary>
public sealed class FateGrailProductionExecutorSideTests
{
    [Fact]
    public void ToSelectSide_Left_ReturnsZero()
    {
        Assert.Equal(0, FateGrailProductionExecutor.ToSelectSide(
            FateGrailRunEngine.TrialSide.Left));
    }

    [Fact]
    public void ToSelectSide_Right_ReturnsOne()
    {
        Assert.Equal(1, FateGrailProductionExecutor.ToSelectSide(
            FateGrailRunEngine.TrialSide.Right));
    }
}