using System.IO;
using System.Text;
using System.Text.Json;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.App;

public sealed class UiTaskEventSink : ITaskEventSink, IDisposable
{
    private readonly object _writeLock = new();
    private readonly StreamWriter _writer;
    private readonly UserLogPolicy _userLogPolicy = new();

    public UiTaskEventSink() : this(logDirectoryOverride: null)
    {
    }

    internal UiTaskEventSink(string? logDirectoryOverride)
    {
        var configuredDirectory =
            Environment.GetEnvironmentVariable("CURRENCY_WARS_LOG_DIRECTORY");
        var logDirectory = !string.IsNullOrWhiteSpace(logDirectoryOverride)
            ? Path.GetFullPath(logDirectoryOverride)
            : string.IsNullOrWhiteSpace(configuredDirectory)
                ? GetDefaultLogDirectory()
                : Path.GetFullPath(configuredDirectory);
        Directory.CreateDirectory(logDirectory);
        LogFilePath = Path.Combine(
            logDirectory,
            $"test-session-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
        _writer = new StreamWriter(
            new FileStream(
                LogFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public string LogFilePath { get; }

    public bool DiagnosticLoggingEnabled { get; set; } = true;

    public event EventHandler<TaskEvent>? EventPublished;

    public void Publish(TaskEvent taskEvent)
    {
        var userVisible = _userLogPolicy.ShouldPublish(taskEvent);
        if (DiagnosticLoggingEnabled ||
            userVisible ||
            taskEvent.Level is TaskEventLevel.Warning or TaskEventLevel.Error)
        {
            lock (_writeLock)
            {
                _writer.WriteLine(JsonSerializer.Serialize(taskEvent));
            }
        }

        if (userVisible)
        {
            EventPublished?.Invoke(this, taskEvent);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer.Dispose();
        }
    }

    private static string GetDefaultLogDirectory()
    {
        var developmentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        return File.Exists(Path.Combine(developmentRoot, "CurrencyWarsAssistant.sln"))
            ? Path.Combine(developmentRoot, "logs")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.UserDataDirectoryName,
                "logs");
    }
}
