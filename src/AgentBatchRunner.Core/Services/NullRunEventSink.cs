namespace AgentBatchRunner.Services;

public sealed class NullRunEventSink : IRunEventSink
{
    public static NullRunEventSink Instance { get; } = new();

    private NullRunEventSink()
    {
    }

    public Task OnRunEventAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
