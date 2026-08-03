using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Workflow;

public enum Phase1WorkflowState
{
    Idle,
    Starting,
    Running,
    Stopping,
    Completed,
    Cancelled,
    Failed
}

public sealed record Phase1WorkflowStatus(
    Phase1WorkflowState State,
    string Message,
    DateTimeOffset ChangedAt);

public interface IPhase1AutomationService
{
    Phase1WorkflowStatus Status { get; }
    event EventHandler<Phase1WorkflowStatus>? StatusChanged;
    event EventHandler<OpeningRerollLoopProgress>? OpeningProgressChanged;

    Task<OpeningRerollLoopResult> RunAsync(
        Phase1RunConfiguration configuration,
        CancellationToken cancellationToken);
}

public interface IOpeningRerollProgressSource
{
    event EventHandler<OpeningRerollLoopProgress>? ProgressChanged;
}

public interface IOpeningRerollRunner
{
    Task<OpeningRerollLoopResult> RunAsync(
        nint windowHandle,
        CurrencyWarsAssistant.Game.OpeningFilterSet filters,
        OpeningRerollLoopOptions options,
        CancellationToken cancellationToken);
}

public sealed class OpeningRerollRunnerAdapter(
    OpeningRerollLoopCoordinator coordinator) :
    IOpeningRerollRunner,
    IOpeningRerollProgressSource
{
    public event EventHandler<OpeningRerollLoopProgress>? ProgressChanged
    {
        add => coordinator.ProgressChanged += value;
        remove => coordinator.ProgressChanged -= value;
    }

    public Task<OpeningRerollLoopResult> RunAsync(
        nint windowHandle,
        CurrencyWarsAssistant.Game.OpeningFilterSet filters,
        OpeningRerollLoopOptions options,
        CancellationToken cancellationToken) =>
        coordinator.RunAsync(
            windowHandle,
            filters,
            options,
            cancellationToken);
}

/// <summary>
/// Application boundary for phase-one automation. It owns run exclusivity and
/// lifecycle state; recognition, input, and game-specific actions remain behind
/// the task-layer ports.
/// </summary>
public sealed class Phase1AutomationService(
    IOpeningRerollRunner runner) : IPhase1AutomationService
{
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public Phase1WorkflowStatus Status { get; private set; } = new(
        Phase1WorkflowState.Idle,
        "Ready",
        DateTimeOffset.Now);

    public event EventHandler<Phase1WorkflowStatus>? StatusChanged;

    public event EventHandler<OpeningRerollLoopProgress>? OpeningProgressChanged
    {
        add
        {
            if (runner is IOpeningRerollProgressSource source)
            {
                source.ProgressChanged += value;
            }
        }
        remove
        {
            if (runner is IOpeningRerollProgressSource source)
            {
                source.ProgressChanged -= value;
            }
        }
    }

    public async Task<OpeningRerollLoopResult> RunAsync(
        Phase1RunConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A phase-one automation run is already active.");
        }

        try
        {
            Change(Phase1WorkflowState.Starting, "Run configuration frozen.");
            Change(Phase1WorkflowState.Running, "Phase-one workflow running.");
            var result = await runner.RunAsync(
                    configuration.WindowHandle,
                    configuration.Filters,
                    configuration.Options,
                    cancellationToken)
                .ConfigureAwait(false);
            Change(
                result.Succeeded
                    ? Phase1WorkflowState.Completed
                    : Phase1WorkflowState.Failed,
                result.Message);
            return result;
        }
        catch (OperationCanceledException)
        {
            Change(Phase1WorkflowState.Cancelled, "Run cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            Change(Phase1WorkflowState.Failed, exception.Message);
            throw;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private void Change(Phase1WorkflowState state, string message)
    {
        Status = new Phase1WorkflowStatus(state, message, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, Status);
    }
}
