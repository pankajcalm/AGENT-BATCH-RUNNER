namespace AgentBatchRunner.Agents;

public sealed class AgentExecutionRequest
{
    public string RepoPath { get; set; } = string.Empty;

    public string PromptId { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public int AttemptNumber { get; set; }

    /// <summary>
    /// Explicitly controls whether this invocation resumes the current agent's session. Null keeps
    /// the legacy adapter behavior for callers that only provide an attempt number.
    /// </summary>
    public bool? ResumeSession { get; set; }

    public bool ShouldResumeSession => ResumeSession ?? AttemptNumber > 1;

    public string? SessionId { get; set; }

    public string AttemptDirectory { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public AgentInvocationOptions Options { get; set; } = new();
}
