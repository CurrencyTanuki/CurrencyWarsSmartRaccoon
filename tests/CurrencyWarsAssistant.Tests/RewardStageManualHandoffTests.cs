using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardStageManualHandoffTests
{
    [Fact]
    public async Task NoStrategyConditionStopsBeforeStrategyRecognitionOrInput()
    {
        var events = new RecordingEventSink();
        var controller = new RewardStageAutomationController(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            events);

        var result = await controller.CompleteAfterSecondRewardStageAsync(
            123,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Equal(
            RewardStageAutomationStatus
                .RewardStagesCompletedAwaitingManualStrategy,
            result.Status);
        Assert.True(result.Succeeded);
        Assert.False(result.ShouldReroll);
        Assert.Contains(
            events.Events,
            item => item.Code ==
                "RewardStagesCompletedForManualContinuation");
    }

    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<TaskEvent> Events { get; } = [];

        public void Publish(TaskEvent taskEvent) => Events.Add(taskEvent);
    }
}
