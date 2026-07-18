namespace AgentBatchRunner.Models;

public enum AgentRoutingReason
{
    Yaml,
    Default,
    GlobalOverride,
    RateLimitFallback,
    ManualPendingOverride
}

public sealed class AgentRoutingDecision
{
    public string PromptId { get; init; } = string.Empty;

    public string BaseAgent { get; init; } = string.Empty;

    public string EffectiveAgent { get; init; } = string.Empty;

    public AgentRoutingReason RoutingReason { get; init; }
}

public sealed class AgentRoutingCandidate
{
    public string PromptId { get; init; } = string.Empty;

    public string BaseAgent { get; init; } = string.Empty;

    public AgentRoutingReason BaseRoutingReason { get; init; }
}

public sealed class AgentSwitchRequest
{
    public string SourceAgent { get; init; } = string.Empty;

    public string ReplacementAgent { get; init; } = string.Empty;

    public AgentRoutingReason Reason { get; init; }

    public bool IsAutomatic { get; init; }

    public string? StartingPromptId { get; init; }

    public List<string> AffectedPromptIds { get; init; } = [];

    public DateTimeOffset? RateLimitResetAt { get; init; }

    public bool UserConfirmed { get; init; }

    public bool RetryRateLimitedTask { get; init; }
}

public sealed class AgentRoutingChange
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public string SourceAgent { get; set; } = string.Empty;

    public string ReplacementAgent { get; set; } = string.Empty;

    public AgentRoutingReason Reason { get; set; }

    public bool IsAutomatic { get; set; }

    public string StartingPromptId { get; set; } = string.Empty;

    public List<string> AffectedPromptIds { get; set; } = [];

    public DateTimeOffset? RateLimitResetAt { get; set; }

    public bool UserConfirmed { get; set; }

    public bool RetryRateLimitedTask { get; set; }
}

public sealed class RunRoutingSnapshot
{
    public string RunId { get; set; } = string.Empty;

    public List<AgentRoutingChange> Changes { get; set; } = [];
}
