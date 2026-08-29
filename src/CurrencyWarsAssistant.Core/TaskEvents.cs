namespace CurrencyWarsAssistant.Core;

public enum TaskEventLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public sealed record TaskEvent(
    DateTimeOffset Timestamp,
    TaskEventLevel Level,
    string Code,
    string Message);

public interface ITaskEventSink
{
    void Publish(TaskEvent taskEvent);
}

public sealed class NullTaskEventSink : ITaskEventSink
{
    public void Publish(TaskEvent taskEvent)
    {
    }
}
