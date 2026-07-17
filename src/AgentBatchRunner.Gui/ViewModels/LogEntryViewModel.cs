namespace AgentBatchRunner.Gui.ViewModels;

public sealed class LogEntryViewModel(DateTimeOffset timestamp, string level, string message)
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public string Level { get; } = level;

    public string Message { get; } = message;

    public string DisplayText => $"[{Timestamp:HH:mm:ss}] {Level} {Message}";
}
