using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Agents;

public sealed class DryRunAgentAdapter(ConsoleLogger logger) : IAgentAdapter
{
    public string Name => "dryrun";

    public Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        logger.Info($"[dryrun] Would execute prompt {request.PromptId}, attempt {request.AttemptNumber}.");
        var output =
            $"""
            Dry run agent execution.
            Prompt ID: {request.PromptId}
            Attempt: {request.AttemptNumber}
            Session: {(request.SessionId ?? "(fresh)")}

            Prompt:
            {request.Prompt}
            """;

        return Task.FromResult(new AgentExecutionResult
        {
            AgentName = Name,
            Command = "dryrun",
            ExitCode = 0,
            Duration = TimeSpan.Zero,
            StandardOutput = SensitiveDataRedactor.Redact(output),
            StandardError = string.Empty,
            SessionId = request.SessionId ?? $"dryrun-{request.PromptId}"
        });
    }
}
