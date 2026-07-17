namespace AgentBatchRunner.Agents;

public sealed class AgentExecutionRequest
{
    public string RepoPath { get; set; } = string.Empty;

    public string PromptId { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public int AttemptNumber { get; set; }

    public string? SessionId { get; set; }

    public string AttemptDirectory { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public AgentInvocationOptions Options { get; set; } = new();
}
