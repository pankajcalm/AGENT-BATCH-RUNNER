using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface IBatchExecutionRunner
{
    Task<RunResult> RunAsync(
        BatchConfig config,
        RunOptions options,
        CancellationToken cancellationToken = default);
}
