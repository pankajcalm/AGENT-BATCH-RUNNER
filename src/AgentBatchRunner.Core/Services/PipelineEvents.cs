using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public enum PipelineEventKind
{
    FolderScanned,
    QueuePlanned,
    FileSelected,
    ExecutionStarted,
    ExecutionCompleted,
    ReviewGenerated,
    ReviewStarted,
    ReviewCompleted,
    NextRecommended,
    ManualStatusChanged,
    EligibilityChanged,
    StartFromSelected,
    PipelinePaused,
    PipelineStopped,
    PipelineCompleted
}

public sealed class PipelineEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public PipelineEventKind Kind { get; set; }

    public string PipelineRunId { get; set; } = string.Empty;

    public string? PipelineFileId { get; set; }

    public string Message { get; set; } = string.Empty;

    public PipelineRunStatus? PipelineStatus { get; set; }

    public PipelineFileStatus? FileStatus { get; set; }

    public string? Path { get; set; }
}

public interface IPipelineEventSink
{
    Task OnPipelineEventAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken);
}

public sealed class NullPipelineEventSink : IPipelineEventSink
{
    public static NullPipelineEventSink Instance { get; } = new();

    private NullPipelineEventSink()
    {
    }

    public Task OnPipelineEventAsync(PipelineEvent pipelineEvent, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
