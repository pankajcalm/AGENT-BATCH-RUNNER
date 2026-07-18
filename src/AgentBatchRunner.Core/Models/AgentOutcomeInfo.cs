namespace AgentBatchRunner.Models;

public enum AgentOutcome
{
    Unspecified,
    Succeeded,
    Blocked,
    NeedsHumanDecision,
    PrerequisiteMissing,
    Failed,
    RateLimited,
    Canceled,
    TimedOut
}

public sealed class AgentOutcomeInfo
{
    public AgentOutcome AgentOutcome { get; set; }

    public string? BlockerCode { get; set; }

    public string? Blocker { get; set; }

    public string? RecommendedNext { get; set; }

    public string RawMessage { get; set; } = string.Empty;

    public bool StopsWithoutRetry => AgentOutcome is
        AgentOutcome.Blocked or
        AgentOutcome.NeedsHumanDecision or
        AgentOutcome.PrerequisiteMissing or
        AgentOutcome.RateLimited or
        AgentOutcome.Canceled;
}
