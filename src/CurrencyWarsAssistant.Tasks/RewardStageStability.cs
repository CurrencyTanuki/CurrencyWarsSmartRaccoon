namespace CurrencyWarsAssistant.Tasks;

internal sealed class ConsecutiveObservationTracker<T>(
    int requiredCount,
    IEqualityComparer<T>? comparer = null)
    where T : notnull
{
    private readonly IEqualityComparer<T> _comparer =
        comparer ?? EqualityComparer<T>.Default;
    private T? _previous;
    private bool _hasPrevious;
    private int _count;

    public bool Observe(T value)
    {
        _count = _hasPrevious && _comparer.Equals(_previous!, value)
            ? _count + 1
            : 1;
        _previous = value;
        _hasPrevious = true;
        return _count >= requiredCount;
    }

    public void Reset()
    {
        _previous = default;
        _hasPrevious = false;
        _count = 0;
    }
}
