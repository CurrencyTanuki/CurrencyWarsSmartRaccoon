using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

internal sealed class Phase2PostCompletionBoundaryDetector(
    int confirmationFrames = 2)
{
    private readonly int _confirmationFrames = confirmationFrames >= 2
        ? confirmationFrames
        : throw new ArgumentOutOfRangeException(nameof(confirmationFrames));
    private int _matchingFrames;

    public bool Observe(Phase2OperationalState? state)
    {
        if (state is null || state.PageFamily is Phase2PageFamily.Unknown or
            Phase2PageFamily.Transition)
        {
            return false;
        }

        var isReliablePreparation11 =
            state.PageFamily == Phase2PageFamily.Preparation &&
            state.NodeId.Status == ObservationStatus.Known &&
            state.NodeId.Confidence >= 0.65 &&
            string.Equals(
                state.NodeId.Value,
                "1-1",
                StringComparison.OrdinalIgnoreCase);
        if (!isReliablePreparation11)
        {
            _matchingFrames = 0;
            return false;
        }

        _matchingFrames++;
        return _matchingFrames >= _confirmationFrames;
    }

    public void Reset() => _matchingFrames = 0;
}
