using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class OpeningNavigationRetryPolicyTests
{
    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(6, 1, true)]
    [InlineData(5, 1, false)]
    public void StopsOnRepeatedOrAlternatingFailureBudget(
        int attempts,
        int repeatedFingerprintCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpeningNavigationRetryPolicy.IsExhausted(
                attempts,
                repeatedFingerprintCount));
    }
}
