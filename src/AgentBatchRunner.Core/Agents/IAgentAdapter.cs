namespace AgentBatchRunner.Agents;

public interface IAgentAdapter
{
    string Name { get; }

    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken);
}
