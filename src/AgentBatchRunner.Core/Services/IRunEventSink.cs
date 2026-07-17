namespace AgentBatchRunner.Services;

public interface IRunEventSink
{
    Task OnRunEventAsync(RunEvent runEvent, CancellationToken cancellationToken);
}
